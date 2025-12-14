import requests
import json

url = "http://localhost:5001/transcribe"
payload = {
    "smb_config": {
        "host": "127.0.0.1",
        "share": "dummy",
        "username": "guest",
        "password": "",
        "file_path": "dummy.wav"
    },
    "language": "en"
}

try:
    print(f"Sending POST to {url}...")
    r = requests.post(url, json=payload)
    print(f"Status: {r.status_code}")
    print("Response Body:")
    print(r.text)
except Exception as e:
    print(f"Connection Failed: {e}")
