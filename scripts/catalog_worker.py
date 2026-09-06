#!/usr/bin/env python3
"""
WebMusic Catalog Enrichment Worker (Mac/NAS External Execution Worker)

This worker executes actual external HTTP calls (MusicBrainz, CAA, LRCLIB) directly
from the Mac/NAS node, respecting rate limits (>= 1.5s) and offloading the MEDIA server.
Coordination is handled via the MEDIA server's database lease protocol:
  1. POST /api/enrichment/worker/lease-batch    (Claims tracks, enforces global 2000/day limit)
  2. POST /api/enrichment/worker/heartbeat      (Renews lease while in-flight)
  3. POST /api/enrichment/worker/submit-batch   (Submits downloaded covers, lyrics, outcomes)
"""

import argparse
import base64
import hashlib
import json
import math
import os
import platform
import re
import socket
import sys
import threading
import time
import unicodedata
import urllib.parse
import urllib.request
import urllib.error
import uuid

# Default Configuration
DEFAULT_API_URL = os.environ.get("WEBMUSIC_URL", "https://music.maifeipin.com")
BOT_USERNAME = os.environ.get("WORKER_USERNAME", os.environ.get("BOT_USERNAME", "catalog-worker"))
BOT_PASSWORD = os.environ.get("WORKER_SECRET", os.environ.get("ENRICHMENT_WORKER_SECRET", os.environ.get("BOT_PASSWORD", "")))
DEFAULT_WORKER_NODE_ID = os.environ.get("WORKER_NODE_ID", f"mac-worker-{platform.node()[:16]}")
DEFAULT_BATCH_SIZE = int(os.environ.get("BATCH_SIZE", "50"))
PID_FILE = "/tmp/webmusic_catalog_worker.pid"
MIN_REQUEST_INTERVAL = 1.5  # Seconds between external MusicBrainz requests
USER_AGENT = "WebMusic/1.0 (https://music.maifeipin.com; contact@maifeipin.com)"


class LocalPidLock:
    def __init__(self, path):
        self.path = path

    def __enter__(self):
        if os.path.exists(self.path):
            try:
                with open(self.path, "r") as f:
                    old_pid = int(f.read().strip())
                # Check if process is still running
                os.kill(old_pid, 0)
                raise RuntimeError(f"Another worker instance is already running with PID {old_pid}")
            except (ValueError, ProcessLookupError, OSError):
                # Stale PID file
                pass
        with open(self.path, "w") as f:
            f.write(str(os.getpid()))
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        try:
            if os.path.exists(self.path):
                os.remove(self.path)
        except OSError:
            pass


