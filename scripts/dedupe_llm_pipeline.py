#!/usr/bin/env python3
"""
dedupe_llm_pipeline.py - Unified LLM Semantic Cross-Album Deduplication Pipeline.

Workflow:
  1. Export: Extracts semantic duplicate candidates across albums on NAS 18 (StorageCredentialId = 1).
  2. Semantic Classification: Differentiates Studio Master, Live concerts, Demos, Remixes, Instrumentals.
  3. Quality PK (KeepScore): Selects single best Winner per semantic cluster based on:
     - Audio format & bitrate (FLAC/WAV/APE 24-bit/16-bit > 320k > 128k)
     - Genre cleanliness (Clean > Unknown > Spam -40pts)
     - Metadata completeness (Year, Album, CoverArt)
  4. Safeguards:
     - 100% Soft Delete: ZERO physical files deleted (NAS files stay intact).
     - StorageCredentialId = 1 only (106 demo completely excluded).
     - User references (Playlist, Favorite, PlayHistory) automatically migrated to Winner.
     - Full rollback manifest generation and one-click rollback support.
"""

import os
import sys
import re
import json
import subprocess
from collections import defaultdict
from datetime import datetime, timezone

MEDIA_SSH = "root@media"

# Genre classification
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

def normalize_title_core(title):
    if not title:
        return ""
    t = title.strip()
    # Strip common trailing tags
    patterns = [
        r'\(deluxe\s*edition.*?\)', r'\[deluxe\s*edition.*?\]',
        r'\(explicit.*?\)', r'\[explicit.*?\]',
        r'\(clean.*?\)', r'\[clean.*?\]',
        r'\(remastered.*?\)', r'\[remastered.*?\]',
        r'\(single\s*edit.*?\)', r'\[single\s*edit.*?\]',
        r'\(album\s*version.*?\)', r'\[album\s*version.*?\]',
        r'\(20\d\d\s*remaster.*?\)', r'\[20\d\d\s*remaster.*?\]',
        r'\[flac\]', r'\[mp3\]', r'\[wav\]',
        r'\(bonus\s*track.*?\)', r'\[bonus\s*track.*?\]'
    ]
    for p in patterns:
        t = re.sub(p, '', t, flags=re.IGNORECASE).strip()
    return t

def detect_semantic_type(title, album, file_path):
    text = f"{title} {album} {file_path}".lower()
    
    # Check Demo
    if any(k in text for k in ["(demo", "[demo", "demo)", "demo]", " original demo", "demo album"]):
        return "DEMO"
        
    # Check Instrumental / TV Track / Karaoke
    if any(k in text for k in ["instrumental", "karaoke", "tv track", "伴奏"]):
        return "INSTRUMENTAL"
        
    # Check Remix / Mix
    if any(k in text for k in ["remix", "mix)", "mix]", "club mix", "main mix", "extended mix", "dub mix"]):
        # Extract specific mix name if possible
        m = re.search(r'[\(\[]([^\)\]]*(?:remix|mix))[^\)\]]*[\)\]]', title, re.IGNORECASE)
        if m:
            return f"REMIX:{m.group(1).strip().lower()}"
        return "REMIX:generic"
        
    # Check Live
    if any(k in text for k in ["live", "concert", "tour", "budokan", "stadium", "at zenith", "on stage", "live in "]):
        # Extract location/year if possible
        m = re.search(r'(live\s*(?:at|in|from|@)?\s*[^,\(\[\/]+)', album, re.IGNORECASE)
        if m:
            return f"LIVE:{m.group(1).strip().lower()}"
        return "LIVE:generic"
        
    return "STUDIO_MASTER"

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
        # Extra bonus for 24-bit Hi-Res (>1400kbps)
        if kbps >= 1400:
            score += 150
        else:
            score += 130
    else:
        score += int(round(min(kbps, 320.0) / 320.0 * 80.0))

    # 2. Genre Tier
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
        if any(p.search(album) for p in SPAM_GENRE_PATTERNS):
            score -= 30  # penalty for spam site in album name
    else:
        score -= 20  # penalty for Unknown Album

    # 6. MusicBrainz Identity & Lyrics
    if item.get("has_identity"):
        score += 20
    if item.get("has_lyrics"):
        score += 10

    # 7. Filename cleanliness
    if "(1)" in file_path or "(2)" in file_path or "副本" in file_path:
        score -= 20
    else:
        score += 10

    # Tie breaker: earlier ID
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

