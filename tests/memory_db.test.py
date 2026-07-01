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

    # --- LIKE escape: %, _, and \ in a search term must be treated as literals ---
    # Force the LIKE fallback path so _like_escape is exercised directly regardless
    # of whether this Python build has FTS5.
    _fts_before = memory_db._HAS_FTS
    memory_db._HAS_FTS = False
    try:
        pct_id  = memory_db.save("batch job at 50% progress", "like-escape-test")
        nosp_id = memory_db.save("GPU model 5090 runs fast",  "like-escape-test")
        # Without the fix the unescaped pattern '%50%%' == '%50%', which matches any
        # string containing "50" — including "5090".  With the fix '%50\%%' only matches
        # strings containing the literal two-char sequence "50%".
        found_50pct = {m["id"] for m in memory_db.search("50%")}
        check(pct_id  in     found_50pct, 'LIKE escape: search("50%") finds row with literal "50%"')
        check(nosp_id not in found_50pct, 'LIKE escape: search("50%") does not false-match "5090" row (% unescaped = wildcard)')
        # Bare "%" must not match rows that have no literal "%" in them.
        # Before fix: pattern %% matches every row.
        found_bare = {m["id"] for m in memory_db.search("%")}
        check(nosp_id not in found_bare, 'LIKE escape: search("%") does not wildcard-match a row with no literal "%"')
        check(pct_id  in     found_bare, 'LIKE escape: search("%") matches the row that does contain a literal "%"')
    finally:
        memory_db._HAS_FTS = _fts_before

    # --- CLI dispatch (the C# MemoryRecall sidecar shells `python memory_db.py <cmd>` like DocSearch->doc_index.py) ---
    import subprocess
    import json as _json
    _mod = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "serving", "memory-mcp", "memory_db.py")

    def _cli(*a):
        return subprocess.run([sys.executable, _mod, *a], capture_output=True, text=True, env={**os.environ})

    _r = _cli("save", "CLI saved fact", "clitag")
    check(_r.returncode == 0, "cli save exits 0")
    _saved = _json.loads(_r.stdout or "{}")
    check(isinstance(_saved.get("id"), int) and _saved["id"] > 0, 'cli save prints {"id":N}')

    _rows = _json.loads(_cli("recent", "5").stdout or "[]")
    check(isinstance(_rows, list) and any(m.get("content") == "CLI saved fact" for m in _rows),
          "cli recent prints a JSON array incl. the saved note")

    _rows = _json.loads(_cli("search", "CLI saved", "5").stdout or "[]")
    check(any(m.get("content") == "CLI saved fact" for m in _rows), "cli search finds a note by content")

    check(_cli("bogus-cmd").returncode != 0, "cli unknown command exits non-zero")

    # --- seed.py: idempotent refresh that matches the exact 'seed' tag token (not a substring) ---
    import seed  # noqa: E402 — same temp MEMORY_DB; module import has no side effects
    trap1 = memory_db.save("seedlings need watering", "garden,seedling")   # substring traps a naive
    trap2 = memory_db.save("angel round closed", "startup,seed-money")     # '%seed%' delete would nuke
    seed.main()
    seed.main()  # run twice — must not duplicate
    allrows = memory_db.recent(1000)
    seeded = [m for m in allrows if "seed" in [t.strip() for t in (m["tags"] or "").split(",")]]
    check(len(seeded) == len(seed.FACTS), f"seed is idempotent: exactly {len(seed.FACTS)} seed rows after two runs (got {len(seeded)})")
    ids = {m["id"] for m in allrows}
    check(trap1 in ids and trap2 in ids, "re-seed preserves user notes tagged 'seedling'/'seed-money' (exact-token delete)")

    print(f"  (FTS5 active on this interpreter: {memory_db._HAS_FTS})")
finally:
    shutil.rmtree(_work, ignore_errors=True)

print(f"\nmemory_db: {_pass} passed, {_fail} failed")
sys.exit(1 if _fail else 0)
