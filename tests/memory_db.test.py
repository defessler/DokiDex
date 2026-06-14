# tests/memory_db.test.py — regression tests for the persistent-memory store (sqlite + FTS5).
#
# Pure stdlib; points MEMORY_DB at a throwaway temp DB (never touches the seeded memory.db).
# Pins the save/search/recent/delete round-trips and the key resilience contract: an
# FTS5-breaking query must NOT raise — search degrades to LIKE. exit 0 = pass, 1 = fail.
import os
import sys
import tempfile
import shutil

_work = tempfile.mkdtemp(prefix="doki-mem-")
os.environ["MEMORY_DB"] = os.path.join(_work, "test.db")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "serving", "memory-mcp"))
import memory_db  # noqa: E402  — import AFTER MEMORY_DB is set; DB_PATH is read at import time

_pass = _fail = 0


def check(cond, msg):
    global _pass, _fail
    if cond:
        _pass += 1
        print(f"  [PASS] {msg}")
    else:
        _fail += 1
        print(f"  [FAIL] {msg}")


try:
    # --- save ---
    try:
        memory_db.save("   ")
        check(False, "save(blank) raises ValueError")
    except ValueError:
        check(True, "save(blank) raises ValueError")

    rid = memory_db.save("   trimmed   ")
    check(isinstance(rid, int) and rid > 0, "save returns a positive int id")
    check(memory_db.recent(1)[0]["content"] == "trimmed", "save strips surrounding whitespace")

    a = memory_db.save("the RTX 5090 has 32GB of VRAM on Blackwell", "gpu hardware")
    b = memory_db.save("Crush is the daily-driver coder CLI", "harness")
    c = memory_db.save("llama-swap hot-swaps coder-fast and coder-big", "serving")
    check(a < b < c, "ids increase monotonically")

    # --- search ---
    check(any("32GB" in m["content"] for m in memory_db.search("VRAM")), "search finds a memory by content word")
    check(any(m["id"] == b for m in memory_db.search("harness")), "search matches on tags too")
    check(len(memory_db.search("coder", limit=1)) <= 1, "search honors the limit arg")

    # --- resilience: FTS5-breaking input must degrade to LIKE, never raise ---
    for bad in ['"unbalanced', "a AND", "NEAR(", ")(", "col:val", "foo*", "50%", ""]:
        try:
            memory_db.search(bad)
            check(True, f"search({bad!r}) does not raise (FTS->LIKE fallback)")
        except Exception as e:
            check(False, f"search({bad!r}) raised {type(e).__name__}: {e}")

    # --- recent / delete ---
    top = memory_db.recent(10)
    check(len(top) >= 3 and top[0]["id"] == c, "recent is newest-first")
    memory_db.delete(a)
    check(all(m["id"] != a for m in memory_db.recent(10)), "delete removes the row")
    check(all(m["id"] != a for m in memory_db.search("Blackwell")), "delete keeps the FTS index consistent")

    print(f"  (FTS5 active on this interpreter: {memory_db._HAS_FTS})")
finally:
    shutil.rmtree(_work, ignore_errors=True)

print(f"\nmemory_db: {_pass} passed, {_fail} failed")
sys.exit(1 if _fail else 0)
