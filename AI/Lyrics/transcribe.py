import sys
import os
from faster_whisper import WhisperModel
from datetime import timedelta

def format_timestamp(seconds):
    """Converts seconds (float) to LRC timestamp format [mm:ss.xx]"""
    td = timedelta(seconds=seconds)
    minutes, remaining_seconds = divmod(td.seconds, 60)
    # Milliseconds are typically 2 digits in LRC, Whisper gives precise float
    milliseconds = int(td.microseconds / 10000) 
    return f"[{minutes:02d}:{remaining_seconds:02d}.{milliseconds:02d}]"

def transcribe(audio_path, model_size="tiny", device="cpu", compute_type="int8"):
    print(f"Loading model '{model_size}' on {device} ({compute_type})...")
    
    try:
        model = WhisperModel(model_size, device=device, compute_type=compute_type)
    except Exception as e:
        print(f"Error loading model: {e}")
        return

    if not os.path.exists(audio_path):
        print(f"Error: File not found: {audio_path}")
        return

    print(f"Transcribing: {audio_path} ...")
    
    segments, info = model.transcribe(audio_path, beam_size=5)

    print(f"Detected language: {info.language} (probability: {info.language_probability:.2f})")
    print("-" * 30)

    lrc_lines = []
    
    # Process segments
    for segment in segments:
        start_time = format_timestamp(segment.start)
        text = segment.text.strip()
        line = f"{start_time}{text}"
        print(line)  # Stream output to console
        lrc_lines.append(line)

    return lrc_lines

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python transcribe.py <path_to_audio_file> [model_size]")
        sys.exit(1)

    audio_file = sys.argv[1]
    model = sys.argv[2] if len(sys.argv) > 2 else "tiny"
    
    # Mac M-series specific optimization could go here (e.g. device='auto')
    # For compatibility, starting with cpu/int8
    transcribe(audio_file, model_size=model)
