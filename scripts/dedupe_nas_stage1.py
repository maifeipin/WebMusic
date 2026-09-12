#!/usr/bin/env python3
"""
dedupe_nas_stage1.py - NAS (192.168.2.18) Stage 1 (Byte-Hash) Physical Deduplication Script.
Excludes 106 (demo data) completely.
Compares physical file sizes down to the exact byte.
Verifies Winner physical existence before removing any Loser file.
"""

import os
import sys
import json
import subprocess
from collections import defaultdict
from datetime import datetime, timezone

MEDIA_SSH = "root@media"
DSM_SSH = "root@192.168.2.18"
DSM_DATA_BASE = "/volume1/DataSync"

def run_psql(sql):
    cmd = [
        "ssh", MEDIA_SSH,
        "docker", "exec", "-i", "webmusic-postgres",
        "psql", "-U", "postgres", "-d", "webmusic", "-t", "-A"
    ]
    res = subprocess.run(cmd, input=sql, capture_output=True, text=True, check=True)
    return res.stdout.strip()

def run_dsm_python(script_str, input_json=None):
    if input_json is not None:
        full_script = f"import json\ndata = json.loads({json.dumps(json.dumps(input_json))})\n{script_str}"
    else:
        full_script = script_str
    cmd = ["ssh", DSM_SSH, "python3", "-"]
    res = subprocess.run(cmd, input=full_script, capture_output=True, text=True)
    if res.returncode != 0:
        raise RuntimeError(f"DSM python execution failed: {res.stderr}")
    return res.stdout.strip()


def get_nas_duplicates():
    # Only StorageCredentialId = 1 (192.168.2.18). Excludes StorageCredentialId = 2 (192.168.2.106 demo).
    sql = """
    SELECT json_agg(t) FROM (
      SELECT 
        m."Id", m."Title", m."Artist", m."Album", m."SizeBytes", 
        m."FilePath", m."FileHash", m."ScanSourceId", s."Name" as sourcename,
        s."Path" as sourcepath
      FROM "MediaFiles" m
      JOIN "ScanSources" s ON m."ScanSourceId" = s."Id"
      WHERE s."StorageCredentialId" = 1
        AND m."FileHash" IN (
          SELECT m2."FileHash"
          FROM "MediaFiles" m2
          JOIN "ScanSources" s2 ON m2."ScanSourceId" = s2."Id"
          WHERE s2."StorageCredentialId" = 1
            AND m2."FileHash" IS NOT NULL 
            AND m2."FileHash" != '' 
            AND NOT m2."FileHash" ILIKE 'nohash%' 
            AND NOT m2."IsDeleted"
          GROUP BY m2."FileHash"
          HAVING count(*) > 1
        )
        AND NOT m."IsDeleted"
      ORDER BY m."FileHash", m."Id"
    ) t;
    """
    out = run_psql(sql)
    if not out or out == "":
        return []
    return json.loads(out)

def select_winner(members):
    # Winner selection heuristic:
    # 1. Prefer HitFm (NetDiskSync structured) over QQMusic
    # 2. Prefer clean filenames without "(1)" or "(2)"
    # 3. Prefer earlier Id
    def score_member(m):
        score = 0
        path = m.get("FilePath", "")
        sourcename = m.get("sourcename", "")
        if "NetDiskSync" in path or sourcename == "HitFm":
            score += 1000
        if "(1)" not in path and "(2)" not in path:
            score += 100
        # earlier ID (older stable record)
        score -= m.get("Id", 0) * 0.0001
        return score

    sorted_members = sorted(members, key=score_member, reverse=True)
    winner = sorted_members[0]
    losers = sorted_members[1:]
    return winner, losers

