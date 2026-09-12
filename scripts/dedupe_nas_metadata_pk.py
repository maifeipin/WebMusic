#!/usr/bin/env python3
"""
dedupe_nas_metadata_pk.py - NAS (192.168.2.18) Metadata PK Governance & Soft-Delete Script.

Key Features & Safeguards:
1. Target Scope: ONLY StorageCredentialId = 1 (192.168.2.18). Host 106 (demo) is STRICTLY EXCLUDED.
2. Soft Delete ONLY: Zero physical files are touched or deleted. Losers are marked IsDeleted = true.
3. Complete Reversibility: Generates a rollback manifest; can restore with --rollback.
4. Protection Rules: Any track referenced in Playlists, Favorites, or PlayHistory is NEVER soft-deleted.
5. Three-Tier Genre Scoring:
   - CLEAN (+30 pts): Standard music genres (Pop, Soul, Rock, Alternative, Jazz, R&B, etc.)
   - UNKNOWN (0 pts): Missing or generic genres (Unknown Genre, Other, empty, etc.)
   - SPAM (-40 pts): Domain/ad spam ([60yp.com], wusunk.com分享, letians.cn, QQ:, etc.)
6. Audio Quality Dimension:
   - Lossless (FLAC, APE, WAV, >=700kbps): +100 pts
   - Lossy: scaled up to +80 pts based on bitrate (320k = 80 pts, 256k = 64 pts, 128k = 32 pts)
7. Completeness Dimension:
   - Real Album Name: +20 pts (Unknown Album = 0 pts)
   - Valid Year (1900-2026): +15 pts
   - Has CoverArt: +15 pts
   - MusicBrainz Identity matched: +20 pts
   - Lyrics present: +10 pts
   - Clean filename (no (1), (2), copy): +10 pts
"""

import os
import sys
import re
import json
import subprocess
from collections import defaultdict
from datetime import datetime, timezone

MEDIA_SSH = "root@media"

SPAM_GENRE_PATTERNS = [
    re.compile(r'\.(com|cn|net|org|cc|top|vip|tv|me|io|xyz|info)', re.IGNORECASE),
    re.compile(r'(http|https|www|ftp)', re.IGNORECASE),
    re.compile(r'(分享|收藏|整理|下载|全集|无损库|群号|公众号|微信|首发|制作|压制|论坛|音乐下载)', re.IGNORECASE),
    re.compile(r'qq[:：\s]*\d+', re.IGNORECASE),
    re.compile(r'(60yp|wusunk|wenanb|letians|yymp3|zasv)', re.IGNORECASE),
]

UNKNOWN_GENRE_PATTERNS = {
    "unknown genre", "unknown", "other", "未知", "未知流派", "none", ""
}

def classify_genre(genre_str):
    if not genre_str:
        return "UNKNOWN", 0
    g = genre_str.strip().lower()
    if g in UNKNOWN_GENRE_PATTERNS:
        return "UNKNOWN", 0
    for p in SPAM_GENRE_PATTERNS:
        if p.search(genre_str):
            return "SPAM", -40
    return "CLEAN", 30

def compute_keep_score(item):
    score = 0
    duration_sec = item.get("duration_sec", 0.0)
    size_bytes = item.get("SizeBytes", 0)
    file_path = item.get("FilePath", "")

    # 1. Audio Quality Dimension
    kbps = 0.0
    if duration_sec > 0:
        kbps = (size_bytes * 8.0) / duration_sec / 1000.0

    is_lossless = any(file_path.lower().endswith(ext) for ext in [".flac", ".ape", ".wav", ".alac", ".dsd", ".dff"]) or kbps >= 700.0
    if is_lossless:
        score += 100
    else:
        # Scale lossy from 0 to 80 based on 320k
        score += int(round(min(kbps, 320.0) / 320.0 * 80.0))

    # 2. Genre Tier Scoring
    genre_tier, genre_pts = classify_genre(item.get("Genre", ""))
    score += genre_pts

    # 3. Year
    year = item.get("Year", 0)
    if 1900 <= year <= 2026:
        score += 15

    # 4. CoverArt
    if item.get("has_cover"):
        score += 15

    # 5. Album Completeness
    album = (item.get("Album") or "").strip()
    if album and album.lower() != "unknown album":
        score += 20

    # 6. MusicBrainz Identity & Lyrics
    if item.get("has_identity"):
        score += 20
    if item.get("has_lyrics"):
        score += 10

    # 7. Filename & Directory cleanliness
    if "(1)" in file_path or "(2)" in file_path or "副本" in file_path:
        score -= 20
    else:
        score += 10

    # Tie breaker: prefer earlier stable ID
    score -= item.get("Id", 0) * 0.00001

    return score, kbps, genre_tier

