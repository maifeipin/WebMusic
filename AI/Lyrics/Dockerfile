FROM python:3.10-slim

WORKDIR /app

# Install FFmpeg (Required for audio processing)
RUN apt-get update && \
    apt-get install -y ffmpeg && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Copy requirements first (Layer caching)
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy source code
COPY app.py .

# Environment Defaults
ENV WHISPER_MODEL=tiny
ENV PORT=5001

EXPOSE 5001

# Run API
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "5001"]
