# memory_db.py — DokiCode persistent memory store (sqlite + FTS5 full-text search).
# Pure, dependency-free (stdlib sqlite3), and testable on its own. Gracefully degrades to
# LIKE search if the bundled sqlite wasn't built with FTS5.
import os
import sqlite3
import time

DB_PATH = os.environ.get("MEMORY_DB", os.path.join(os.path.dirname(os.path.abspath(__file__)), "memory.db"))
_HAS_FTS = None


def _conn():
    global _HAS_FTS
    c = sqlite3.connect(DB_PATH)
    c.execute("CREATE TABLE IF NOT EXISTS memories (id INTEGER PRIMARY KEY, content TEXT NOT NULL, tags TEXT DEFAULT '', ts REAL)")
    if _HAS_FTS is None:
        try:
            c.execute("CREATE VIRTUAL TABLE IF NOT EXISTS mem_fts USING fts5(content, tags, content='memories', content_rowid='id')")
            c.execute("CREATE TRIGGER IF NOT EXISTS mem_ai AFTER INSERT ON memories BEGIN INSERT INTO mem_fts(rowid, content, tags) VALUES (new.id, new.content, new.tags); END")
            c.execute("CREATE TRIGGER IF NOT EXISTS mem_ad AFTER DELETE ON memories BEGIN INSERT INTO mem_fts(mem_fts, rowid, content, tags) VALUES('delete', old.id, old.content, old.tags); END")
            _HAS_FTS = True
        except sqlite3.OperationalError:
            _HAS_FTS = False
    c.commit()
    return c


def save(content, tags=""):
    """Store a note. Returns the new row id."""
    if not content or not content.strip():
        raise ValueError("content required")
    c = _conn()
    cur = c.execute("INSERT INTO memories(content, tags, ts) VALUES(?,?,?)", (content.strip(), (tags or "").strip(), time.time()))
    c.commit()
    rid = cur.lastrowid
    c.close()
    return rid


def search(query, limit=5):
    """Full-text search (FTS5 if available, else LIKE). Returns a list of dicts."""
    c = _conn()
    rows = []
    if _HAS_FTS:
        try:
            rows = c.execute(
                "SELECT m.id, m.content, m.tags, m.ts FROM mem_fts f JOIN memories m ON m.id=f.rowid "
                "WHERE mem_fts MATCH ? ORDER BY rank LIMIT ?", (query, int(limit))).fetchall()
        except sqlite3.OperationalError:
            rows = []  # bad FTS query syntax -> fall through to LIKE
    if not rows:
        rows = c.execute(
            "SELECT id, content, tags, ts FROM memories WHERE content LIKE ? OR tags LIKE ? "
            "ORDER BY ts DESC LIMIT ?", (f"%{query}%", f"%{query}%", int(limit))).fetchall()
    c.close()
    return [{"id": r[0], "content": r[1], "tags": r[2], "ts": r[3]} for r in rows]


def recent(limit=10):
    c = _conn()
    rows = c.execute("SELECT id, content, tags, ts FROM memories ORDER BY ts DESC LIMIT ?", (int(limit),)).fetchall()
    c.close()
    return [{"id": r[0], "content": r[1], "tags": r[2], "ts": r[3]} for r in rows]


def delete(mem_id):
    c = _conn()
    c.execute("DELETE FROM memories WHERE id=?", (int(mem_id),))
    c.commit()
    c.close()
    return True


# CLI dispatch — lets the C# chat surface (MemoryRecall) shell `python memory_db.py <cmd> ...` one-shot,
# exactly like DocSearch shells doc_index.py. Prints JSON to stdout; a bad command / error prints {"error":...}
# and exits non-zero. The MCP server (memory_mcp.py) still imports the functions above unchanged.
if __name__ == "__main__":
    import sys
    import json as _json
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    try:
        if cmd == "recent":
            print(_json.dumps(recent(int(sys.argv[2]) if len(sys.argv) > 2 else 10)))
        elif cmd == "search":
            q = sys.argv[2] if len(sys.argv) > 2 else ""
            lim = int(sys.argv[3]) if len(sys.argv) > 3 else 5
            print(_json.dumps(search(q, lim)))
        elif cmd == "save":
            content = sys.argv[2] if len(sys.argv) > 2 else ""
            tags = sys.argv[3] if len(sys.argv) > 3 else ""
            print(_json.dumps({"id": save(content, tags)}))
        elif cmd == "delete":
            print(_json.dumps({"ok": delete(int(sys.argv[2]))}))
        else:
            print(_json.dumps({"error": "usage: memory_db.py {recent|search|save|delete} ..."}))
            sys.exit(2)
    except Exception as e:
        print(_json.dumps({"error": str(e)}))
        sys.exit(1)