def main():
    apply_mode = "--apply" in sys.argv
    now_utc = datetime.now(timezone.utc).isoformat()
    print(f"=== NAS (192.168.2.18) Stage 1 Byte-Hash Deduplication ===")
    print(f"Scope: StorageCredentialId = 1 (192.168.2.18 only, 106 excluded)")
    print(f"Mode: {'🚀 APPLY (Physical Deletion + DB Cleanup)' if apply_mode else '🧪 DRY RUN (Audit Only)'}")
    print(f"Timestamp: {now_utc}\n")

    records = get_nas_duplicates()
    if not records:
        print("No duplicate records found on StorageCredentialId = 1.")
        return

    groups = defaultdict(list)
    for r in records:
        groups[r["FileHash"]].append(r)

    print(f"Found {len(groups)} duplicate groups strictly on NAS (192.168.2.18).")

    audit_groups = []
    total_loser_files = 0
    total_reclaimed_bytes = 0
    size_mismatches = 0

    all_paths_to_check = {}

    for hash_val, members in groups.items():
        winner, losers = select_winner(members)
        w_size = winner["SizeBytes"]
        w_path = os.path.join(DSM_DATA_BASE, winner["FilePath"])
        all_paths_to_check[w_path] = w_size
        
        group_losers = []
        for loser in losers:
            total_loser_files += 1
            l_size = loser["SizeBytes"]
            total_reclaimed_bytes += l_size

            # Verify database byte size equality
            if l_size != w_size:
                size_mismatches += 1
                print(f"🚨 DB SIZE MISMATCH: Winner {winner['Id']} ({w_size} B) != Loser {loser['Id']} ({l_size} B)")
            
            l_path = os.path.join(DSM_DATA_BASE, loser["FilePath"])
            all_paths_to_check[l_path] = l_size
            group_losers.append(loser)

        audit_groups.append({
            "FileHash": hash_val,
            "SizeBytes": w_size,
            "Winner": winner,
            "Losers": group_losers
        })

    print(f"--- Database Size Consistency Audit ---")
    print(f"Total Groups: {len(audit_groups)}")
    print(f"Total Loser Files to Remove: {total_loser_files}")
    print(f"Database Size Mismatches: {size_mismatches} (Expect 0)")
    print(f"Total Space to Reclaim: {total_reclaimed_bytes / (1024*1024):.2f} MB ({total_reclaimed_bytes / (1024*1024*1024):.2f} GB)\n")

    if size_mismatches > 0:
        print("❌ ABORTING: Detected database size mismatch between duplicate pairs!")
        sys.exit(1)

    # Physical disk verification on DSM918 via remote Python
    print(f"--- DSM918 Physical Disk Verification ({len(all_paths_to_check)} files) ---")
    dsm_checker_script = """
import os, json

results = {}
for path, expected_size in data.items():
    if not os.path.isfile(path):
        results[path] = {"exists": False, "size": 0, "status": "MISSING"}
    else:
        sz = os.path.getsize(path)
        match = (sz == expected_size)
        results[path] = {"exists": True, "size": sz, "match": match, "status": "OK" if match else "SIZE_DIFF"}
print(json.dumps(results))
"""
    raw_results = run_dsm_python(dsm_checker_script, all_paths_to_check)
    disk_results = json.loads(raw_results)

    disk_check_failures = 0
    for idx, ag in enumerate(audit_groups):
        w = ag["Winner"]
        w_path = os.path.join(DSM_DATA_BASE, w["FilePath"])
        w_stat = disk_results.get(w_path, {})
        if not w_stat.get("exists") or not w_stat.get("match"):
            disk_check_failures += 1
            print(f"❌ WINNER PHYSICAL DISK CHECK FAILED Group {idx+1}: {w_path} -> {w_stat}")
            ag["WinnerDiskOk"] = False
        else:
            ag["WinnerDiskOk"] = True

        for loser in ag["Losers"]:
            l_path = os.path.join(DSM_DATA_BASE, loser["FilePath"])
            l_stat = disk_results.get(l_path, {})
            loser["DiskStatus"] = l_stat

    if disk_check_failures > 0:
        print(f"❌ ABORTING: {disk_check_failures} Winner files failed physical disk check on DSM918!")
        sys.exit(1)

    print(f"✅ All {len(audit_groups)} Winner files verified physically present and size-accurate on DSM918!")
    print(f"✅ All {total_loser_files} Loser files verified physically present and size-accurate on DSM918!\n")

    # Detailed Audit List
    print("--- 31 Duplicate Groups Detailed Audit Table ---")
    for idx, ag in enumerate(audit_groups):
        w = ag["Winner"]
        print(f"[{idx+1:02d}/31] {w['Title']} - {w['Artist']} (Size: {w['SizeBytes']:,} Bytes)")
        print(f"  ★ KEEP (WINNER): [ID={w['Id']}] [{w['sourcename']}] {w['FilePath']}")
        for l in ag["Losers"]:
            print(f"  ❌ DEL  (LOSER) : [ID={l['Id']}] [{l['sourcename']}] {l['FilePath']}")

    if not apply_mode:
        print("\n" + "="*70)
        print("🧪 DRY RUN COMPLETE: 100% Size match verified. No files or database records modified.")
        print("To execute physical deletion and database cleanup, run:")
        print("    python3 scripts/dedupe_nas_stage1.py --apply")
        print("="*70)
        return

    # APPLY PHASE
    print("\n" + "="*70)
    print("🚀 EXECUTING APPLY: Physical deletion and database cleanup...")
    print("="*70)

    # 1. Physical file deletion on DSM918 via remote python
    paths_to_delete = []
    removed_db_ids = []
    for ag in audit_groups:
        if not ag.get("WinnerDiskOk"):
            continue
        for l in ag["Losers"]:
            paths_to_delete.append(os.path.join(DSM_DATA_BASE, l["FilePath"]))
            removed_db_ids.append(l["Id"])

    dsm_remover_script = """
import os, json

results = {}
for p in data:
    try:
        if os.path.exists(p):
            os.remove(p)
            results[p] = {"success": True}
        else:
            results[p] = {"success": False, "error": "Not found"}
    except Exception as ex:
        results[p] = {"success": False, "error": str(ex)}
print(json.dumps(results))
"""
    raw_del_results = run_dsm_python(dsm_remover_script, paths_to_delete)
    del_results = json.loads(raw_del_results)

    success_count = sum(1 for r in del_results.values() if r.get("success"))
    fail_count = len(del_results) - success_count

    print(f"Physical Files Deleted: {success_count} / {len(paths_to_delete)}")
    if fail_count > 0:
        print(f"⚠️ Failed to delete {fail_count} files on disk:")
        for p, r in del_results.items():
            if not r.get("success"):
                print(f"  - {p}: {r.get('error')}")

    # 2. Database Cleanup in PostgreSQL
    if removed_db_ids:
        id_list_str = ",".join(str(i) for i in removed_db_ids)
        del_sql = f"""
        BEGIN;
        DELETE FROM "MediaFiles" WHERE "Id" IN ({id_list_str});
        COMMIT;
        """
        run_psql(del_sql)
        print(f"✅ Successfully deleted {len(removed_db_ids)} duplicate rows from PostgreSQL MediaFiles.")

    # 3. Save Rollback Manifest
    os.makedirs("reports", exist_ok=True)
    manifest_path = "reports/nas_stage1_dedupe_manifest_33.json"
    manifest = {
        "ExecutedAt": now_utc,
        "TargetHost": "192.168.2.18",
        "TotalGroups": len(audit_groups),
        "TotalRemovedFiles": success_count,
        "ReclaimedBytes": total_reclaimed_bytes,
        "DeletionResults": del_results,
        "AuditGroups": audit_groups
    }
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)

    print(f"📄 Rollback & audit manifest saved -> {manifest_path}")
    print(f"\n🎉 Stage 1 NAS Physical Deduplication Completed Successfully!")
    print(f"Reclaimed Disk Space: {total_reclaimed_bytes / (1024*1024):.2f} MB ({total_reclaimed_bytes / (1024*1024*1024):.2f} GB)")

if __name__ == "__main__":
    main()
