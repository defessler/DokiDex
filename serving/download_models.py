"""Download DokiCode model weights from HuggingFace with resume support.

Model A first (needed for first benchmarks), then model B's three shards.
Re-running skips completed files.
"""
import sys
from huggingface_hub import hf_hub_download

MODELS_DIR = r"D:\Projects\DokiCode\models"

FILES = [
    ("unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF",
     "Qwen3-Coder-30B-A3B-Instruct-UD-Q4_K_XL.gguf"),
    ("ggml-org/gpt-oss-120b-GGUF", "gpt-oss-120b-mxfp4-00001-of-00003.gguf"),
    ("ggml-org/gpt-oss-120b-GGUF", "gpt-oss-120b-mxfp4-00002-of-00003.gguf"),
    ("ggml-org/gpt-oss-120b-GGUF", "gpt-oss-120b-mxfp4-00003-of-00003.gguf"),
]

for repo, fname in FILES:
    print(f"DOWNLOADING {repo} :: {fname}", flush=True)
    path = hf_hub_download(repo_id=repo, filename=fname, local_dir=MODELS_DIR)
    print(f"DONE {path}", flush=True)

print("ALL_DOWNLOADS_COMPLETE", flush=True)
