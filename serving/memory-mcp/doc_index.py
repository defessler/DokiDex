# doc_index.py — DokiDex knowledge-base RAG: "chat with your documents" over a per-conversation doc set.
#
# The prose twin of code_index.py. A conversation ATTACHES plain-text docs (.txt/.md, first slice); each doc is
# chunked into bounded overlapping CHAR windows (prose, not source-lines), embedded via the SAME local embed
# server (llama.cpp --embedding, OpenAI /v1/embeddings on :8090), and stored in its OWN disposable sqlite DB
# (doc_index.db) — separate from the precious memory.db AND from code_index.db, so a doc reindex can never harm
# either. Rows are SCOPED BY kb_id (the conversation id), so retrieval only ever sees the thread's own docs.
# Nearest-neighbour queries are answered by brute-force cosine — at single-doc scale (a few hundred chunks)
# sub-100ms, pure stdlib, NO native vector extension (sqlite-vec).
#
# Pure + injectable: chunk_text / _cosine / _pack are dependency-free and the embed call is a single injectable
# function, so the whole pipeline is unit-tested with a stub embedder (no GPU, no network) by
# tests/doc_index.test.py. The live embed HTTP call (code_index.embed, reused verbatim) is the only part that
# needs the embed server running — when it's down, the C# DocSearch sidecar degrades to "no context injected"
# and plain chat proceeds unchanged (the same contract code_search has).
#
# _pack / _unpack / _cosine / embed / _embed_input are imported VERBATIM from code_index — they are already pure
# and dim-agnostic (struct-pack <Nf at whatever length comes back; search_vec skips dim-mismatched rows), so a
# model change can't silently mis-score, and the KB inherits that for free.

import json
import math
import os
import sqlite3
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from code_index import (  # noqa: E402  — reuse the proven, pure, dim-agnostic primitives verbatim
    MAX_EMBED_CHARS,
    EMBED_BATCH_CHARS,
    _cosine,
    _pack,
    _unpack,
    _embed_input,
    embed,
)

DB_PATH = os.environ.get("DOC_INDEX_DB", os.path.join(os.path.dirname(os.path.abspath(__file__)), "doc_index.db"))

# Prose chunking: bounded overlapping CHAR windows. ~1500 chars (~375 tokens, well under nomic's 2048 cap even
# after the source-name prefix) with ~200 overlap so a sentence straddling a boundary is findable from either
# side. CHUNK_SIZE stays <= MAX_EMBED_CHARS so a single window never trips the per-input truncation.
CHUNK_SIZE = 1500
CHUNK_OVERLAP = 200

# Hard caps so a pathological paste/upload can't emit thousands of sequential :8090 embed calls (each ~tens of
# ms) and blow past the C# sidecar's 30s timeout (which would surface a MISLEADING "start the embed server"
# 503 — the server is fine, the doc is just too big). The C# DocSearch.ValidateIngest is the primary gate
# (it returns a clear "document too large" message to the UI BEFORE spawning python); these are the defensive
# backstop so even a direct `uv run python doc_index.py doc_ingest ...` call stays bounded. MAX_CHUNKS at the
# default 1500/200 stride covers ~260k chars — comfortably above MAX_DOC_CHARS, so the C# cap trips first.
MAX_DOC_CHARS = 200_000
MAX_CHUNKS = 200


# --- chunking (pure) ------------------------------------------------------------------------------------
def chunk_text(text, size=CHUNK_SIZE, overlap=CHUNK_OVERLAP, max_chunks=MAX_CHUNKS):
    """Split prose into bounded overlapping char windows: [(ord, chunk), ...] (0-based ordinal).
    Overlap keeps a sentence that straddles a window boundary findable from either side. All-blank
    windows are dropped. A text shorter than `size` yields a single chunk. At most `max_chunks` windows are
    emitted (the tail of an over-large body is dropped) so ingest can never fan out into an unbounded number
    of embed calls — the defensive backstop to the C# size gate."""
    if not text or not text.strip():
        return []
    step = max(1, size - overlap)
    out = []
    i = 0
    ordinal = 0
    n = len(text)
    while i < n:
        if ordinal >= max_chunks:
            break
        window = text[i:i + size]
        if window.strip():
            out.append((ordinal, window))
            ordinal += 1
        i += step
    return out


