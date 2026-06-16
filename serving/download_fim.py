"""Download the small FIM (fill-in-the-middle) autocomplete model.

ggml-org/Qwen2.5-Coder-3B-Q8_0-GGUF is the base (non-instruct) model
llama.vscode recommends for local tab-completion. ~3.3GB.
"""
import os
from huggingface_hub import hf_hub_download, list_repo_files

REPO = "ggml-org/Qwen2.5-Coder-3B-Q8_0-GGUF"
# Repo-relative (serving\ -> ..\models) so a project move can't break it.
MODELS_DIR = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "models"))

ggufs = [f for f in list_repo_files(REPO) if f.endswith(".gguf")]
print("GGUF files:", ggufs, flush=True)
target = ggufs[0]
print(f"DOWNLOADING {REPO} :: {target}", flush=True)
path = hf_hub_download(repo_id=REPO, filename=target, local_dir=MODELS_DIR)
print(f"DONE {path}", flush=True)
print("FIM_DOWNLOAD_COMPLETE", flush=True)
