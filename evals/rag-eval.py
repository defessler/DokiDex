# evals/rag-eval.py — retrieval benchmark for the codebase RAG (the code_search MCP tool).
#
# A fixed set of natural-language queries, each paired with the repo file it SHOULD surface. Indexes the
# repo through the live embed server (:8090, CPU) into a throwaway DB, runs code_search, and scores
# recall@k. This is the measured GATE for embedder swaps — the RAG analogue of the golden-task coder
# suite: re-run after changing the embed model (or chunking) and compare; never tune retrieval blind.
#
# Needs the embed server up (`doki up agent` / `serving/start-embed.ps1`). Side-effect-free: it uses a
# temp index DB and never touches the user's serving/memory-mcp/code_index.db.
import os
import sys
import tempfile

os.environ["CODE_INDEX_DB"] = os.path.join(tempfile.gettempdir(), "doki-rag-bench.db")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "serving", "memory-mcp"))
import code_index  # noqa: E402 — import AFTER CODE_INDEX_DB is set

# (query, expected path substring): each query should surface that file in the top-k. Chosen so the target
# is the clear best match by content, spread across the coding + media + panel + serving subsystems.
BENCH = [
    ("off-screen render the control panel window to a PNG image",        "App.xaml.cs"),
    ("switch the GPU between the LLM and media mode profiles",           "doki.ps1"),
    ("cosine similarity nearest-neighbour vector search over chunks",    "code_index.py"),
    ("uncensored text to speech voice cloning server",                   "start-tts.ps1"),
    ("speech to text transcription with parakeet",                       "start-stt.ps1"),
    ("CPU-only embedding server for the codebase RAG",                   "start-embed.ps1"),
    ("persistent project memory save and full-text FTS5 search",         "memory_db.py"),
    ("text to image / video / music generation recipe dispatch",         "doki-gen.ps1"),
    ("in-app auto-updater downloads, verifies and swaps the exe",        "Updater.cs"),
    ("warm-load a coder model into llama-swap with a tiny request",      "DokiService.cs"),
    ("the cinematic boot splash sequence the seal ignites",              "BootWindow"),
    ("the always-on prompt rewriter that expands lazy prompts",          "start-prompt-rewriter.ps1"),
    ("full-stack verify smoke test hitting every capability",            "verify.ps1"),
    ("derive service state healthy degraded crashed from a poll",        "ServiceViewModel.cs"),
]

KS = [1, 3, 5]


def hit_at_k(hits, expected, k):
    return any(expected.lower() in h["path"].lower() for h in hits[:k])


def main():
    root = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    files = list(code_index.walk_repo(root))
    code_index.reset()
    nf, nc = code_index.index_files(files, root)
    print(f"indexed {nf} files / {nc} chunks\n")

    score = {k: 0 for k in KS}
    for q, expected in BENCH:
        hits = code_index.search(q, k=max(KS))
        for k in KS:
            if hit_at_k(hits, expected, k):
                score[k] += 1
        marks = "".join("+" if hit_at_k(hits, expected, k) else "." for k in KS)
        top = hits[0]["path"] if hits else "(none)"
        print(f"  [{marks}] want {expected:28s} top: {top}")

    n = len(BENCH)
    print(f"\nrecall@1 = {score[1]}/{n}   recall@3 = {score[3]}/{n}   recall@5 = {score[5]}/{n}")
    return 0 if score[3] >= int(0.8 * n) else 1   # gate: >=80% of queries surface the right file in top-3


if __name__ == "__main__":
    sys.exit(main())