class WorkerClient:
    def __init__(self, base_url, username, password, node_id):
        self.base_url = base_url.rstrip("/")
        self.username = username
        self.password = password
        self.node_id = node_id
        self.token = None
        self.last_mb_request_time = 0

    def login(self):
        if not self.password:
            raise ValueError("BOT_PASSWORD must be provided. Empty passwords are not permitted.")
        url = f"{self.base_url}/api/auth/login"
        payload = json.dumps({
            "username": self.username,
            "password": self.password
        }).encode("utf-8")
        req = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=15) as resp:
                data = json.loads(resp.read().decode("utf-8"))
                self.token = data.get("token")
                if not self.token:
                    raise RuntimeError("No token returned in login response.")
                print(f"✅ Authenticated as {self.username} (Worker Node: {self.node_id})")
        except urllib.error.HTTPError as e:
            err_body = e.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"Authentication failed (HTTP {e.code}): {err_body}")

    def _auth_headers(self):
        return {
            "Authorization": f"Bearer {self.token}",
            "Content-Type": "application/json",
            "User-Agent": f"WebMusic-CatalogWorker/2.0 ({self.node_id})"
        }

    def get_preview(self):
        url = f"{self.base_url}/api/enrichment/worker/preview"
        req = urllib.request.Request(url, headers=self._auth_headers())
        with urllib.request.urlopen(req, timeout=60) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def lease_batch(self, batch_size, specific_media_file_id=None):
        url = f"{self.base_url}/api/enrichment/worker/lease-batch"
        data_dict = {
            "workerNodeId": self.node_id,
            "batchSize": batch_size
        }
        if specific_media_file_id is not None:
            data_dict["specificMediaFileId"] = specific_media_file_id
        payload = json.dumps(data_dict).encode("utf-8")
        req = urllib.request.Request(url, data=payload, headers=self._auth_headers())
        try:
            with urllib.request.urlopen(req, timeout=60) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            err_body = e.read().decode("utf-8", errors="replace")
            if e.code == 429:
                return {"quotaReached": True, "detail": err_body}
            raise RuntimeError(f"Lease request failed (HTTP {e.code}): {err_body}")

    def send_heartbeat(self, batch_id, item_ids):
        url = f"{self.base_url}/api/enrichment/worker/heartbeat"
        payload = json.dumps({
            "batchId": batch_id,
            "workerNodeId": self.node_id,
            "itemIds": item_ids
        }).encode("utf-8")
        req = urllib.request.Request(url, data=payload, headers=self._auth_headers())
        try:
            with urllib.request.urlopen(req, timeout=15) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception as e:
            print(f"⚠️ Heartbeat error: {e}")
            return None

    def upload_cover(self, item_id, image_bytes):
        url = f"{self.base_url}/api/enrichment/worker/items/{item_id}/upload-cover?workerNodeId={urllib.parse.quote(self.node_id)}"
        req = urllib.request.Request(
            url,
            data=image_bytes,
            headers={
                "Authorization": f"Bearer {self.token}",
                "Content-Type": "application/octet-stream"
            }
        )
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception as e:
            print(f"⚠️ Failed to stream upload cover for item {item_id}: {e}")
            return None

    def submit_batch(self, batch_id, results, submission_id=None):
        url = f"{self.base_url}/api/enrichment/worker/submit-batch"
        payload = json.dumps({
            "batchId": batch_id,
            "workerNodeId": self.node_id,
            "submissionId": submission_id or str(uuid.uuid4()),
            "results": results
        }).encode("utf-8")
        req = urllib.request.Request(url, data=payload, headers=self._auth_headers())
        with urllib.request.urlopen(req, timeout=60) as resp:
            return json.loads(resp.read().decode("utf-8"))

    # External Fetchers
    def _rate_limit_musicbrainz(self):
        elapsed = time.time() - self.last_mb_request_time
        if elapsed < MIN_REQUEST_INTERVAL:
            time.sleep(MIN_REQUEST_INTERVAL - elapsed)
        self.last_mb_request_time = time.time()

    def query_musicbrainz(self, title, artist, duration_seconds):
        clean_title = re.sub(r'["\\]', "", title)
        clean_artist = re.sub(r'["\\]', "", artist)
        query = f'recording:"{clean_title}" AND artist:"{clean_artist}"'
        encoded_query = urllib.parse.quote(query)
        url = f"https://musicbrainz.org/ws/2/recording/?fmt=json&limit=5&query={encoded_query}"

        max_retries = 1
        retries_done = 0

        for attempt in range(max_retries + 1):
            self._rate_limit_musicbrainz()
            req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            try:
                with urllib.request.urlopen(req, timeout=20) as resp:
                    data = json.loads(resp.read().decode("utf-8"))
                    recordings = data.get("recordings", [])
                    best_candidate = None
                    best_confidence = 0.0

                    for rec in recordings:
                        cand_title = rec.get("title", "")
                        credits = rec.get("artist-credit", [])
                        cand_artist = ", ".join(c.get("name", "") for c in credits if isinstance(c, dict))
                        cand_length_ms = rec.get("length")
                        cand_duration = (cand_length_ms / 1000.0) if cand_length_ms else 0.0

                        confidence = calculate_confidence(title, artist, duration_seconds, cand_title, cand_artist, cand_duration)
                        if confidence > best_confidence:
                            best_confidence = confidence
                            releases = rec.get("releases", [])
                            release_id = releases[0].get("id") if releases else None
                            best_candidate = {
                                "recordingId": rec.get("id"),
                                "releaseId": release_id,
                                "confidence": round(confidence, 4),
                                "title": cand_title,
                                "artist": cand_artist
                            }

                    return 200, retries_done, best_candidate, "OK"

            except urllib.error.HTTPError as e:
                # HTTP error (e.g. 503, 502, 504, 429)
                if e.code in (429, 502, 503, 504) and attempt < max_retries:
                    retries_done += 1
                    backoff = 2.0 * (attempt + 1)
                    print(f"    ⏳ MusicBrainz HTTP {e.code}, retrying in {backoff:.1f}s (attempt {attempt + 1}/{max_retries})...")
                    time.sleep(backoff)
                    continue
                return e.code, retries_done, None, f"MusicBrainz HTTP {e.code}"

            except (urllib.error.URLError, TimeoutError, socket.timeout, ConnectionError, OSError) as e:
                # Transport error (timeout, DNS failure, connection reset)
                error_msg = f"Transport error: {type(e).__name__}: {e}"
                if attempt < max_retries:
                    retries_done += 1
                    backoff = 2.0 * (attempt + 1)
                    print(f"    ⏳ MusicBrainz transport error ({type(e).__name__}), retrying in {backoff:.1f}s (attempt {attempt + 1}/{max_retries})...")
                    time.sleep(backoff)
                    continue
                return None, retries_done, None, error_msg

            except Exception as e:
                return None, retries_done, None, f"Unexpected error: {type(e).__name__}: {e}"

    def download_cover_art(self, release_id):
        if not release_id:
            return None
        url = f"https://coverartarchive.org/release/{release_id}/front-250"
        req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        try:
            with urllib.request.urlopen(req, timeout=25) as resp:
                data = resp.read()
                if len(data) < 12 or len(data) > 5 * 1024 * 1024:
                    return None

                # Verify image magic bytes (JPEG, PNG, WebP)
                if data[:3] == b"\xff\xd8\xff" or data[:8] == b"\x89PNG\r\n\x1a\n" or (data[:4] == b"RIFF" and data[8:12] == b"WEBP"):
                    return data
                return None
        except Exception:
            return None

    def fetch_lyrics_lrclib(self, title, artist, album, duration_seconds):
        params = {
            "track_name": title,
            "artist_name": artist,
            "duration": str(int(round(duration_seconds)))
        }
        if album and not album.lower().startswith("unknown"):
            params["album_name"] = album
        query = urllib.parse.urlencode(params)
        url = f"https://lrclib.net/api/get?{query}"
        req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        try:
            with urllib.request.urlopen(req, timeout=20) as resp:
                data = json.loads(resp.read().decode("utf-8"))
                synced = data.get("syncedLyrics")
                plain = data.get("plainLyrics")
                content = None
                is_synced = False
                if synced and synced.strip():
                    content = synced.strip()
                    is_synced = True
                elif plain and plain.strip():
                    content = plain.strip()
                    is_synced = False

                if content and len(content.encode("utf-8")) <= 64 * 1024:
                    return content, is_synced
        except Exception:
            pass
        return None, False


