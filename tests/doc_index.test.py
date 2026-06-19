# tests/doc_index.test.py — regression tests for the knowledge-base RAG core (serving/memory-mcp/doc_index.py).
#
# The doc_index is the per-conversation "chat with your documents" twin of code_index: it chunks PROSE (by
# chars, not source-lines), embeds each chunk via the SAME :8090 embed server, stores the vectors in its OWN
# disposable sqlite DB scoped by kb_id, and answers nearest-neighbour queries by brute-force cosine. This suite
# is pure stdlib: it points DOC_INDEX_DB at a throwaway temp DB and injects a deterministic STUB embedder, so
# the chunker, vector pack/unpack, cosine ranking, kb-scoped (re)ingest, and retrieval are all exercised with
# NO embed server, NO GPU, NO network. exit 0 = pass, 1 = fail. Mirrors tests/code_index.test.py exactly.
import os
import sys
import tempfile

_work = tempfile.mkdtemp(prefix="doki-docidx-")
os.environ["DOC_INDEX_DB"] = os.path.join(_work, "doc.db")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "serving", "memory-mcp"))
import doc_index  # noqa: E402  — import AFTER DOC_INDEX_DB is set; DB_PATH is read at import time

_pass = _fail = 0


def check(cond, msg):
    global _pass, _fail
    if cond:
        _pass += 1
        print(f"  [PASS] {msg}")
    else:
        _fail += 1
        print(f"  [FAIL] {msg}")


# deterministic bag-of-keywords stub: chunks sharing vocab with the query score higher under cosine — enough
# signal to test ranking without a real embed model (same trick as code_index.test.py).
_VOCAB = ["dragon", "north", "castle", "ocean", "south", "forest", "wizard", "spell", "river", "mountain"]


def stub_embed(texts):
    out = []
    for t in texts:
        tl = t.lower()
        v = [float(tl.count(w)) for w in _VOCAB]
        v.append(0.5)   # bias so a no-vocab chunk isn't a degenerate all-zero vector
        out.append(v)
    return out


