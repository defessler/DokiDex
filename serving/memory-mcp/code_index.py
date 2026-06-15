# code_index.py — DokiCode codebase RAG: semantic code search over this repo.
#
# Chunks source files into overlapping line-windows, embeds each chunk via the local embed server
# (llama.cpp --embedding, OpenAI /v1/embeddings), stores the vectors in sqlite, and answers
# nearest-neighbour queries by brute-force cosine. At repo scale (a few thousand chunks) brute-force is
# sub-100ms and needs NO native vector extension (sqlite-vec) — pure stdlib. The index lives in its OWN
# disposable DB (code_index.db), separate from the precious memory.db, so a reindex can never harm memories.
#
# Pure + injectable: chunk_text / _cosine / _pack are dependency-free and the embed call is a single
# injectable function, so the whole pipeline is unit-tested with a stub embedder (no GPU, no network) by
# tests/code_index.test.py. The live embed HTTP call is the only part that needs the embed server running.

import json
import math
import os
import sqlite3
import struct
import time
import urllib.request

DB_PATH = os.environ.get("CODE_INDEX_DB", os.path.join(os.path.dirname(os.path.abspath(__file__)), "code_index.db"))
EMBED_URL = os.environ.get("EMBED_URL", "http://127.0.0.1:8090/v1/embeddings")
EMBED_MODEL = os.environ.get("EMBED_MODEL", "embed")

# nomic-embed-text caps at 2048 tokens PER INPUT; ~4 chars/token, so truncate any single chunk well under
# that, and cap TOTAL chars per embed request so it never overruns the server's token batch (n_batch).
# Keep EMBED_BATCH_CHARS <= start-embed.ps1's -b/-ub (2048 tokens) * ~4 chars/token, or longer requests
# get silently dropped server-side.
MAX_EMBED_CHARS = 4000
EMBED_BATCH_CHARS = 6000

# files worth indexing (source + docs/config); everything else is skipped by the walker.
SOURCE_EXT = {".ps1", ".psm1", ".cs", ".py", ".xaml", ".json", ".md", ".yaml", ".yml", ".bat", ".js", ".ts"}
# directory names never worth indexing (weights, build output, deps, vendored servers).
SKIP_DIRS = {".git", "bin", "obj", "node_modules", ".venv", "__pycache__", "models", "media", "tts", "stt",
             ".run", ".claude", "llama.cpp", "SwarmUI"}


# --- chunking (pure) ------------------------------------------------------------------------------------
def chunk_text(text, max_lines=60, overlap=10):
    """Split source into overlapping line windows: [(start_line, end_line, chunk), ...] (1-based, inclusive).
    Overlap keeps a definition that straddles a window boundary findable from either side. All-blank
    windows are dropped."""
    lines = text.splitlines()
    if not lines:
        return []
    step = max(1, max_lines - overlap)
    out = []
    i = 0
    n = len(lines)
    while i < n:
        window = lines[i:i + max_lines]
        if any(ln.strip() for ln in window):
            out.append((i + 1, i + len(window), "\n".join(window)))
        i += step
    return out


# --- vector packing + cosine (pure) ---------------------------------------------------------------------
def _pack(vec):
    return struct.pack(f"<{len(vec)}f", *vec)


def _unpack(blob):
    return list(struct.unpack(f"<{len(blob) // 4}f", blob))


def _cosine(a, b):
    dot = sum(x * y for x, y in zip(a, b))
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(y * y for y in b))
    return dot / (na * nb) if na and nb else 0.0


# --- store ----------------------------------------------------------------------------------------------
def _conn():
    c = sqlite3.connect(DB_PATH)
    c.execute("CREATE TABLE IF NOT EXISTS code_chunks "
              "(id INTEGER PRIMARY KEY, path TEXT, start_line INT, end_line INT, content TEXT, vec BLOB, ts REAL)")
    c.execute("CREATE INDEX IF NOT EXISTS ix_code_path ON code_chunks(path)")
    c.commit()
    return c


def reset():
    """Drop every indexed chunk (a clean full reindex)."""
    c = _conn()
    try:
        c.execute("DELETE FROM code_chunks")
        c.commit()
    finally:
        c.close()


def count():
    c = _conn()
    try:
        return c.execute("SELECT COUNT(*) FROM code_chunks").fetchone()[0]
    finally:
        c.close()