# Scoring and normalization
def normalize_string(s):
    if not s:
        return ""
    decomposed = unicodedata.normalize("NFKD", s)
    cleaned = "".join(c.lower() for c in decomposed if not unicodedata.combining(c) and c.isalnum())
    return cleaned

def similarity(a, b):
    na = normalize_string(a)
    nb = normalize_string(b)
    if na == nb:
        return 1.0
    if not na or not nb:
        return 0.0
    if na in nb or nb in na:
        return 0.9

    # Levenshtein distance
    m, n = len(na), len(nb)
    prev = list(range(n + 1))
    for i in range(1, m + 1):
        curr = [i] + [0] * n
        for j in range(1, n + 1):
            cost = 0 if na[i - 1] == nb[j - 1] else 1
            curr[j] = min(curr[j - 1] + 1, prev[j] + 1, prev[j - 1] + cost)
        prev = curr
    dist = prev[n]
    return 1.0 - (dist / max(m, n))

def calculate_confidence(title1, artist1, dur1, title2, artist2, dur2):
    title_score = similarity(title1, title2)
    artist_score = similarity(artist1, artist2)
    diff = abs(dur1 - dur2) if dur1 > 0 and dur2 > 0 else 0
    dur_score = 0.6 if dur2 == 0 else (1.0 if diff <= 2 else (0.95 if diff <= 5 else (0.85 if diff <= 10 else (0.65 if diff <= 20 else 0.0))))
    if title_score < 0.85 or artist_score < 0.85 or diff > 10:
        return 0.0
    return title_score * 0.55 + artist_score * 0.35 + dur_score * 0.10