# --- store ----------------------------------------------------------------------------------------------
def _conn():
    c = sqlite3.connect(DB_PATH)
    c.execute("CREATE TABLE IF NOT EXISTS doc_chunks "
              "(id INTEGER PRIMARY KEY, kb_id TEXT, source TEXT, ord INT, content TEXT, vec BLOB, ts REAL)")
    c.execute("CREATE INDEX IF NOT EXISTS ix_doc_kb ON doc_chunks(kb_id)")
    c.commit()
    return c


def reset(kb_id=None):
    """Drop a single KB's chunks (kb_id given) or the whole index (None) — a clean re-ingest."""
    c = _conn()
    try:
        if kb_id is None:
            c.execute("DELETE FROM doc_chunks")
        else:
            c.execute("DELETE FROM doc_chunks WHERE kb_id=?", (kb_id,))
        c.commit()
    finally:
        c.close()


def count(kb_id=None):
    c = _conn()
    try:
        if kb_id is None:
            return c.execute("SELECT COUNT(*) FROM doc_chunks").fetchone()[0]
        return c.execute("SELECT COUNT(*) FROM doc_chunks WHERE kb_id=?", (kb_id,)).fetchone()[0]
    finally:
        c.close()


def _batched_by_chars(chunks, budget=EMBED_BATCH_CHARS):
    """Group (ord, content) chunks so each embed request stays under the server's token budget (chars proxy).
    Same rule as code_index._batched_by_chars, adapted to the (ord, content) chunk shape."""
    batch, size = [], 0
    for ch in chunks:
        clen = min(len(ch[1]), MAX_EMBED_CHARS)
        if batch and size + clen > budget:
            yield batch
            batch, size = [], 0
        batch.append(ch)
        size += clen
    if batch:
        yield batch


def _embed_batch(chunks, embed_fn, prefix=""):
    """[((ord, content), vec), ...] for one batch. The embed input is `prefix + content` — the source filename
    is prepended so a query naming the doc matches (exactly as code_index prefixes the path). On a server error,
    retry PER ITEM and skip any chunk the server still rejects — one pathological chunk can never abort ingest."""
    def inp(ch):
        return _embed_input(prefix + ch[1])
    try:
        vecs = embed_fn([inp(ch) for ch in chunks])
        return list(zip(chunks, vecs))
    except Exception:
        out = []
        for ch in chunks:
            try:
                out.append((ch, embed_fn([inp(ch)])[0]))
            except Exception:
                pass
        return out


def ingest_doc(kb_id, source, text, embed_fn=embed):
    """Chunk + embed + (re)store one document's text under (kb_id, source). embed_fn is injectable (stubbed in
    tests). Re-ingesting the same (kb_id, source) REPLACES that source's old chunks (no duplicate accumulation).
    Returns the number of chunks written."""
    c = _conn()
    nchunks = 0
    try:
        chunks = chunk_text(text)
        c.execute("DELETE FROM doc_chunks WHERE kb_id=? AND source=?", (kb_id, source))   # replace this source
        if not chunks:
            c.commit()
            return 0
        for batch in _batched_by_chars(chunks):
            for (ordinal, content), vec in _embed_batch(batch, embed_fn, prefix=f"{source}\n"):
                c.execute("INSERT INTO doc_chunks(kb_id,source,ord,content,vec,ts) VALUES(?,?,?,?,?,?)",
                          (kb_id, source, ordinal, content, _pack(vec), time.time()))
                nchunks += 1
        c.commit()
    finally:
        c.close()
    return nchunks


def sources(kb_id):
    """The distinct source filenames attached to a KB (for the list endpoint)."""
    c = _conn()
    try:
        rows = c.execute("SELECT source, COUNT(*) FROM doc_chunks WHERE kb_id=? GROUP BY source ORDER BY source",
                         (kb_id,)).fetchall()
    finally:
        c.close()
    return [{"source": r[0], "chunks": r[1]} for r in rows]


def remove_source(kb_id, source):
    """Detach one source from a KB. Returns the number of chunks removed."""
    c = _conn()
    try:
        cur = c.execute("DELETE FROM doc_chunks WHERE kb_id=? AND source=?", (kb_id, source))
        c.commit()
        return cur.rowcount
    finally:
        c.close()


