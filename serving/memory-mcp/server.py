# server.py — DokiCode Memory MCP: persistent project memory + full-text search for the
# coding agent (Crush), so facts/decisions survive across sessions. stdio transport.
#
# Launched by Crush via:  uv run --with "mcp[cli]" <this file>
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # so `import memory_db` works regardless of cwd
import memory_db  # noqa: E402

from mcp.server.fastmcp import FastMCP  # noqa: E402

mcp = FastMCP("doki-memory")


@mcp.tool()
def memory_save(content: str, tags: str = "") -> str:
    """Save a note, fact, decision, or preference to persistent project memory so it survives
    across sessions. `tags` is an optional comma-separated set of labels for later filtering."""
    rid = memory_db.save(content, tags)
    return f"saved memory #{rid}"


@mcp.tool()
def memory_search(query: str, limit: int = 5) -> str:
    """Search persistent project memory by keywords (full-text). Use before starting work to
    recall prior decisions, gotchas, and context. Returns the best-matching notes."""
    rows = memory_db.search(query, limit)
    if not rows:
        return "no matching memories"
    return "\n".join(f"#{r['id']} [{r['tags']}] {r['content']}" for r in rows)


@mcp.tool()
def memory_recent(limit: int = 10) -> str:
    """List the most recently saved memories (for a quick catch-up on recent context)."""
    rows = memory_db.recent(limit)
    if not rows:
        return "no memories yet"
    return "\n".join(f"#{r['id']} [{r['tags']}] {r['content']}" for r in rows)


@mcp.tool()
def memory_delete(memory_id: int) -> str:
    """Delete a memory by its id (e.g. when a fact is outdated or wrong)."""
    memory_db.delete(memory_id)
    return f"deleted memory #{memory_id}"


if __name__ == "__main__":
    mcp.run()
