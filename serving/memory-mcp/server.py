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


@mcp.tool()
def code_search(query: str, limit: int = 5) -> str:
    """Semantic search over THIS repository's indexed source code (RAG). Returns the most relevant code
    chunks with their file path + line range — use it to find WHERE something is implemented when a literal
    keyword grep would miss the right file (different wording, related concept). Requires the local embed
    server and a prior index build; returns a hint if either is missing."""
    try:
        import code_index
        rows = code_index.search(query, limit)
    except Exception as e:  # embed server down / index missing / bad query — never crash the MCP session
        return (f"code_search unavailable ({type(e).__name__}: {e}). Is the embed server up and the repo "
                "indexed?  Build the index with:  doki index")
    if not rows:
        return "no matching code chunks — is the index built?  (run:  doki index)"
    return "\n\n".join(
        f"{r['path']}:{r['start_line']}-{r['end_line']}  (score {r['score']})\n{r['content'][:500]}"
        for r in rows)


if __name__ == "__main__":
    mcp.run()