def embed(texts, timeout=60):
    """POST to the local embed server (OpenAI /v1/embeddings). texts: list[str] -> list[list[float]].
    Validates one vector per input: a short/garbage body (server OOM, token overrun) RAISES rather than
    letting a downstream zip() silently drop the trailing inputs (_embed_batch then retries per item)."""
    body = json.dumps({"model": EMBED_MODEL, "input": texts}).encode()
    req = urllib.request.Request(EMBED_URL, data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = json.load(resp)
    items = data.get("data") if isinstance(data, dict) else None
    if not isinstance(items, list) or len(items) != len(texts):
        got = len(items) if isinstance(items, list) else "no"
        raise ValueError(f"embed server returned {got} vectors for {len(texts)} inputs")
    return [d["embedding"] for d in items]


def _embed_input(text):
    return text if len(text) <= MAX_EMBED_CHARS else text[:MAX_EMBED_CHARS]


def _batched_by_chars(chunks, budget=EMBED_BATCH_CHARS):
    """Group chunks so each embed request stays under the server's token budget (chars as a proxy)."""
    batch, size = [], 0
    for ch in chunks:
        clen = min(len(ch[2]), MAX_EMBED_CHARS)
        if batch and size + clen > budget:
            yield batch
            batch, size = [], 0
        batch.append(ch)
        size += clen
    if batch:
        yield batch


def _embed_batch(chunks, embed_fn):
    """[(chunk, vec), ...] for one batch; on a server error, retry PER ITEM and skip any chunk the embed
    server still rejects — so one pathological chunk can never abort the whole index."""
    try:
        vecs = embed_fn([_embed_input(ch[2]) for ch in chunks])
        return list(zip(chunks, vecs))
    except Exception:
        out = []
        for ch in chunks:
            try:
                out.append((ch, embed_fn([_embed_input(ch[2])])[0]))
            except Exception:
                pass
        return out


def index_files(paths, root, embed_fn=embed):
    """Chunk + embed + (re)store each file at `root/path`. embed_fn is injectable (stubbed in tests).
    Re-indexing a file replaces its old chunks. Returns (files_indexed, chunks_written)."""
    c = _conn()
    nfiles = nchunks = 0
    try:
        for rel in paths:
            full = os.path.join(root, rel)
            try:
                with open(full, encoding="utf-8", errors="replace") as fh:
                    text = fh.read()
            except OSError:
                continue
            chunks = chunk_text(text)
            c.execute("DELETE FROM code_chunks WHERE path=?", (rel,))   # replace this file's chunks
            if not chunks:
                continue
            for batch in _batched_by_chars(chunks):
                for (start, end, content), vec in _embed_batch(batch, embed_fn):
                    c.execute("INSERT INTO code_chunks(path,start_line,end_line,content,vec,ts) VALUES(?,?,?,?,?,?)",
                              (rel, start, end, content, _pack(vec), time.time()))
                    nchunks += 1
            nfiles += 1
        c.commit()
    finally:
        c.close()
    return nfiles, nchunks


def search_vec(query_vec, k=5):
    """Brute-force cosine nearest neighbours over every stored chunk. Returns [] for a degenerate
    (zero-norm) query, and skips any stored vector whose dimension differs from the query — a mismatch
    (index built with a different embed model) would otherwise silently mis-score over a shared prefix."""
    qdim = len(query_vec)
    if math.sqrt(sum(x * x for x in query_vec)) == 0:
        return []
    c = _conn()
    try:
        rows = c.execute("SELECT path,start_line,end_line,content,vec FROM code_chunks").fetchall()
    finally:
        c.close()
    scored = []
    for r in rows:
        v = _unpack(r[4])
        if len(v) == qdim:
            scored.append((_cosine(query_vec, v), r))
    scored.sort(key=lambda t: t[0], reverse=True)
    return [{"path": r[0], "start_line": r[1], "end_line": r[2], "content": r[3], "score": round(s, 4)}
            for s, r in scored[:k]]


def search(query, k=5, embed_fn=embed):
    """Embed the query, then cosine-rank the indexed chunks."""
    qv = embed_fn([query])[0]
    return search_vec(qv, k)


# --- repo walker (which files to index) -----------------------------------------------------------------
def walk_repo(root):
    """Yield repo-relative paths of indexable source files, skipping weights / build output / vendored dirs."""
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS and not d.startswith(".")]
        for fn in filenames:
            if os.path.splitext(fn)[1].lower() in SOURCE_EXT:
                yield os.path.relpath(os.path.join(dirpath, fn), root).replace("\\", "/")


if __name__ == "__main__":
    import sys
    repo = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    files = list(walk_repo(repo))
    print(f"indexing {len(files)} files under {repo} -> {DB_PATH}")
    reset()
    nf, nc = index_files(files, repo)
    print(f"done: {nf} files, {nc} chunks embedded ({count()} total in index)")