try:
    # --- chunk_text (pure, prose: bounded overlapping CHAR windows) ---
    check(doc_index.chunk_text("") == [], "chunk_text('') -> []")
    check(doc_index.chunk_text("   \n\n  \n ") == [], "all-blank text -> [] (blank windows dropped)")

    short = doc_index.chunk_text("a short paragraph that fits in one window")
    check(len(short) == 1 and short[0][0] == 0, "short text -> one chunk, ordinal 0")
    check(short[0][1] == "a short paragraph that fits in one window", "the single chunk carries the full text")

    body = "x" * 4000   # 4000 chars at size 1500 / overlap 200 -> multiple overlapping windows
    cks = doc_index.chunk_text(body, size=1500, overlap=200)
    check(len(cks) >= 3, f"4000 chars @ 1500/200 -> multiple windows ({len(cks)})")
    check(cks[0][0] == 0 and cks[1][0] == 1, "chunk ordinals are 0-based and sequential")
    # windows overlap: each window after the first begins (size-overlap)=1300 chars in, so consecutive windows
    # share the trailing/leading `overlap` chars — a sentence straddling a boundary is findable from either side.
    step = 1500 - 200
    check(len(cks[0][1]) == 1500, "first window is exactly `size` chars")
    full = "".join(cks[i][1][:step] if i < len(cks) - 1 else cks[i][1] for i in range(len(cks)))
    check(body in full or full.startswith(body[:step]), "windows tile the whole text (overlap, no gap)")
    # explicit overlap proof: the last `overlap` chars of window 0 equal the first `overlap` chars of window 1.
    check(cks[0][1][step:] == cks[1][1][:1500 - step], "consecutive windows overlap by `overlap` chars")

    # --- vector pack/unpack + cosine reused VERBATIM from code_index (dim-agnostic) ---
    ru = doc_index._unpack(doc_index._pack([0.1, -2.5, 3.0, 0.0]))
    check(len(ru) == 4 and abs(ru[1] + 2.5) < 1e-5, "pack/unpack round-trips a float vector")
    check(abs(doc_index._cosine([1, 2, 3], [1, 2, 3]) - 1.0) < 1e-9, "cosine(x,x) == 1")
    check(doc_index._cosine([1, 0], [0, 1]) == 0.0, "cosine(orthogonal) == 0")
    check(doc_index._cosine([0, 0], [1, 1]) == 0.0, "cosine with a zero vector == 0 (no div-by-zero)")

    # --- ingest_doc + search (stub embedder, temp DB, kb_id-scoped) ---
    n1 = doc_index.ingest_doc("kbA", "lore.txt",
                              "Dragons rule the north and guard the castle.", embed_fn=stub_embed)
    n2 = doc_index.ingest_doc("kbA", "geo.txt",
                              "The ocean lies to the south past the forest.", embed_fn=stub_embed)
    check(n1 >= 1 and n2 >= 1, f"ingest_doc returns chunks written ({n1}, {n2})")
    check(doc_index.count("kbA") == n1 + n2, "count(kbA) totals both docs' chunks")

    hits = doc_index.search("kbA", "dragon castle north", k=3, embed_fn=stub_embed)
    check(len(hits) >= 1, "search returns hits")
    check(hits[0]["source"] == "lore.txt", f"top hit for 'dragon castle' is lore.txt (got {hits[0]['source']})")
    check(hits[0]["score"] >= hits[-1]["score"], "results sorted by descending score")
    check(doc_index.search("kbA", "ocean south forest", k=1, embed_fn=stub_embed)[0]["source"] == "geo.txt",
          "top hit for 'ocean south' is geo.txt")

    # --- kb_id scoping: a query in another KB never sees kbA's chunks ---
    doc_index.ingest_doc("kbB", "other.txt", "A wizard casts a spell by the river.", embed_fn=stub_embed)
    check(doc_index.count("kbB") >= 1, "kbB has its own chunks")
    bhits = doc_index.search("kbB", "dragon castle north", k=5, embed_fn=stub_embed)
    check(all(h["source"] == "other.txt" for h in bhits), "search is scoped to kb_id (kbB never returns kbA rows)")
    check(doc_index.search("kbMISSING", "anything", k=5, embed_fn=stub_embed) == [],
          "search over an unknown kb_id returns [] (empty, not an error)")

    # --- re-ingest replaces a source's chunks, not duplicates ---
    # lore.txt currently has n1 chunks; re-ingesting a SHORTER body (one window) must leave the KB at
    # geo.txt's chunks + exactly ONE lore.txt chunk — strictly fewer rows than before, never duplicated.
    before = doc_index.count("kbA")
    doc_index.ingest_doc("kbA", "lore.txt", "Dragons rule the north.", embed_fn=stub_embed)
    after = doc_index.count("kbA")
    check(after == n2 + 1, f"re-ingest replaces lore.txt with its single new chunk (geo {n2} + lore 1 = {after})")
    check(after <= before, f"re-ingest never grows the row count ({before} -> {after})")
    # prove no dupes: only geo.txt + the single replaced lore.txt chunk remain for the short bodies.
    srcs = sorted({h["source"] for h in doc_index.search("kbA", "north south", k=10, embed_fn=stub_embed)})
    check(srcs == ["geo.txt", "lore.txt"], f"re-ingest leaves exactly the two sources (got {srcs})")

    # --- search_vec guards: degenerate / mismatched queries return [] instead of garbage rankings ---
    check(doc_index.search_vec("kbA", [0.0] * 11, k=5) == [], "zero-norm query returns [] (no all-tied ranking)")
    check(doc_index.search_vec("kbA", [1.0, 2.0, 3.0], k=5) == [],
          "dimension-mismatched query returns [] (no prefix mis-scoring)")

    # --- source-prefix: the source filename is prepended to the EMBED input (helps doc-naming queries) ---
    captured = []
    def spy(texts):
        captured.extend(texts)
        return [[1.0] for _ in texts]
    doc_index.ingest_doc("kbS", "manual.txt", "body text", embed_fn=spy)
    check(captured and "manual.txt" in captured[0], "ingest prepends the source filename to the embed input")

    # --- request-budget batching + truncation (the embed server's per-input token limit), reused from code_index ---
    check(len(doc_index._embed_input("z" * 9000)) == doc_index.MAX_EMBED_CHARS, "_embed_input truncates an over-long chunk")
    check(doc_index._embed_input("short") == "short", "_embed_input leaves a short chunk untouched")

    def flaky(texts):   # fails on any item containing BOOM (simulates the embed server rejecting one)
        if any("BOOM" in t for t in texts):
            raise RuntimeError("server 500")
        return [[1.0] for _ in texts]
    # a long body so chunk_text yields MULTIPLE windows; only the middle window carries BOOM.
    flaky_body = ("good " * 300) + ("BOOM " * 300) + ("good2 " * 300)   # ~5400 chars -> several windows
    n = doc_index.ingest_doc("kbF", "f.txt", flaky_body, embed_fn=flaky)
    # the pathological window(s) are skipped, the rest survive — one bad chunk can never abort the whole ingest.
    check(doc_index.count("kbF") == n and n >= 1, f"ingest skips a rejected chunk, keeps the rest ({n})")
    check(not any("BOOM" in h["content"] for h in doc_index.search_vec("kbF", [1.0], k=20)),
          "no stored kbF chunk contains the rejected BOOM window")

    # --- chunk-count cap: a pathologically large body can't emit thousands of sequential embed calls. chunk_text
    #     stops at MAX_CHUNKS windows (drops the tail) so ingest stays bounded even on a direct CLI call. ---
    check(doc_index.MAX_CHUNKS >= 1, "MAX_CHUNKS is a positive cap")
    huge = "word " * 200_000   # ~1,000,000 chars -> far more than MAX_CHUNKS windows without a cap
    capped = doc_index.chunk_text(huge)
    check(len(capped) == doc_index.MAX_CHUNKS, f"chunk_text caps at MAX_CHUNKS windows ({len(capped)} == {doc_index.MAX_CHUNKS})")
    nh = doc_index.ingest_doc("kbHUGE", "huge.txt", huge, embed_fn=stub_embed)
    check(nh <= doc_index.MAX_CHUNKS, f"ingest_doc never stores more than MAX_CHUNKS chunks ({nh})")
    # a small body is unaffected by the cap (still a single window).
    check(len(doc_index.chunk_text("just a little prose")) == 1, "the chunk cap leaves a small doc at one window")

    # --- doc_delete: dropping a KB's chunks on conversation-delete (the disk-leak fix). delete(kb_id) removes
    #     exactly that KB's rows and leaves every other KB intact. ---
    doc_index.ingest_doc("kbDEL", "a.txt", "Dragons in the north.", embed_fn=stub_embed)
    doc_index.ingest_doc("kbDEL", "b.txt", "An ocean to the south.", embed_fn=stub_embed)
    doc_index.ingest_doc("kbKEEP", "k.txt", "A wizard by the river.", embed_fn=stub_embed)
    check(doc_index.count("kbDEL") >= 2, "kbDEL seeded with two sources")
    removed = doc_index.delete("kbDEL")
    check(removed >= 2, f"delete(kbDEL) reports the rows dropped ({removed})")
    check(doc_index.count("kbDEL") == 0, "delete(kbDEL) leaves zero kbDEL rows")
    check(doc_index.count("kbKEEP") >= 1, "delete(kbDEL) leaves other KBs intact (kbKEEP untouched)")
    check(doc_index.delete("kbNEVER") == 0, "delete over an unknown kb_id is a no-op returning 0")

except Exception as e:
    import traceback
    traceback.print_exc()
    check(False, f"unexpected exception: {e}")

print(f"\ndoc_index: {_pass} passed, {_fail} failed")
sys.exit(1 if _fail else 0)