def run_worker_batch(client, batch_size=50, target_id=None):
    if target_id is not None:
        print(f"\n--- 📦 Requesting Targeted Lease for MediaFile ID {target_id} ---")
        lease = client.lease_batch(1, specific_media_file_id=target_id)
    else:
        print(f"\n--- 📦 Requesting Batch Lease (batchSize={batch_size}) ---")
        lease = client.lease_batch(batch_size)

    if lease.get("quotaReached"):
        print(f"🛑 Quota reached: {lease.get('detail')}")
        return False, 0

    batch_id = lease.get("batchId")
    items = lease.get("items", [])
    if not batch_id or not items:
        print(f"ℹ️ No eligible items returned for leasing: {lease.get('message', 'None')}")
        return False, 0

    print(f"✅ Claimed Lease {batch_id} with {len(items)} tracks. Processing external requests on Mac/NAS...")

    # Heartbeat thread
    stop_heartbeat = threading.Event()
    item_ids = [it["itemId"] for it in items]

    def heartbeat_loop():
        while not stop_heartbeat.wait(120):  # Heartbeat every 2 mins
            client.send_heartbeat(batch_id, item_ids)

    hb_thread = threading.Thread(target=heartbeat_loop, daemon=True)
    hb_thread.start()

    results = []
    try:
        for idx, item in enumerate(items, 1):
            item_id = item["itemId"]
            media_id = item["mediaFileId"]
            title = item["title"]
            artist = item["artist"]
            album = item.get("album") or ""
            duration = item.get("durationSeconds", 0)
            needs_cover = item.get("needsCover", False)
            needs_lyrics = item.get("needsLyrics", False)

            print(f"[{idx}/{len(items)}] ID {media_id}: '{title}' - '{artist}' (needs: {'cover ' if needs_cover else ''}{'lyrics' if needs_lyrics else ''})")

            # 1. Query MusicBrainz
            status_code, retries, candidate, detail = client.query_musicbrainz(title, artist, duration)

            if status_code != 200:
                results.append({
                    "itemId": item_id,
                    "mediaFileId": media_id,
                    "outcome": "Failed",
                    "httpStatus": status_code,
                    "retryCount": retries,
                    "detail": detail,
                    "mbRequestsCount": retries + 1,
                    "caaRequestsCount": 0,
                    "lrcRequestsCount": 0
                })
                print(f"    ❌ MusicBrainz query failed (status={status_code}, retries={retries}): {detail}")
                continue

            if not candidate or candidate["confidence"] < 0.90:
                conf = candidate["confidence"] if candidate else 0.0
                results.append({
                    "itemId": item_id,
                    "mediaFileId": media_id,
                    "outcome": "Unmatched",
                    "confidence": conf,
                    "httpStatus": 200,
                    "retryCount": retries,
                    "detail": f"Confidence {conf:.2f} < 0.90 threshold.",
                    "mbRequestsCount": retries + 1,
                    "caaRequestsCount": 0,
                    "lrcRequestsCount": 0
                })
                print(f"    ⚠️ Unmatched (Confidence {conf:.2f}, retries={retries})")
                continue

            # Matched! Check assets
            recording_id = candidate["recordingId"]
            release_id = candidate["releaseId"]
            confidence = candidate["confidence"]
            has_cover = False
            lyrics_content = None
            lyrics_synced = False

            if needs_cover and release_id:
                cover_bytes = client.download_cover_art(release_id)
                if cover_bytes:
                    print("    🖼️ Downloaded Cover Art, streaming to server...")
                    upload_res = client.upload_cover(item_id, cover_bytes)
                    if upload_res and upload_res.get("success"):
                        has_cover = True
                        print("    ✅ Streamed Cover Art uploaded successfully")

            if needs_lyrics:
                lyrics_content, lyrics_synced = client.fetch_lyrics_lrclib(title, artist, album, duration)
                if lyrics_content:
                    print(f"    📝 Downloaded Lyrics ({'synced' if lyrics_synced else 'plain'})")

            outcome = "Matched" if (has_cover or lyrics_content) else "MatchedWithoutAssets"
            results.append({
                "itemId": item_id,
                "mediaFileId": media_id,
                "outcome": outcome,
                "recordingId": recording_id,
                "releaseId": release_id,
                "confidence": confidence,
                "httpStatus": 200,
                "retryCount": retries,
                "detail": f"Matched recording {recording_id} (conf: {confidence:.2f})",
                "lyricsContent": lyrics_content,
                "lyricsSynced": lyrics_synced,
                "mbRequestsCount": retries + 1,
                "caaRequestsCount": 1 if (needs_cover and release_id) else 0,
                "lrcRequestsCount": 1 if needs_lyrics else 0
            })
            print(f"    ✅ Result: {outcome} (retries={retries})")

    finally:
        stop_heartbeat.set()

    print(f"\n📤 Submitting batch {batch_id} ({len(results)} items) to MEDIA server...")
    submit_resp = client.submit_batch(batch_id, results)
    print(f"🎉 Batch {batch_id} submitted successfully: "
          f"Processed={submit_resp.get('processed')}, "
          f"IgnoredOrExpired={submit_resp.get('ignoredOrExpired')}, "
          f"Updated={submit_resp.get('updated')}, "
          f"Unmatched={submit_resp.get('unmatched')}, "
          f"Skipped={submit_resp.get('skipped')}, "
          f"Failed={submit_resp.get('failed')}")

    return True, len(results)