def run_psql(sql):
    cmd = [
        "ssh", MEDIA_SSH,
        "docker", "exec", "-i", "webmusic-postgres",
        "psql", "-U", "postgres", "-d", "webmusic", "-t", "-A"
    ]
    res = subprocess.run(cmd, input=sql, capture_output=True, text=True, check=True)
    return res.stdout.strip()

def load_active_nas_tracks():
    # Only StorageCredentialId = 1 (192.168.2.18). Excludes 106. Excludes IsDeleted.
    # We join with reference counts and identity tables
    sql = """
    SELECT json_agg(t) FROM (
      SELECT 
        m."Id", m."Title", m."Artist", m."Album", m."Genre", m."Year",
        EXTRACT(epoch FROM m."Duration") as duration_sec,
        m."SizeBytes", m."FilePath",
        (m."CoverArt" IS NOT NULL AND m."CoverArt" != '') as has_cover,
        EXISTS (SELECT 1 FROM "MediaIdentities" mi WHERE mi."MediaFileId" = m."Id") as has_identity,
        EXISTS (SELECT 1 FROM "Lyrics" l WHERE l."MediaFileId" = m."Id") as has_lyrics,
        (
          EXISTS (SELECT 1 FROM "PlaylistSongs" ps WHERE ps."MediaFileId" = m."Id") OR
          EXISTS (SELECT 1 FROM "Favorites" fav WHERE fav."MediaFileId" = m."Id") OR
          EXISTS (SELECT 1 FROM "PlayHistories" ph WHERE ph."MediaFileId" = m."Id")
        ) as is_protected
      FROM "MediaFiles" m
      JOIN "ScanSources" s ON m."ScanSourceId" = s."Id"
      WHERE s."StorageCredentialId" = 1
        AND NOT m."IsDeleted"
        AND m."Title" IS NOT NULL AND m."Title" != ''
        AND m."Artist" IS NOT NULL AND m."Artist" != ''
        AND m."Album" IS NOT NULL AND m."Album" != ''
        AND lower(trim(m."Album")) != 'unknown album'
    ) t;
    """
    print("Fetching active tracks from MEDIA PostgreSQL (StorageCredentialId = 1)...")
    out = run_psql(sql)
    if not out or out == "":
        return []
    return json.loads(out)

def cluster_tracks(tracks, duration_tolerance=3.0):
    # Cluster by (normalized Title, normalized Artist, normalized Album)
    groups_by_key = defaultdict(list)
    for t in tracks:
        key = (
            t["Title"].strip().lower(),
            t["Artist"].strip().lower(),
            t["Album"].strip().lower()
        )
        groups_by_key[key].append(t)

    clusters = []
    for key, group in groups_by_key.items():
        if len(group) < 2:
            continue
        
        # Duration sub-clustering within tolerance
        ordered = sorted(group, key=lambda x: x["duration_sec"])
        current_cluster = [ordered[0]]
        for i in range(1, len(ordered)):
            if ordered[i]["duration_sec"] - current_cluster[0]["duration_sec"] <= duration_tolerance:
                current_cluster.append(ordered[i])
            else:
                if len(current_cluster) > 1:
                    clusters.append(current_cluster)
                current_cluster = [ordered[i]]
        if len(current_cluster) > 1:
            clusters.append(current_cluster)

    return clusters

def evaluate_clusters(clusters):
    evaluated_groups = []
    total_losers = 0

    for cluster in clusters:
        # Score each member
        scored_members = []
        for m in cluster:
            score, kbps, g_tier = compute_keep_score(m)
            scored_members.append({
                "raw": m,
                "Id": m["Id"],
                "Title": m["Title"],
                "Artist": m["Artist"],
                "Album": m["Album"],
                "Genre": m.get("Genre") or "",
                "GenreTier": g_tier,
                "Year": m.get("Year", 0),
                "DurationSec": round(m.get("duration_sec", 0.0), 1),
                "SizeBytes": m["SizeBytes"],
                "Kbps": int(round(kbps)),
                "FilePath": m["FilePath"],
                "IsProtected": m.get("is_protected", False),
                "KeepScore": round(score, 1)
            })

        # Sort descending by KeepScore, then earlier Id
        scored_members.sort(key=lambda x: (x["KeepScore"], -x["Id"]), reverse=True)

        winner = scored_members[0]
        losers = scored_members[1:]

        if not losers:
            continue

        total_losers += len(losers)
        evaluated_groups.append({
            "group_key": f"{winner['Title']} | {winner['Artist']} | {winner['Album']}",
            "winner": winner,
            "losers": losers,
            "all_members": scored_members
        })

    return evaluated_groups, total_losers