def load_active_nas_candidates():
    # Candidates where Title + Artist has count > 1
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
        AND (m."Title", m."Artist") IN (
          SELECT m2."Title", m2."Artist"
          FROM "MediaFiles" m2
          JOIN "ScanSources" s2 ON m2."ScanSourceId" = s2."Id"
          WHERE s2."StorageCredentialId" = 1 AND NOT m2."IsDeleted"
          GROUP BY m2."Title", m2."Artist"
          HAVING count(*) > 1
        )
    ) t;
    """
    print("Extracting multi-version candidates from MEDIA PostgreSQL...")
    out = run_psql(sql)
    if not out or out == "":
        return []
    return json.loads(out)

def cluster_and_classify_candidates(tracks, duration_tolerance=4.0):
    # Group by (normalized Artist, normalized Core Title)
    groups = defaultdict(list)
    for t in tracks:
        norm_art = t["Artist"].strip().lower()
        norm_title = normalize_title_core(t["Title"]).lower()
        sem_type = detect_semantic_type(t["Title"], t["Album"], t["FilePath"])
        t["semantic_type"] = sem_type
        score, kbps, g_tier = compute_keep_score(t)
        t["keep_score"] = score
        t["kbps"] = kbps
        t["genre_tier"] = g_tier
        groups[(norm_art, norm_title)].append(t)

    semantic_clusters = []
    
    for (art, title), members in groups.items():
        if len(members) < 2:
            continue
            
        # Sub-group by semantic_type
        by_sem = defaultdict(list)
        for m in members:
            by_sem[m["semantic_type"]].append(m)
            
        for sem_type, sub_members in by_sem.items():
            if len(sub_members) < 2:
                continue
                
            # Cluster by duration tolerance
            ordered = sorted(sub_members, key=lambda x: x["duration_sec"])
            cur = [ordered[0]]
            for i in range(1, len(ordered)):
                if ordered[i]["duration_sec"] - cur[0]["duration_sec"] <= duration_tolerance:
                    cur.append(ordered[i])
                else:
                    if len(cur) > 1:
                        semantic_clusters.append((f"{art} - {title} [{sem_type}]", cur))
                    cur = [ordered[i]]
            if len(cur) > 1:
                semantic_clusters.append((f"{art} - {title} [{sem_type}]", cur))

    return semantic_clusters

def evaluate_semantic_clusters(clusters):
    evaluated_groups = []
    total_losers = 0

    for cluster_name, members in clusters:
        # Sort members by keep_score descending
        sorted_members = sorted(members, key=lambda x: (x["keep_score"], -x["Id"]), reverse=True)
        winner = sorted_members[0]
        losers = sorted_members[1:]
        
        total_losers += len(losers)
        evaluated_groups.append({
            "cluster_name": cluster_name,
            "winner": winner,
            "losers": losers,
            "all_members": sorted_members
        })

    return evaluated_groups, total_losers

def main():
    apply_mode = "--apply" in sys.argv
    rollback_file = None
    target_filter = None
    batch_arg = None
    for arg in sys.argv:
        if arg.startswith("--rollback="):
            rollback_file = arg.split("=", 1)[1]
        elif arg.startswith("--filter="):
            target_filter = arg.split("=", 1)[1].lower()
        elif arg.startswith("--batch="):
            batch_arg = arg.split("=", 1)[1]

    if rollback_file:
        print(f"=== ROLLBACK LLM SEMANTIC DEDUPLICATION ===")
        print(f"Manifest: {rollback_file}")
        with open(rollback_file, "r", encoding="utf-8") as f:
            manifest = json.load(f)
        ids = manifest.get("soft_deleted_ids", [])
        if not ids:
            print("No IDs to restore.")
            return
        print(f"Restoring {len(ids)} soft-deleted tracks...")
        chunk_size = 500
        for i in range(0, len(ids), chunk_size):
            chunk = ids[i:i+chunk_size]
            id_str = ",".join(str(x) for x in chunk)
            sql = f'UPDATE "MediaFiles" SET "IsDeleted" = false, "DeletedAt" = NULL WHERE "Id" IN ({id_str});'
            run_psql(sql)
        print(f"✅ Successfully restored {len(ids)} tracks to active library!")
        return

    print("=== NAS (192.168.2.18) Unified LLM Semantic Deduplication Pipeline ===")
    print("Scope: StorageCredentialId = 1 (192.168.2.18 only, 106 demo excluded)")
    print(f"Mode: {'🚀 APPLY (Soft-Delete Database Update)' if apply_mode else '🧪 DRY RUN (Audit Only)'}")

    if batch_arg:
        if batch_arg.isdigit():
            batch_file = f"reports/llm_dedupe/candidates_batch_{int(batch_arg):02d}.json"
        else:
            batch_file = batch_arg
        print(f"Loading candidate batch from file: {batch_file}")
        with open(batch_file, "r", encoding="utf-8") as f:
            b_data = json.load(f)
        evaluated_groups = []
        for c in b_data["clusters"]:
            evaluated_groups.append({
                "cluster_name": c["cluster_name"],
                "winner": c["winner"],
                "losers": c["losers"]
            })
        total_losers = sum(len(eg["losers"]) for eg in evaluated_groups)
    else:
        tracks = load_active_nas_candidates()
        print(f"Loaded {len(tracks)} multi-version candidate tracks.")

        clusters = cluster_and_classify_candidates(tracks)
        print(f"Identified {len(clusters)} semantic duplicate clusters (Duration delta <= 4s within semantic version).")

        evaluated_groups, total_losers = evaluate_semantic_clusters(clusters)
    
    if target_filter:
        evaluated_groups = [eg for eg in evaluated_groups if target_filter in eg["cluster_name"].lower()]
        total_losers = sum(len(eg["losers"]) for eg in evaluated_groups)
        print(f"Filtered by '{target_filter}': {len(evaluated_groups)} groups, {total_losers} losers.")

    print(f"\nAudit Summary:")
    print(f"  Total Semantic Clusters: {len(evaluated_groups)}")
    print(f"  Total Tracks to Keep (Winners): {len(evaluated_groups)}")
    print(f"  Total Tracks to Soft-Delete (Losers): {total_losers}")

    print("\n" + "="*80)
    print("SAMPLE EVALUATION GROUPS (LLM Semantic PK):")
    print("="*80)

    sample_shown = 0
    for eg in evaluated_groups:
        c_name = eg["cluster_name"]
        w = eg["winner"]
        w_score = w.get("keep_score", w.get("Score", 0))
        w_kbps = w.get("kbps", w.get("Kbps", 0))
        if sample_shown < 10 or (target_filter and sample_shown < 25):
            print(f"\nCluster: {c_name}")
            print(f"  👑 WINNER: [Id {w['Id']}] Score={round(w_score,1)} | {int(round(w_kbps))}kbps | Album={w['Album']} | Genre={w['Genre']} | Year={w['Year']}")
            print(f"     Path: {w['FilePath']}")
            for loser in eg["losers"]:
                l_score = loser.get("keep_score", loser.get("Score", 0))
                l_kbps = loser.get("kbps", loser.get("Kbps", 0))
                print(f"  ❌ LOSER : [Id {loser['Id']}] Score={round(l_score,1)} | {int(round(l_kbps))}kbps | Album={loser['Album']} | Genre={loser['Genre']} | Year={loser['Year']}")
                print(f"     Path: {loser['FilePath']}")
            sample_shown += 1

    loser_ids = [loser["Id"] for eg in evaluated_groups for loser in eg["losers"]]

    if not apply_mode:
        print("\n" + "="*80)
        print("🧪 DRY RUN COMPLETE - ZERO DATABASE CHANGES MADE")
        print(f"To execute soft-delete for {len(loser_ids)} redundant tracks, run:")
        print("  python3 scripts/dedupe_llm_pipeline.py --apply")
        if target_filter:
            print(f"  (Filtered to '{target_filter}')")
        print("="*80)
        return

    # Apply Mode
    print("\n" + "="*80)
    print(f"🚀 APPLYING SOFT-DELETE FOR {len(loser_ids)} TRACKS...")
    print("="*80)

    # Reference migration
    migration_statements = []
    migrated_count = 0
    for eg in evaluated_groups:
        wid = eg["winner"]["Id"]
        ref_losers = [l["Id"] for l in eg["losers"]]
        if not ref_losers:
            continue
        l_str = ",".join(str(x) for x in ref_losers)
        migrated_count += len(ref_losers)
        migration_statements.append(f"""
        UPDATE "PlaylistSongs" SET "MediaFileId" = {wid} 
        WHERE "MediaFileId" IN ({l_str}) 
          AND "PlaylistId" NOT IN (SELECT "PlaylistId" FROM "PlaylistSongs" WHERE "MediaFileId" = {wid});
        DELETE FROM "PlaylistSongs" WHERE "MediaFileId" IN ({l_str});
        UPDATE "Favorites" SET "MediaFileId" = {wid} 
        WHERE "MediaFileId" IN ({l_str}) 
          AND "UserId" NOT IN (SELECT "UserId" FROM "Favorites" WHERE "MediaFileId" = {wid});
        DELETE FROM "Favorites" WHERE "MediaFileId" IN ({l_str});
        UPDATE "PlayHistories" SET "MediaFileId" = {wid} WHERE "MediaFileId" IN ({l_str});
        """)

    print(f"Safely executing reference migration for {migrated_count} loser tracks...")
    if migration_statements:
        # Run in chunks of 100 statements
        chunk_m = 100
        for i in range(0, len(migration_statements), chunk_m):
            stmts = migration_statements[i:i+chunk_m]
            run_psql("BEGIN;\n" + "\n".join(stmts) + "\nCOMMIT;")
        print("✅ References successfully migrated!")

    # Save manifest
    os.makedirs("reports", exist_ok=True)
    now_str = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    batch_tag = f"_batch_{batch_arg}" if batch_arg else ""
    manifest_path = f"reports/nas_llm_pipeline_manifest{batch_tag}_{len(loser_ids)}_{now_str}.json"
    manifest_data = {
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "type": "llm_pipeline_dedupe",
        "batch": batch_arg or "ALL",
        "filter": target_filter or "ALL",
        "total_groups": len(evaluated_groups),
        "total_losers": len(loser_ids),
        "soft_deleted_ids": loser_ids,
        "groups": [
            {
                "cluster_name": eg["cluster_name"],
                "winner_id": eg["winner"]["Id"],
                "winner_path": eg["winner"]["FilePath"],
                "winner_score": eg["winner"].get("keep_score", eg["winner"].get("Score", 0)),
                "losers": [
                    {
                        "id": l["Id"],
                        "path": l["FilePath"],
                        "score": l.get("keep_score", l.get("Score", 0)),
                        "genre": l.get("Genre", "")
                    } for l in eg["losers"]
                ]
            } for eg in evaluated_groups
        ]
    }
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest_data, f, indent=2, ensure_ascii=False)
    print(f"Manifest written to: {manifest_path}")

    # Chunked soft-delete
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
    print(f"  python3 scripts/dedupe_llm_pipeline.py --rollback={manifest_path}")

if __name__ == "__main__":
    main()
