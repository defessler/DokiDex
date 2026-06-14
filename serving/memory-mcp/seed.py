# seed.py — populate the memory store with DokiCode's hard-won facts/gotchas so the coding
# agent starts with real project knowledge (and to demonstrate the store end-to-end).
# Idempotent: clears prior 'seed' notes first. Run:  python serving/memory-mcp/seed.py
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import memory_db  # noqa: E402

FACTS = [
    ("Wan 2.2 TI2V-5B uses wan2.2_vae, NOT wan_2.1_vae (that's the Wan 2.1 1.3B floor's VAE).", "media,vae,gotcha"),
    ("No flash-attn wheel exists for Blackwell sm_120 (RTX 5090) on native Windows — use SDPA.", "blackwell,gotcha"),
    ("Image-to-video works via SwarmUI's NATIVE videomodel param (also needs videosteps+videocfg+videoresolution), not a custom workflow.", "media,i2v"),
    ("LTX-2 19B (class lightricks-ltx-video-2): SwarmUI detects it but can't load the transformer_only weights yet ('comfy limitations'). Nascent support.", "media,ltx2,blocked"),
    ("Wan-S2V and SUPIR are blocked by SwarmUI's custom-workflow base64 image/audio injection limitation (no native class). Use native paths only.", "media,blocked"),
    ("ACE-Step 1.5 (class ace-step-1_5) is the SwarmUI-native music model — NOT the v1 all-in-one. The qwen ace15 text-encoders auto-download.", "media,music"),
    ("Qwen-Image-Edit-2511 ships fp8mixed (~20GB), NOT fp8_e4m3fn. Class qwen-image-edit-plus, SwarmUI-native (model + init image + instruction).", "media,edit"),
    ("4x-UltraSharp upscale only fires via the Refiner-Upscale group: refinermethod=PostApply + refinercontrolpercentage=0 + refinerupscalemethod=model-4x-UltraSharp.pth.", "media,upscale,gotcha"),
    ("STT gotcha: a FastAPI Form param named 'model' shadows the module-level model() loader -> 'str object is not callable'. Alias the param.", "stt,gotcha"),
    ("32GB VRAM ceiling: Wan 2.2 A14B dual-expert and LTX-2 (19B + Gemma3-12B encoder) don't fit. Wan 2.2 5B is the practical video max.", "vram,gotcha"),
    ("TTS (Chatterbox): pin protobuf 4.25.5 (onnx needs >=3.20; the cu128 reqs pin 3.19.6). The Perth watermark is stripped at install.", "tts,gotcha"),
    ("Coder: Crush + Qwen3-Coder-30B (coder-fast) = 91% on the golden suite (the daily driver). coder-big = gpt-oss-120b (CPU-offload, quality).", "code,models"),
    ("doki.ps1 is the control plane (no Docker): up/down/status json/restart/start/stop/panel. The GPU runs one group (llm vs media). Scripts are PowerShell.", "infra"),
    ("doki verify runs 14 live capability smokes + a memory smoke; expects all-green. Add a guarded smoke for any new service.", "infra,verify"),
]


def main():
    c = memory_db._conn()  # ensures schema + FTS triggers exist
    c.execute("DELETE FROM memories WHERE tags LIKE '%seed%'")  # idempotent refresh
    c.commit()
    c.close()
    for content, tags in FACTS:
        memory_db.save(content, tags + ",seed")
    print(f"seeded {len(FACTS)} memories into {memory_db.DB_PATH}")


if __name__ == "__main__":
    main()