def delete(kb_id):
    """Drop ALL of one KB's chunks (every source under kb_id) — called when the owning conversation is deleted
    so a deleted-with-docs thread's vectors don't accumulate in doc_index.db forever. Returns the number of
    rows removed (0 for an unknown kb_id — a clean no-op). Only this kb_id's rows are touched; other KBs are
    untouched. (`reset(kb_id)` exists for a clean re-ingest; `delete` is the cleanup-on-delete name + count.)"""
    c = _conn()
    try:
        cur = c.execute("DELETE FROM doc_chunks WHERE kb_id=?", (kb_id,))
        c.commit()
        return cur.rowcount
    finally:
        c.close()


# --- retrieval (pure cosine, kb-scoped) -----------------------------------------------------------------
def search_vec(kb_id, query_vec, k=5):
    """Brute-force cosine nearest neighbours over the kb_id-scoped chunks, top-K. Returns [] for a degenerate
    (zero-norm) query, and skips any stored vector whose dimension differs from the query (a mismatch from a
    different embed model would otherwise silently mis-score over a shared prefix). No code-vs-docs re-rank —
    that bias is meaningless for a pure-doc corpus."""
    qdim = len(query_vec)
    if math.sqrt(sum(x * x for x in query_vec)) == 0:
        return []
    c = _conn()
    try:
        rows = c.execute("SELECT source,ord,content,vec FROM doc_chunks WHERE kb_id=?", (kb_id,)).fetchall()
    finally:
        c.close()
    scored = []
    for r in rows:
        v = _unpack(r[3])
        if len(v) == qdim:
            scored.append((_cosine(query_vec, v), r))
    scored.sort(key=lambda t: t[0], reverse=True)
    return [{"source": r[0], "ord": r[1], "content": r[2], "score": round(s, 4)}
            for s, r in scored[:k]]


def search(kb_id, query, k=5, embed_fn=embed):
    """Embed the query, then cosine-rank the kb_id-scoped chunks."""
    qv = embed_fn([query])[0]
    return search_vec(kb_id, qv, k)


if __name__ == "__main__":
    # Subcommands the C# DocSearch sidecar shells via `uv run python doc_index.py ...` (stdlib-only, no temp file):
    #   doc_ingest KB SOURCE     — read the document TEXT from STDIN, chunk+embed+store it; prints {"chunks":N}.
    #   doc_search KB QUERY [K]  — prints a JSON array of {source,ord,content,score}.
    #   doc_sources KB           — prints a JSON array of {source,chunks}.
    #   doc_remove KB SOURCE     — prints {"removed":N}.
    #   doc_delete KB            — drop the WHOLE KB (conversation-delete cleanup); prints {"removed":N}.
    # A connection error (embed server down) / missing index propagates as a non-zero exit, which the C# caller
    # degrades to "no context injected" (plain chat proceeds unchanged) — the same contract as code_search.
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "doc_ingest":
        kb = sys.argv[2] if len(sys.argv) > 2 else ""
        src = sys.argv[3] if len(sys.argv) > 3 else ""
        text = sys.stdin.read()
        n = ingest_doc(kb, src, text)
        print(json.dumps({"chunks": n}))
        # A non-blank doc that produced ZERO stored chunks means the embed server rejected EVERY chunk (it's down /
        # OOM): exit non-zero so the C# IngestAsync surfaces a 503 ("start the embed server") instead of a
        # misleading "attached, 0 chunks". A genuinely empty/blank doc (no chunks to embed) exits 0.
        sys.exit(1 if (n == 0 and text.strip()) else 0)
    if cmd == "doc_search":
        kb = sys.argv[2] if len(sys.argv) > 2 else ""
        q = sys.argv[3] if len(sys.argv) > 3 else ""
        k = int(sys.argv[4]) if len(sys.argv) > 4 else 5
        print(json.dumps(search(kb, q, k)))
        sys.exit(0)
    if cmd == "doc_sources":
        kb = sys.argv[2] if len(sys.argv) > 2 else ""
        print(json.dumps(sources(kb)))
        sys.exit(0)
    if cmd == "doc_remove":
        kb = sys.argv[2] if len(sys.argv) > 2 else ""
        src = sys.argv[3] if len(sys.argv) > 3 else ""
        print(json.dumps({"removed": remove_source(kb, src)}))
        sys.exit(0)
    if cmd == "doc_delete":
        kb = sys.argv[2] if len(sys.argv) > 2 else ""
        print(json.dumps({"removed": delete(kb)}))
        sys.exit(0)
    print("usage: doc_index.py {doc_ingest KB SOURCE <stdin| doc_search KB Q [K] | doc_sources KB | doc_remove KB SOURCE | doc_delete KB}")
    sys.exit(2)
