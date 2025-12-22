import os
import uvicorn
import time
from typing import Optional
from fastapi import FastAPI, HTTPException, UploadFile, File, Form
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

# Added back helper functions
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

import uuid
import tempfile
import os
from smbprotocol.connection import Connection
from smbprotocol.session import Session
from smbprotocol.tree import TreeConnect
from smbprotocol.open import Open, CreateDisposition, FilePipePrinterAccessMask, FileAttributes, ShareAccess, CreateOptions, ImpersonationLevel
from smbprotocol.exceptions import SMBResponseException

class SmbConfig(BaseModel):
    host: str
    share: str
    username: str
    password: str
    file_path: str

class TranscriptionRequest(BaseModel):
    smb_config: Optional[SmbConfig] = None
    language: Optional[str] = None
    initial_prompt: Optional[str] = None

# ... helper functions ...

def download_smb_file(cfg: SmbConfig, local_path: str):
    logger.info(f"Connecting to SMB: {cfg.host}, Share: {cfg.share}, Path: {cfg.file_path}")
    connection = Connection(uuid.uuid4(), cfg.host, 445)
    try:
        connection.connect()
        session = Session(connection, cfg.username, cfg.password)
        session.connect()
        tree = TreeConnect(session, f"\\\\{cfg.host}\\{cfg.share}")
        tree.connect()
        
        file_open = Open(tree, cfg.file_path.replace('/', '\\'))
        file_open.create(ImpersonationLevel.Impersonation,
                         FilePipePrinterAccessMask.GENERIC_READ, 
                         FileAttributes.FILE_ATTRIBUTE_NORMAL, 
                         ShareAccess.FILE_SHARE_READ | ShareAccess.FILE_SHARE_WRITE | ShareAccess.FILE_SHARE_DELETE,
                         CreateDisposition.FILE_OPEN, 
                         CreateOptions.FILE_NON_DIRECTORY_FILE)
        try:
            with open(local_path, 'wb') as f:
                offset = 0
                while True:
                    try:
                        # Read 64KB chunks
                        data = file_open.read(offset, 65536)
                        if not data:
                            break
                        f.write(data)
                        offset += len(data)
                    except SMBResponseException as e:
                        # Check for STATUS_END_OF_FILE (0xC0000011) which equals 3221225489 unsigned
                        if e.status == 0xC0000011:
                            break
                        raise e
        finally:
            file_open.close()
    finally:
        connection.disconnect()

@app.post("/transcribe")
def transcribe_audio(req: TranscriptionRequest):
    if not model:
        raise HTTPException(status_code=503, detail="Model not initialized")
    
    if not req.smb_config:
         raise HTTPException(status_code=400, detail="SMB Config required (Local files not supported in this mode)")

    tmp_file = tempfile.NamedTemporaryFile(delete=False, suffix=".wav")
    tmp_path = tmp_file.name
    tmp_file.close()

    try:
        # Download Audio from SMB
        download_smb_file(req.smb_config, tmp_path)
        
        logger.info(f"Transcribing downloaded file: {tmp_path} (Lang: {req.language})")
        
        segments, info = model.transcribe(
            tmp_path, 
            beam_size=5,
            language=req.language,
            initial_prompt=req.initial_prompt
        )
        
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
        
        return {
            "language": info.language,
            "language_prob": info.language_probability,
            "segments": lines,
            "full_text": " ".join(full_text)
        }
    except Exception as e:
        logger.error(f"Transcription failed: {str(e)}")
        raise HTTPException(status_code=500, detail=str(e))
    finally:
        if os.path.exists(tmp_path):
            os.remove(tmp_path)



if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=5001)
