# tests/code_index.test.py — regression tests for the codebase-RAG core (serving/memory-mcp/code_index.py).
#
# Pure stdlib; points CODE_INDEX_DB at a throwaway temp DB and injects a deterministic STUB embedder, so the
# chunker, vector packing, cosine ranking, (re)indexing, and repo walker are all exercised with NO embed
# server, NO GPU, NO network. exit 0 = pass, 1 = fail.
import os
import sys
import tempfile

_work = tempfile.mkdtemp(prefix="doki-codeidx-")
os.environ["CODE_INDEX_DB"] = os.path.join(_work, "idx.db")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "serving", "memory-mcp"))
import code_index  # noqa: E402  — import AFTER CODE_INDEX_DB is set; DB_PATH is read at import time

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
# signal to test ranking without a real embed model.
_VOCAB = ["gpu", "meter", "reactor", "boot", "memory", "search", "video", "music", "cosine", "embed"]


def stub_embed(texts):
    out = []
    for t in texts:
        tl = t.lower()
        v = [float(tl.count(w)) for w in _VOCAB]
        v.append(0.5)   # bias term so a chunk with no vocab hit isn't a degenerate all-zero vector
        out.append(v)
    return out


def writefile(rel, body):
    p = os.path.join(_work, rel)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "w", encoding="utf-8") as fh:
        fh.write(body)
    return rel


try:
    # --- chunk_text (pure) ---
    check(code_index.chunk_text("") == [], "chunk_text('') -> []")
    check(code_index.chunk_text("\n\n  \n") == [], "all-blank text -> [] (blank windows dropped)")
    one = code_index.chunk_text("line1\nline2\nline3")
    check(len(one) == 1 and one[0][0] == 1 and one[0][1] == 3, "short text -> one chunk, lines 1..3 (1-based inclusive)")

    big = "\n".join(f"row{i}" for i in range(1, 151))   # 150 lines
    cks = code_index.chunk_text(big, max_lines=60, overlap=10)
    check(len(cks) >= 3, f"150 lines @ 60/10 -> multiple windows ({len(cks)})")
    check(cks[0][0] == 1, "first window starts at line 1")
    check(cks[1][0] <= cks[0][1], "consecutive windows overlap (next start <= prev end)")
    check(cks[-1][1] == 150, "last window reaches the final line (full coverage)")

    # --- vector pack/unpack + cosine (pure) ---
    ru = code_index._unpack(code_index._pack([0.1, -2.5, 3.0, 0.0]))
    check(len(ru) == 4 and abs(ru[1] + 2.5) < 1e-5, "pack/unpack round-trips a float vector")
    check(abs(code_index._cosine([1, 2, 3], [1, 2, 3]) - 1.0) < 1e-9, "cosine(x,x) == 1")
    check(code_index._cosine([1, 0], [0, 1]) == 0.0, "cosine(orthogonal) == 0")
    check(code_index._cosine([0, 0], [1, 1]) == 0.0, "cosine with a zero vector == 0 (no div-by-zero)")

    # --- index_files + search (stub embedder, temp DB) ---
    f_gpu = writefile("gpu.ps1", "the gpu meter shows vram and gpu temperature\n" * 3)
    f_mus = writefile("media.ps1", "generate music and video from a prompt\n" * 3)
    f_mem = writefile("mem.py", "memory search recalls saved notes\n" * 3)
    nf, nc = code_index.index_files([f_gpu, f_mus, f_mem], _work, embed_fn=stub_embed)
    check(nf == 3 and nc == 3, f"index_files indexed 3 files / 3 chunks (got {nf}/{nc})")
    check(code_index.count() == 3, "count() == 3 after indexing")

    hits = code_index.search("gpu meter vram", k=3, embed_fn=stub_embed)
    check(len(hits) == 3, "search returns k results")
    check(hits[0]["path"] == "gpu.ps1", f"top hit for 'gpu meter' is gpu.ps1 (got {hits[0]['path']})")
    check(hits[0]["score"] >= hits[1]["score"], "results sorted by descending score")
    check(hits[0]["start_line"] == 1 and hits[0]["end_line"] == 3, "hit carries a 1-based line range")
    check(code_index.search("music video generation", k=1, embed_fn=stub_embed)[0]["path"] == "media.ps1",
          "top hit for 'music video' is media.ps1")

    # --- re-index replaces, not duplicates ---
    code_index.index_files([f_gpu], _work, embed_fn=stub_embed)
    check(code_index.count() == 3, "re-indexing a file replaces its chunks (count stays 3)")

    # --- walk_repo: skips weights / build output / vendored + non-source ext ---
    writefile("keep.py", "x = 1\n")
    writefile("models/huge.gguf", "BINARY")
    writefile("bin/Release/App.dll", "BINARY")
    writefile(".git/config", "[core]\n")
    walked = set(code_index.walk_repo(_work))
    check("keep.py" in walked, "walk_repo yields a real source file")
    check(not any(w.endswith(".gguf") for w in walked), "walk_repo skips *.gguf weights")
    check(not any(w.startswith("bin/") for w in walked), "walk_repo skips bin/ build output")
    check(not any(".git" in w for w in walked), "walk_repo skips .git")

    # --- request-budget batching + truncation (the fix for the embed server's per-input token limit) ---
    check(len(code_index._embed_input("z" * 9000)) == code_index.MAX_EMBED_CHARS, "_embed_input truncates an over-long chunk")
    check(code_index._embed_input("short") == "short", "_embed_input leaves a short chunk untouched")
    big_batch = [(i, i, "y" * 2500) for i in range(5)]   # 12500 chars total vs a 6000-char budget
    parts = list(code_index._batched_by_chars(big_batch))
    check(len(parts) >= 2, f"_batched_by_chars splits when total exceeds the budget ({len(parts)} parts)")
    check(sum(len(p) for p in parts) == 5, "_batched_by_chars preserves every chunk across splits")

    def flaky(texts):   # fails on any batch/item containing BOOM (simulates the embed server rejecting one)
        if any("BOOM" in t for t in texts):
            raise RuntimeError("server 500")
        return [[1.0] for _ in texts]
    got = code_index._embed_batch([(1, 1, "good one"), (2, 2, "BOOM bad"), (3, 3, "good two")], flaky)
    check(len(got) == 2 and all("good" in c[2] for c, _ in got), "_embed_batch skips a rejected chunk, keeps the rest (no abort)")

except Exception as e:
    import traceback
    traceback.print_exc()
    check(False, f"unexpected exception: {e}")

print(f"\ncode_index: {_pass} passed, {_fail} failed")
sys.exit(1 if _fail else 0)