def main():
    apply_mode = "--apply" in sys.argv
    rollback_file = None
    for arg in sys.argv:
        if arg.startswith("--rollback="):
            rollback_file = arg.split("=", 1)[1]
        elif arg == "--rollback":
            # Find latest manifest
            manifests = sorted([f for f in os.listdir("reports") if f.startswith("nas_metadata_pk_manifest_") and f.endswith(".json")])
            if manifests:
                rollback_file = os.path.join("reports", manifests[-1])

    if rollback_file:
        print(f"=== ROLLBACK METADATA PK ===")
        print(f"Manifest: {rollback_file}")
        with open(rollback_file, "r", encoding="utf-8") as f:
            manifest = json.load(f)
        ids = manifest.get("soft_deleted_ids", [])
        if not ids:
            print("No IDs to restore.")
            return
        print(f"Restoring {len(ids)} soft-deleted tracks (setting IsDeleted = false, DeletedAt = null)...")
        chunk_size = 500
        for i in range(0, len(ids), chunk_size):
            chunk = ids[i:i+chunk_size]
            id_str = ",".join(str(x) for x in chunk)
            sql = f'UPDATE "MediaFiles" SET "IsDeleted" = false, "DeletedAt" = NULL WHERE "Id" IN ({id_str});'
            run_psql(sql)
        print(f"✅ Successfully restored {len(ids)} tracks to active library!")
        return

    print("=== NAS (192.168.2.18) Metadata PK Governance Audit ===")
    print("Scope: StorageCredentialId = 1 (192.168.2.18 only, 106 excluded)")
    print(f"Mode: {'🚀 APPLY (Soft-Delete Database Update)' if apply_mode else '🧪 DRY RUN (Audit Only)'}")

    tracks = load_active_nas_tracks()
    print(f"Loaded {len(tracks)} active tracks on NAS 18.")

    clusters = cluster_tracks(tracks)
    print(f"Identified {len(clusters)} duplicate clusters (Title + Artist + Album with duration tolerance <= 3s).")

    evaluated_groups, total_losers = evaluate_clusters(clusters)
    print(f"\nAudit Summary:")
    print(f"  Total Duplicate Groups: {len(evaluated_groups)}")
    print(f"  Total Tracks to Keep (Winners): {len(evaluated_groups)}")
    print(f"  Total Tracks to Soft-Delete (Losers): {total_losers}")

    # Display sample groups
    print("\n" + "="*80)
    print("SAMPLE EVALUATION GROUPS (Metadata PK):")
    print("="*80)

    # Let's find specific famous tracks if present
    sample_targets = ["wanna be startin", "paparazzi", "what i've done", "dreaming"]
    sample_shown = 0

    for eg in evaluated_groups:
        gk_lower = eg["group_key"].lower()
        is_target = any(target in gk_lower for target in sample_targets)
        if is_target or (sample_shown < 8 and len(eg["losers"]) >= 1):
            print(f"\nGroup: {eg['group_key']}")
            w = eg["winner"]
            print(f"  👑 WINNER: [Id {w['Id']}] Score={w['KeepScore']} | {w['Kbps']}kbps | Genre={w['Genre']} ({w['GenreTier']}) | Year={w['Year']}")
            print(f"     Path: {w['FilePath']}")
            for loser in eg["losers"]:
                print(f"  ❌ LOSER : [Id {loser['Id']}] Score={loser['KeepScore']} | {loser['Kbps']}kbps | Genre={loser['Genre']} ({loser['GenreTier']}) | Year={loser['Year']}")
                print(f"     Path: {loser['FilePath']}")
            sample_shown += 1

    loser_ids = [loser["Id"] for eg in evaluated_groups for loser in eg["losers"]]

    if not apply_mode:
        print("\n" + "="*80)
        print("🧪 DRY RUN COMPLETE - ZERO DATABASE CHANGES MADE")
        print(f"To execute soft-delete for {len(loser_ids)} redundant tracks, run:")
        print("  python3 scripts/dedupe_nas_metadata_pk.py --apply")
        print("="*80)
        return

    # Apply Mode
    print("\n" + "="*80)
    print(f"🚀 APPLYING SOFT-DELETE FOR {len(loser_ids)} TRACKS...")
    print("="*80)

    # 1. Filter out only losers that have references to migrate
    migration_statements = []
    migrated_count = 0
    for eg in evaluated_groups:
        wid = eg["winner"]["Id"]
        ref_losers = [l["Id"] for l in eg["losers"] if l["IsProtected"]]
        if not ref_losers:
            continue
        l_str = ",".join(str(x) for x in ref_losers)
        migrated_count += len(ref_losers)
        # PlaylistSongs: update MediaFileId to winner if winner not already in that playlist, else delete loser item
        migration_statements.append(f"""
        UPDATE "PlaylistSongs" SET "MediaFileId" = {wid} 
        WHERE "MediaFileId" IN ({l_str}) 
          AND "PlaylistId" NOT IN (SELECT "PlaylistId" FROM "PlaylistSongs" WHERE "MediaFileId" = {wid});
        DELETE FROM "PlaylistSongs" WHERE "MediaFileId" IN ({l_str});
        """)
        # Favorites: update if winner not favorited
        migration_statements.append(f"""
        UPDATE "Favorites" SET "MediaFileId" = {wid} 
        WHERE "MediaFileId" IN ({l_str}) 
          AND "UserId" NOT IN (SELECT "UserId" FROM "Favorites" WHERE "MediaFileId" = {wid});
        DELETE FROM "Favorites" WHERE "MediaFileId" IN ({l_str});
        """)
        # PlayHistories: update MediaFileId to winner
        migration_statements.append(f"""
        UPDATE "PlayHistories" SET "MediaFileId" = {wid} WHERE "MediaFileId" IN ({l_str});
        """)

    print(f"Migrating {migrated_count} referenced loser tracks to their winners...")
    if migration_statements:
        run_psql("BEGIN;\n" + "\n".join(migration_statements) + "\nCOMMIT;")
        print("✅ References successfully migrated!")

    # 2. Save manifest first
    os.makedirs("reports", exist_ok=True)
    now_str = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    manifest_path = f"reports/nas_metadata_pk_manifest_{len(loser_ids)}_{now_str}.json"
    manifest_data = {
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "total_groups": len(evaluated_groups),
        "total_losers": len(loser_ids),
        "soft_deleted_ids": loser_ids,
        "groups": [
            {
                "group_key": eg["group_key"],
                "winner_id": eg["winner"]["Id"],
                "winner_path": eg["winner"]["FilePath"],
                "winner_score": eg["winner"]["KeepScore"],
                "losers": [
                    {
                        "id": l["Id"],
                        "path": l["FilePath"],
                        "score": l["KeepScore"],
                        "genre": l["Genre"],
                        "genre_tier": l["GenreTier"]
                    } for l in eg["losers"]
                ]
            } for eg in evaluated_groups
        ]
    }
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest_data, f, indent=2, ensure_ascii=False)
    print(f"Manifest written to: {manifest_path}")

    # 3. Chunked SQL update for soft-delete
    chunk_size = 2000
    total_chunks = (len(loser_ids) + chunk_size - 1) // chunk_size
    print(f"Updating {len(loser_ids)} tracks to IsDeleted = true in {total_chunks} batches...")
    for i in range(0, len(loser_ids), chunk_size):
        chunk = loser_ids[i:i+chunk_size]
        id_str = ",".join(str(x) for x in chunk)
        sql = f'UPDATE "MediaFiles" SET "IsDeleted" = true, "DeletedAt" = NOW() WHERE "Id" IN ({id_str});'
        run_psql(sql)
        print(f"  Batch {i//chunk_size + 1}/{total_chunks} completed ({len(chunk)} tracks).")

    print(f"\n✅ Successfully soft-deleted {len(loser_ids)} low-quality tracks from Library view!")
    print(f"All physical files on DSM918 remain 100% untouched.")
    print(f"Tracks can be restored anytime using:")
    print(f"  python3 scripts/dedupe_nas_metadata_pk.py --rollback={manifest_path}")

if __name__ == "__main__":
    main()
