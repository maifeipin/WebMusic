#!/usr/bin/env python3
"""
WebMusic Catalog Scan Worker (Mac / NAS Client)
==============================================
Runs on a Mac/NAS worker node with local access to the NAS filesystem,
calling the central MEDIA API (https://music.maifeipin.com) to drive
controlled, rate-limited Catalog Scan batches for Milestone B (P1 Expansion).

Key Specifications:
1. MEDIA node serves as the API coordinator and PostgreSQL state store.
2. Worker enforces single-process execution via a local file lock.
3. Batch size is capped at 100 tracks.
4. Request delay between tracks is strictly enforced (>= 1.5s).
5. Daily processing quota is capped (default: 2,000 tracks/day).
6. Supports --dry-run for non-intrusive preview without mutations.
"""

import argparse
import fcntl
import json
import os
import signal
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

LOCK_FILE = "/tmp/webmusic_catalog_worker.lock"
DAILY_PROGRESS_FILE = os.path.expanduser("~/.webmusic_catalog_daily.json")

class CatalogWorker:
    def __init__(self, endpoint, username, password, batch_size=100, delay=1.5, daily_limit=2000, dry_run=False):
        self.endpoint = endpoint.rstrip("/")
        self.username = username
        self.password = password
        self.batch_size = min(max(int(batch_size), 1), 100)
        self.delay = max(float(delay), 1.5)
        self.daily_limit = int(daily_limit)
        self.dry_run = dry_run
        self.token = None
        self.stop_requested = False
        self.lock_fd = None

    def acquire_lock(self):
        """Ensure only one worker process runs at a time."""
        try:
            self.lock_fd = open(LOCK_FILE, "w")
            fcntl.flock(self.lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.lock_fd.write(f"{os.getpid()}\n")
            self.lock_fd.flush()
        except (IOError, BlockingIOError):
            print(f"❌ [Worker Error] Another instance of catalog_worker is already running (Lock: {LOCK_FILE}). Exiting.")
            sys.exit(1)

    def release_lock(self):
        if self.lock_fd:
            try:
                fcntl.flock(self.lock_fd, fcntl.LOCK_UN)
                self.lock_fd.close()
            except Exception:
                pass
            if os.path.exists(LOCK_FILE):
                try:
                    os.remove(LOCK_FILE)
                except Exception:
                    pass

    def setup_signals(self):
        def handle_signal(sig, frame):
            print("\n⚠️ [Worker Notice] Graceful shutdown signal received. Finishing active step and stopping...")
            self.stop_requested = True
        signal.signal(signal.SIGINT, handle_signal)
        signal.signal(signal.SIGTERM, handle_signal)

    def _request(self, path, method="GET", data=None):
        url = f"{self.endpoint}{path}"
        headers = {"Content-Type": "application/json"}
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"

        encoded_data = json.dumps(data).encode("utf-8") if data is not None else None
        req = urllib.request.Request(url, data=encoded_data, headers=headers, method=method)

        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                body = resp.read().decode("utf-8")
                return resp.status, json.loads(body) if body else {}
        except urllib.error.HTTPError as e:
            try:
                err_body = e.read().decode("utf-8")
                err_json = json.loads(err_body)
            except Exception:
                err_json = {"error": e.reason}
            return e.code, err_json
        except Exception as ex:
            return 0, {"error": str(ex)}

    def authenticate(self):
        print(f"🔑 Authenticating as '{self.username}' at {self.endpoint}...")
        status, res = self._request("/api/auth/login", method="POST", data={
            "username": self.username,
            "password": self.password
        })
        if status == 200 and "token" in res:
            self.token = res["token"]
            print("✅ Authenticated successfully.")
            return True
        else:
            print(f"❌ Authentication failed (HTTP {status}): {res}")
            return False

    def get_preview(self, cursor=None):
        query = f"?cursor={cursor}" if cursor is not None else ""
        status, res = self._request(f"/api/enrichment/catalog/preview{query}")
        return status, res

    def get_daily_processed(self):
        today = time.strftime("%Y-%m-%d")
        if os.path.exists(DAILY_PROGRESS_FILE):
            try:
                with open(DAILY_PROGRESS_FILE, "r") as f:
                    data = json.load(f)
                    if data.get("date") == today:
                        return data.get("processed", 0)
            except Exception:
                pass
        return 0

    def record_daily_processed(self, count):
        today = time.strftime("%Y-%m-%d")
        current = self.get_daily_processed()
        updated = current + count
        try:
            with open(DAILY_PROGRESS_FILE, "w") as f:
                json.dump({"date": today, "processed": updated, "last_updated": time.strftime("%Y-%m-%dT%H:%M:%SZ")}, f)
        except Exception as ex:
            print(f"⚠️ Failed to write daily progress file: {ex}")
        return updated

    def run(self, start_cursor=None):
        self.acquire_lock()
        self.setup_signals()

        try:
            if not self.authenticate():
                return 1

            print("\n" + "=" * 60)
            print("🎵 WebMusic Catalog Scan Worker (Mac/NAS Topology)")
            print(f"   Target Endpoint:   {self.endpoint}")
            print(f"   Batch Size:        {self.batch_size} (Max: 100)")
            print(f"   Per-Request Delay: {self.delay}s (Min: 1.5s)")
            print(f"   Daily Limit:       {self.daily_limit} tracks")
            print(f"   Dry Run Mode:      {self.dry_run}")
            print("=" * 60 + "\n")

            # Check candidate preview
            status, preview = self.get_preview(start_cursor)
            if status != 200:
                print(f"❌ Failed to fetch catalog preview (HTTP {status}): {preview}")
                return 1

            total_eligible = preview.get("totalEligible", 0)
            next_cursor = preview.get("nextCursor", 0)
            print(f"📊 Catalog Status: {total_eligible} eligible tracks awaiting P1 enrichment.")
            print(f"📍 Starting Cursor: {start_cursor if start_cursor is not None else 'Auto (0)'}")

            if total_eligible == 0:
                print("🎉 No tracks currently require catalog enrichment. Everything is up to date!")
                return 0

            if self.dry_run:
                print("\n🔍 [Dry-Run] Preview sample candidates:")
                for item in preview.get("sample", []):
                    print(f"   - [ID {item.get('Id')}] {item.get('Artist')} - {item.get('Title')} ({item.get('Album', 'No Album')})")
                print("\n✅ Dry run completed. No batches were dispatched. Exiting cleanly.")
                return 0

            # Active batch dispatching loop
            processed_today = self.get_daily_processed()
            print(f"📅 Daily Progress: {processed_today}/{self.daily_limit} tracks processed today.")

            current_cursor = start_cursor
            while not self.stop_requested:
                if processed_today >= self.daily_limit:
                    print(f"\n🛑 Daily limit reached ({processed_today}/{self.daily_limit}). Worker stopping for today.")
                    break

                batch_to_run = min(self.batch_size, self.daily_limit - processed_today)
                print(f"\n🚀 Dispatching next Catalog Batch (batchSize={batch_to_run}, cursor={current_cursor or 0})...")
                
                start_path = f"/api/enrichment/catalog/start?batchSize={batch_to_run}"
                if current_cursor is not None:
                    start_path += f"&cursor={current_cursor}"

                status, batch_res = self._request(start_path, method="POST")
                if status != 200:
                    print(f"❌ Failed to start catalog batch (HTTP {status}): {batch_res}")
                    break

                batch_id = batch_res.get("batchId")
                total_in_batch = batch_res.get("total", 0)
                next_cursor = batch_res.get("nextCursor", 0)

                if not batch_id or total_in_batch == 0:
                    print(f"🏁 No further tracks to process from cursor {current_cursor}. Catalog scan complete.")
                    break

                print(f"✅ Batch created: ID {batch_id} with {total_in_batch} tracks. Next cursor: {next_cursor}")

                # Monitor batch
                completed = False
                while not completed and not self.stop_requested:
                    time.sleep(2.0)
                    stat_code, stat_data = self._request(f"/api/enrichment/{batch_id}?allUsers=true")
                    if stat_code == 200:
                        p = stat_data.get("processed", 0)
                        t = stat_data.get("total", total_in_batch)
                        u = stat_data.get("updated", 0)
                        um = stat_data.get("unmatched", 0)
                        sk = stat_data.get("skipped", 0)
                        fl = stat_data.get("failed", 0)
                        st = stat_data.get("status")
                        print(f"   [{time.strftime('%H:%M:%S')}] Status: {st} | Progress: {p}/{t} | Updated: {u} | Unmatched: {um} | Skipped: {sk} | Failed: {fl}")
                        if st in ("Completed", "Failed", "Paused"):
                            completed = True
                    else:
                        print(f"⚠️ Error checking status: HTTP {stat_code}")

                processed_today = self.record_daily_processed(total_in_batch)
                print(f"🏁 Batch {batch_id} finished. Daily count now: {processed_today}/{self.daily_limit}")
                current_cursor = next_cursor

                # Rest between batches
                if not self.stop_requested and processed_today < self.daily_limit:
                    print(f"⏳ Sleeping {self.delay}s before next batch...")
                    time.sleep(self.delay)

            return 0

        finally:
            self.release_lock()

def main():
    parser = argparse.ArgumentParser(description="WebMusic Catalog Scan Worker for Mac/NAS")
    parser.add_argument("--endpoint", default=os.getenv("ENDPOINT", "https://music.maifeipin.com"), help="MEDIA API URL")
    parser.add_argument("--username", default=os.getenv("BOT_USERNAME", "enrichment-bot"), help="Bot username")
    parser.add_argument("--password", default=os.getenv("BOT_PASSWORD") or os.getenv("ENRICHMENT_BOT_PASSWORD"), help="Bot password")
    parser.add_argument("--batch-size", type=int, default=100, help="Tracks per batch (max 100)")
    parser.add_argument("--delay", type=float, default=1.5, help="Seconds delay between items/requests (min 1.5)")
    parser.add_argument("--daily-limit", type=int, default=2000, help="Max tracks to process per day")
    parser.add_argument("--cursor", type=int, default=None, help="Explicit starting cursor (MediaFileId)")
    parser.add_argument("--dry-run", action="store_true", help="Preview status and candidates without dispatching")

    args = parser.parse_args()

    if not args.password:
        print("❌ Error: Bot password must be specified via --password or ENRICHMENT_BOT_PASSWORD environment variable.")
        sys.exit(1)

    worker = CatalogWorker(
        endpoint=args.endpoint,
        username=args.username,
        password=args.password,
        batch_size=args.batch_size,
        delay=args.delay,
        daily_limit=args.daily_limit,
        dry_run=args.dry_run
    )
    sys.exit(worker.run(start_cursor=args.cursor))

if __name__ == "__main__":
    main()