def main():
    parser = argparse.ArgumentParser(description="WebMusic Catalog Worker Node Client")
    parser.add_argument("--url", default=DEFAULT_API_URL, help=f"WebMusic API URL (default: {DEFAULT_API_URL})")
    parser.add_argument("--username", default=BOT_USERNAME, help=f"Bot username (default: {BOT_USERNAME})")
    parser.add_argument("--password", default=BOT_PASSWORD, help="Bot password (or BOT_PASSWORD env var)")
    parser.add_argument("--node-id", default=DEFAULT_WORKER_NODE_ID, help=f"Worker node ID (default: {DEFAULT_WORKER_NODE_ID})")
    parser.add_argument("--batch-size", type=int, default=DEFAULT_BATCH_SIZE, help=f"Batch size (default: {DEFAULT_BATCH_SIZE})")
    parser.add_argument("--target-id", type=int, default=None, help="Specific MediaFile ID to lease and test (single-song mode)")
    parser.add_argument("--preview", action="store_true", help="Preview catalog statistics and top prioritized tracks, then exit")
    parser.add_argument("--dry-run", action="store_true", help="Authenticate, lease preview, do not execute external calls")
    parser.add_argument("--run-once", action="store_true", help="Process a single batch, then exit")
    parser.add_argument("--daemon", action="store_true", help="Run continuously in background until daily quota is met")

    args = parser.parse_args()

    password = args.password or os.environ.get("WORKER_SECRET") or os.environ.get("BOT_PASSWORD", "")
    if not password:
        print("❌ Error: Worker secret must be specified via WORKER_SECRET, BOT_PASSWORD, or --password.")
        sys.exit(1)

    client = WorkerClient(args.url, args.username, password, args.node_id)
    client.login()

    if args.preview:
        print("\n--- 🔍 Catalog Enrichment Preview ---")
        preview = client.get_preview()
        print(f"Total Eligible Candidates: {preview.get('totalEligible')}")
        print(f"Completed Today (Global): {preview.get('completedToday')} / {preview.get('dailyQuota')}")
        print(f"Remaining Global Quota:   {preview.get('remainingToday')}")

        providers = preview.get("providers", {})
        if providers:
            print("\nProvider Daily Quota Status:")
            for p_name, p_info in providers.items():
                print(f"  {p_name:18s}: Limit={p_info.get('dailyLimit')}, Consumed={p_info.get('consumed')}, Reserved={p_info.get('reserved')}, Remaining={p_info.get('remaining')}")
        print("\nTop Prioritized Tracks:")
        for t in preview.get("samplePrioritizedTracks", []):
            print(f"  [Score: {t['score']:4d}] ID {t['id']}: '{t['title']}' - '{t['artist']}' (Needs: {'Cover ' if t['needsCover'] else ''}{'Lyrics' if t['needsLyrics'] else ''})")
        sys.exit(0)

    if args.dry_run:
        print("\n--- 🧪 Dry-Run Mode ---")
        preview = client.get_preview()
        print(f"Total eligible: {preview.get('totalEligible')}, remaining today: {preview.get('remainingToday')}")
        print("✅ Authentication, connection, and catalog preview verified. Dry-run complete.")
        sys.exit(0)

    # Live execution requires PID lock
    with LocalPidLock(PID_FILE):
        if args.target_id is not None:
            run_worker_batch(client, batch_size=1, target_id=args.target_id)
            sys.exit(0)

        if args.run_once:
            run_worker_batch(client, args.batch_size)
            sys.exit(0)

        if args.daemon:
            print("🚀 Worker starting in daemon mode...")
            consecutive_empty = 0
            while True:
                had_items, count = run_worker_batch(client, args.batch_size)
                if not had_items or count == 0:
                    consecutive_empty += 1
                    if consecutive_empty >= 3:
                        print("💤 No more items or daily quota reached. Sleeping for 15 minutes...")
                        time.sleep(900)
                        consecutive_empty = 0
                    else:
                        time.sleep(10)
                else:
                    consecutive_empty = 0
                    time.sleep(5)


if __name__ == "__main__":
    main()
