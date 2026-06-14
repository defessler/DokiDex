# DokiCode STT — fully-local speech-to-text on :8005.
# NVIDIA Parakeet (TDT 0.6B v2) via onnx-asr — an OpenAI-compatible
# /v1/audio/transcriptions endpoint. CPU execution provider by default (no VRAM),
# so it coexists with the coder in agent mode; set STT_PROVIDER=cuda to use the GPU.
#
# No content filter — transcription is verbatim. Runs in its own isolated venv.
import os
import tempfile

from fastapi import FastAPI, File, Form, UploadFile
from fastapi.concurrency import run_in_threadpool
import uvicorn
import onnx_asr

MODEL_ID = os.environ.get("STT_MODEL", "nemo-parakeet-tdt-0.6b-v2")
PROVIDERS = ["CUDAExecutionProvider", "CPUExecutionProvider"] if os.environ.get("STT_PROVIDER") == "cuda" else ["CPUExecutionProvider"]

app = FastAPI(title="DokiCode STT")
_model = None


def model():
    global _model
    if _model is None:
        # downloads the Parakeet ONNX model from HF on first call, then caches it
        _model = onnx_asr.load_model(MODEL_ID, providers=PROVIDERS)
    return _model


@app.get("/")
def root():
    return {"status": "ok", "service": "DokiCode STT", "model": MODEL_ID, "providers": PROVIDERS}


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/v1/audio/transcriptions")
async def transcriptions(file: UploadFile = File(...), model_name: str = Form(default="parakeet", alias="model")):
    """OpenAI-compatible: multipart 'file' (16kHz wav best) -> {"text": ...}.
    NB: the form field is aliased to 'model' but the Python name must NOT be `model`
    (that shadows the module-level model() loader)."""
    data = await file.read()
    suffix = os.path.splitext(file.filename or "audio.wav")[1] or ".wav"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        tmp.write(data)
        path = tmp.name
    try:
        # recognize() (and the first-call model load) is blocking CPU work; run it OFF the event
        # loop so /health stays responsive during a transcription — otherwise the doki/panel health
        # probe (3s timeout) reports STT "down" for the whole ~6s job. onnx-asr resamples to 16k.
        text = await run_in_threadpool(lambda: model().recognize(path))
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass
    return {"text": text if isinstance(text, str) else (text[0] if text else "")}


if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=8005)
