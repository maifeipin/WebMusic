import os
import uvicorn
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from faster_whisper import WhisperModel
from datetime import timedelta
import logging

# Configure Logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("LyricsAI")

app = FastAPI(title="WebMusic AI Lyrics Service")

# Load Model on Startup (Cache it)
# On Mac M-series, "cpu" with "int8" is fast enough. 
# Eventually we can expose env vars to control device/quantization.
MODEL_SIZE = os.getenv("WHISPER_MODEL", "tiny")
logger.info(f"Loading Whisper Model: {MODEL_SIZE} ...")
try:
    model = WhisperModel(MODEL_SIZE, device="cpu", compute_type="int8")
    logger.info("Model loaded successfully.")
except Exception as e:
    logger.error(f"Failed to load model: {e}")
    model = None

class TranscriptionRequest(BaseModel):
    file_path: str  # Absolute path to the audio file (mapped via Docker volume or SMB)

def format_timestamp(seconds):
    td = timedelta(seconds=seconds)
    minutes, remaining_seconds = divmod(td.seconds, 60)
    milliseconds = int(td.microseconds / 10000) 
    return f"[{minutes:02d}:{remaining_seconds:02d}.{milliseconds:02d}]"

@app.get("/health")
def health_check():
    if model is None:
        raise HTTPException(status_code=503, detail="Model not loaded")
    return {"status": "ok", "model": MODEL_SIZE}

@app.post("/transcribe")
def transcribe_audio(req: TranscriptionRequest):
    if not model:
        raise HTTPException(status_code=503, detail="Model not initialized")
    
    if not os.path.exists(req.file_path):
        raise HTTPException(status_code=404, detail=f"File not found: {req.file_path}")

    logger.info(f"Transcribing: {req.file_path}")
    
    try:
        segments, info = model.transcribe(req.file_path, beam_size=5)
        
        lines = []
        full_text = []

        for segment in segments:
            timestamp = format_timestamp(segment.start)
            text = segment.text.strip()
            lines.append({
                "time": timestamp,
                "text": text
            })
            full_text.append(text)
        
        # Return structured data (C# backend can format it as LRC)
        return {
            "language": info.language,
            "language_prob": info.language_probability,
            "segments": lines,
            "full_text": " ".join(full_text)
        }

    except Exception as e:
        logger.error(f"Transcription failed: {e}")
        raise HTTPException(status_code=500, detail=str(e))

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=5001)
