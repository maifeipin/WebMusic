#!/usr/bin/env python3
"""
extract_shadow_run_candidates.py

Formal audited tool for classifying and extracting high-confidence candidates from
a zero-write Shadow Run JSON report.

Rules & Gates:
1. Report Integrity: Requires strict --expected-report-sha verification before processing.
   Top-level 'AllItems' must exist and be a list.
2. Existing Identities Snapshot: Requires a verified read-only existing-identities.json snapshot
   via mandatory --expected-identities-snapshot-sha (with verified SHA, export timestamp, and source SQL)
   to automatically exclude any existing identities (including historical MusicBrainz, MusicBrainzLocal,
   and manual identities). Snapshot internal counts must be strictly consistent.
3. Outcome & Confidence Gate: Fail-closed requirement that Outcome == "HighConfidence" AND
   confidence >= min_confidence (default 0.995).
4. Field Integrity Validation: Fail-closed validation for positive integer MediaId, valid UUID MBID,
   non-empty Title and Artist, positive finite durations, and valid confidence in [0.0, 1.0].
5. Version Check: Scans raw Title, raw Album, MatchedTitle, and Disambiguation for all
   version terms (clean, explicit, live, remix, mix, edit, version, acoustic, instrumental,
   deluxe, demo, bonus, re-recording, 演唱会, 现场, 翻唱, 纯音乐, 单曲, etc.).
   Any non-empty Disambiguation is also strictly flagged.
6. Artist Consistency: Verifies normalized raw artist strictly matches matched artist.
7. Title Consistency: Verifies normalized raw title strictly matches matched title.
8. Duration Difference: Disqualifies candidates with |raw_dur - matched_dur| > max_dur_diff (default 3.0s).
9. MBID Deduplication: Prevents multiple file entries matching the same recording from
   entering the primary pilot pool.
10. Encoding/Mojibake Check: Detects and flags garbled/mojibake metadata.
"""

import argparse
import hashlib
import json
import math
import os
import re
import sys

EN_KEYWORDS = [
    'clean', 'explicit', 'live', 'remix', 'mix', 'remastered', 'remaster',
    'deluxe', 'acoustic', 'instrumental', 'karaoke', 'demo', 'bonus', 'edit',
    're-recording', 'rerecorded', 're-recorded', 'version', 'ver', 'extended',
    'radio', 'single', 'promo', 'ost'
]

ZH_KEYWORDS = [
    '演唱会', '现场', '现场版', '音乐会', '重录', '翻唱', '混音', '伴奏', '伴唱',
    '纯音乐', '特别版', '纪念版', '重制', '重置', '原声', '单曲', '加长版', '精选'
]

EN_PATTERN = r'(?i)(?:^|[^a-z0-9])(' + '|'.join(re.escape(k) for k in EN_KEYWORDS) + r')(?:[^a-z0-9]|$)'
ZH_PATTERN = r'(' + '|'.join(re.escape(k) for k in ZH_KEYWORDS) + r')'
VERSION_REGEX = re.compile(f'{EN_PATTERN}|{ZH_PATTERN}')

MOJIBAKE_REGEX = re.compile(r'[ÄÅÆÇÉÑÖÜáàâäãåçéèêëíìîïñóòôöõúùûüýÿ¸µ¶·º»¼½¾¿ÀÁÂÃÈÊËÌÍÎÏÐÒÓÔÕØÙÚÛÝÞß]')

UUID_REGEX = re.compile(r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$', re.IGNORECASE)


def compute_file_sha256(file_path):
    """Computes SHA-256 hash of a file."""
    h = hashlib.sha256()
    with open(file_path, 'rb') as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()


def verify_report_sha(report_path, expected_sha):
    """
    Verifies that report file matches expected SHA-256 hash.
    Raises ValueError on mismatch.
    """
    if not report_path or not os.path.exists(report_path):
        raise FileNotFoundError(f"Report file not found: {report_path}")

    actual_sha = compute_file_sha256(report_path)
    if actual_sha.lower() != expected_sha.strip().lower():
        raise ValueError(
            f"Report SHA mismatch! Expected: {expected_sha.strip().lower()}, Actual: {actual_sha.lower()}"
        )
    return actual_sha.lower()


def load_existing_identities(snapshot_path, expected_sha=None):
    """
    Loads read-only existing-identities.json snapshot with integrity and count verification.
    Returns (set_of_media_file_ids, metadata_dict, snapshot_sha).
    Raises FileNotFoundError or ValueError on invalid structure, count mismatch, or SHA mismatch.
    """
    if not snapshot_path or not os.path.exists(snapshot_path):
        raise FileNotFoundError(f"Existing identities snapshot file not found: {snapshot_path}")

    snapshot_sha = compute_file_sha256(snapshot_path)
    if expected_sha is not None and snapshot_sha.lower() != expected_sha.strip().lower():
        raise ValueError(
            f"Identities snapshot SHA mismatch! Expected: {expected_sha.strip().lower()}, Actual: {snapshot_sha.lower()}"
        )

    with open(snapshot_path, 'r', encoding='utf-8') as f:
        data = json.load(f)

    if not isinstance(data, dict):
        raise ValueError("Existing identities snapshot must be a JSON object")

    identities = data.get('Identities')
    if not isinstance(identities, list):
        raise ValueError("Existing identities snapshot missing 'Identities' array")

    total_identities = data.get('TotalIdentities')
    if total_identities is not None and total_identities != len(identities):
        raise ValueError(
            f"Snapshot TotalIdentities mismatch: declared {total_identities}, actual len(Identities) is {len(identities)}"
        )

    distinct_ids = data.get('DistinctMediaFileIds')
    declared_distinct_count = data.get('DistinctMediaFileIdsCount')

    excluded_mids = set()
    for item in identities:
        mid = item.get('MediaFileId')
        if not isinstance(mid, int) or mid <= 0:
            raise ValueError(f"Invalid MediaFileId in snapshot: {mid}")
        excluded_mids.add(mid)

    if distinct_ids is not None:
        if not isinstance(distinct_ids, list):
            raise ValueError("'DistinctMediaFileIds' must be a list if provided")
        if set(distinct_ids) != excluded_mids:
            raise ValueError(
                f"DistinctMediaFileIds list does not match actual distinct MediaFileIds from Identities"
            )

    if declared_distinct_count is not None and declared_distinct_count != len(excluded_mids):
        raise ValueError(
            f"Snapshot DistinctMediaFileIdsCount mismatch: declared {declared_distinct_count}, actual distinct is {len(excluded_mids)}"
        )

    metadata = {
        'snapshotPath': snapshot_path,
        'snapshotSha256': snapshot_sha,
        'exportTimestamp': data.get('ExportTimestamp', 'UNKNOWN'),
        'sourceHost': data.get('SourceHost', 'UNKNOWN'),
        'sourceDatabase': data.get('SourceDatabase', 'UNKNOWN'),
        'sourceTable': data.get('SourceTable', 'UNKNOWN'),
        'sourceSql': data.get('SourceSql', 'UNKNOWN'),
        'totalIdentities': len(identities),
        'distinctMediaFileIdsCount': len(excluded_mids),
        'distinctMediaFileIds': sorted(list(excluded_mids))
    }

    return excluded_mids, metadata, snapshot_sha


def validate_item_fields(item):
    """
    Validates field integrity of a report item fail-closed.
    Returns (is_valid, error_reason).
    """
    mid = item.get('MediaId')
    if not isinstance(mid, int) or mid <= 0:
        return False, f"Invalid or non-positive MediaId: {mid}"

    mbid = item.get('MatchedMbid')
    if not mbid or not isinstance(mbid, str) or not UUID_REGEX.match(mbid.strip()):
        return False, f"Invalid or missing MatchedMbid: {mbid}"

    title = item.get('Title')
    if not title or not isinstance(title, str) or not title.strip():
        return False, "Missing or empty Title"

    artist = item.get('Artist')
    if not artist or not isinstance(artist, str) or not artist.strip():
        return False, "Missing or empty Artist"

    matched_title = item.get('MatchedTitle')
    if not matched_title or not isinstance(matched_title, str) or not matched_title.strip():
        return False, "Missing or empty MatchedTitle"

    matched_artist = item.get('MatchedArtist')
    if not matched_artist or not isinstance(matched_artist, str) or not matched_artist.strip():
        return False, "Missing or empty MatchedArtist"

    try:
        dur = float(item.get('DurationSeconds', 0))
        if dur <= 0 or not math.isfinite(dur):
            return False, f"Invalid DurationSeconds: {dur}"
    except (ValueError, TypeError):
        return False, "Invalid DurationSeconds"

    try:
        mdur = float(item.get('MatchedDurationSeconds', 0))
        if mdur <= 0 or not math.isfinite(mdur):
            return False, f"Invalid MatchedDurationSeconds: {mdur}"
    except (ValueError, TypeError):
        return False, "Invalid MatchedDurationSeconds"

    try:
        conf = float(item.get('Confidence', 0))
        if not (0.0 <= conf <= 1.0) or not math.isfinite(conf):
            return False, f"Invalid Confidence: {conf}"
    except (ValueError, TypeError):
        return False, "Invalid Confidence"

    return True, None


def check_version_terms(raw_title, raw_album, matched_title, disambiguation):
    """
    Checks if any version term exists across raw title, raw album, matched title, or disambiguation.
    Returns (has_version_term, matched_keyword).
    """
    combined = f'{raw_title or ""} | {raw_album or ""} | {matched_title or ""} | {disambiguation or ""}'
    match = VERSION_REGEX.search(combined)
    if match:
        word = match.group(0).strip()
        return True, word
    if disambiguation and disambiguation.strip():
        return True, f'disambig:{disambiguation.strip()}'
    return False, None


def normalize_string(s):
    """
    Normalizes string by stripping whitespace, lowercasing, and normalizing punctuation.
    """
    if not s:
        return ''
    s = s.strip().lower()
    s = s.replace('’', "'").replace('‘', "'").replace('“', '"').replace('”', '"').replace('…', '...')
    s = re.sub(r'\s+', ' ', s)
    return s


def check_artist_match(raw_artist, matched_artist):
    """
    Checks whether raw_artist matches matched_artist after case/whitespace/punctuation normalization.
    """
    if not raw_artist or not matched_artist:
        return False
    return normalize_string(raw_artist) == normalize_string(matched_artist)


def check_title_match(raw_title, matched_title):
    """
    Checks whether raw_title matches matched_title after case/whitespace/punctuation normalization.
    """
    if not raw_title or not matched_title:
        return False
    return normalize_string(raw_title) == normalize_string(matched_title)


def check_mojibake(s):
    """
    Detects potential mojibake/garbled text.
    """
    if not s:
        return False
    return bool(MOJIBAKE_REGEX.search(s))


def classify_candidates(report_data, existing_ids=None, max_dur_diff=3.0, min_confidence=0.995):
    """
    Classifies report items into conservative pristine, duplicate MBIDs, version diff,
    artist diff, title diff, duration diff, confidence diff, and existing identities.
    Requires top-level AllItems list and Outcome == 'HighConfidence'.
    """
    if existing_ids is None:
        existing_ids = set()

    if not isinstance(report_data, dict) or 'AllItems' not in report_data or not isinstance(report_data['AllItems'], list):
        raise ValueError("Report missing 'AllItems' array or 'AllItems' is not a list")

    items = report_data['AllItems']

    conservative_pristine = []
    duplicate_mbids = []
    version_flagged = []
    artist_flagged = []
    title_flagged = []
    duration_flagged = []
    confidence_flagged = []
    mojibake_flagged = []
    malformed_flagged = []
    already_identified = []

    seen_mbids = set()
    total_high_confidence = 0

    for item in items:
        outcome = item.get('Outcome')
        # Fail-closed: only evaluate items with Outcome == "HighConfidence"
        if outcome != 'HighConfidence':
            continue

        total_high_confidence += 1

        is_valid, reason = validate_item_fields(item)
        if not is_valid:
            malformed_flagged.append({
                'mediaId': item.get('MediaId'),
                'rawTitle': str(item.get('Title') or ''),
                'rawArtist': str(item.get('Artist') or ''),
                'reason': reason
            })
            continue

        mid = item['MediaId']
        raw_title = item['Title'].strip()
        raw_artist = item['Artist'].strip()
        raw_album = (item.get('Album') or '').strip()
        raw_dur = float(item['DurationSeconds'])

        matched_title = item['MatchedTitle'].strip()
        matched_artist = item['MatchedArtist'].strip()
        matched_album = (item.get('MatchedRelease') or '').strip()
        matched_dur = float(item['MatchedDurationSeconds'])
        mbid = item['MatchedMbid'].strip()
        disambig = (item.get('Disambiguation') or '').strip()
        conf = float(item['Confidence'])

        dur_diff = abs(raw_dur - matched_dur)
        has_version, matched_word = check_version_terms(raw_title, raw_album, matched_title, disambig)
        artist_matched = check_artist_match(raw_artist, matched_artist)
        title_matched = check_title_match(raw_title, matched_title)
        is_mojibake = check_mojibake(raw_title) or check_mojibake(raw_album) or check_mojibake(raw_artist)

        entry = {
            'mediaId': mid,
            'rawTitle': raw_title,
            'rawArtist': raw_artist,
            'rawAlbum': raw_album,
            'rawDuration': raw_dur,
            'matchedTitle': matched_title,
            'matchedArtist': matched_artist,
            'matchedAlbum': matched_album,
            'matchedDuration': matched_dur,
            'durDiff': dur_diff,
            'mbid': mbid,
            'disambiguation': disambig,
            'confidence': conf,
            'versionWord': matched_word,
            'hasVersion': has_version,
            'artistMatched': artist_matched,
            'titleMatched': title_matched,
            'isMojibake': is_mojibake
        }

        # 1. Any existing identity gate (including MusicBrainz, MusicBrainzLocal, manual)
        if mid in existing_ids:
            already_identified.append(entry)
            continue

        # 2. Mojibake gate
        if is_mojibake:
            mojibake_flagged.append(entry)
            continue

        # 3. Version gate (including non-empty disambiguation)
        if has_version:
            version_flagged.append(entry)
            continue

        # 4. Artist match gate
        if not artist_matched:
            artist_flagged.append(entry)
            continue

        # 5. Title match gate
        if not title_matched:
            title_flagged.append(entry)
            continue

        # 6. Duration difference gate
        if dur_diff > max_dur_diff:
            duration_flagged.append(entry)
            continue

        # 7. Confidence gate
        if conf < min_confidence:
            confidence_flagged.append(entry)
            continue

        # 8. MBID deduplication gate
        if mbid in seen_mbids:
            duplicate_mbids.append(entry)
            continue

        seen_mbids.add(mbid)
        conservative_pristine.append(entry)

    return {
        'totalEvaluated': len(items),
        'totalHighConfidence': total_high_confidence,
        'conservativePristine': conservative_pristine,
        'duplicateMbids': duplicate_mbids,
        'versionFlagged': version_flagged,
        'artistFlagged': artist_flagged,
        'titleFlagged': title_flagged,
        'durationFlagged': duration_flagged,
        'confidenceFlagged': confidence_flagged,
        'mojibakeFlagged': mojibake_flagged,
        'malformedFlagged': malformed_flagged,
        'alreadyIdentified': already_identified
    }


def generate_markdown_audit_sheet(classified, report_sha, snapshot_meta, output_file, top_n=20):
    """
    Renders structured, audited markdown report table with verified provenance.
    """
    lines = []
    lines.append('# Round 3 保守高置信候选审核清单')
    lines.append('')
    lines.append('## 审计元数据与来源存证')
    lines.append('')
    lines.append(f'- **原始报告 SHA-256**: `{report_sha}`')
    lines.append(f'- **已有身份快照文件**: `{snapshot_meta.get("snapshotPath", "N/A")}`')
    lines.append(f'- **已有身份快照 SHA-256**: `{snapshot_meta.get("snapshotSha256", "N/A")}`')
    lines.append(f'- **身份快照导出时间**: `{snapshot_meta.get("exportTimestamp", "N/A")}`')
    lines.append(f'- **身份快照来源数据库**: `{snapshot_meta.get("sourceHost", "N/A")} / {snapshot_meta.get("sourceDatabase", "N/A")}`')
    lines.append(f'- **身份快照来源 SQL**: `{snapshot_meta.get("sourceSql", "N/A")}`')
    lines.append(f'- **身份快照总记录数**: {snapshot_meta.get("totalIdentities", 0)}')
    lines.append(f'- **身份快照去重 Media ID 数**: {snapshot_meta.get("distinctMediaFileIdsCount", 0)}')
    excluded_mids_list = [r['mediaId'] for r in classified['alreadyIdentified']]
    lines.append(f'- **实际在本次高置信中排除的任何既有身份 Media ID ({len(excluded_mids_list)})**: `{excluded_mids_list}`')
    lines.append('')
    lines.append('## 分类汇总')
    lines.append('')
    lines.append(f'- 报告评估总数: {classified["totalEvaluated"]}')
    lines.append(f'- 高置信总数 (Outcome == "HighConfidence"): {classified["totalHighConfidence"]}')
    lines.append(f'- **保守纯净候选池 (无版本词、标题/艺人一致、时长差<=3s、置信度>=0.995、MBID唯一、无任何既有身份)**: {len(classified["conservativePristine"])} 首')
    lines.append(f'- **MBID 去重候选 (多文件指向同一曲目)**: {len(classified["duplicateMbids"])} 首')
    lines.append(f'- **版本词/Disambiguation 候选 (含 live/clean/remix/演唱会/单曲/Disambiguation 等)**: {len(classified["versionFlagged"])} 首')
    lines.append(f'- **艺人署名差异候选 (含繁简、feat、合作艺人)**: {len(classified["artistFlagged"])} 首')
    lines.append(f'- **标题差异候选 (含繁简、括号附加等)**: {len(classified["titleFlagged"])} 首')
    lines.append(f'- **时长差超标候选 (>3s)**: {len(classified["durationFlagged"])} 首')
    lines.append(f'- **置信度未达标候选 (<0.995)**: {len(classified["confidenceFlagged"])} 首')
    lines.append(f'- **编码乱码排除候选**: {len(classified["mojibakeFlagged"])} 首')
    lines.append(f'- **数据格式校验失败候选**: {len(classified["malformedFlagged"])} 首')
    lines.append(f'- **排除任何既有身份候选**: {len(classified["alreadyIdentified"])} 首')
    lines.append('')

    def render_table(items, title, limit=None):
        displayed = items if limit is None else items[:limit]
        suffix = f' (展示前 {len(displayed)} / 共 {len(items)})' if limit and len(items) > limit else f' ({len(items)})'
        lines.append(f'## {title}{suffix}')
        lines.append('')
        if not displayed:
            lines.append('*无条目*')
            lines.append('')
            return

        lines.append('| # | Media ID | 原始标题 | 原始艺人 | 原始专辑 | 原始时长 | 匹配标题 | 匹配艺人 | 匹配时长 | 时长差 | MBID | Disambiguation | 置信度 | 标记说明 |')
        lines.append('| ---: | ---: | --- | --- | --- | ---: | --- | --- | ---: | ---: | --- | --- | ---: | --- |')
        for idx, r in enumerate(displayed, 1):
            tag = r.get('versionWord') or ('艺人差异' if not r.get('artistMatched') else ('标题差异' if not r.get('titleMatched') else '纯净'))
            lines.append(
                f"| {idx} | {r['mediaId']} | {r['rawTitle'].replace('|', '/')} | {r['rawArtist'].replace('|', '/')} | {r['rawAlbum'].replace('|', '/')} | "
                f"{r['rawDuration']:.1f}s | {r['matchedTitle'].replace('|', '/')} | {r['matchedArtist'].replace('|', '/')} | "
                f"{r['matchedDuration']:.1f}s | {r['durDiff']:.1f}s | `{r['mbid']}` | {r['disambiguation'].replace('|', '/')} | "
                f"{r['confidence']:.4f} | {tag} |"
            )
        lines.append('')

    # 1. Highlight top 20 pristine candidates specifically
    render_table(classified['conservativePristine'][:top_n], f'1. 推荐首批审核候选 (Top {top_n} 保守纯净)')
    # 2. Full pristine pool
    render_table(classified['conservativePristine'], '2. 完整保守纯净候选池')
    # 3. Duplicate MBIDs
    render_table(classified['duplicateMbids'], '3. MBID 去重候选 (多文件指向同一曲目)')
    # 4. Version flagged
    render_table(classified['versionFlagged'], '4. 待确认版本词/Disambiguation 候选')
    # 5. Artist flagged
    render_table(classified['artistFlagged'], '5. 待确认艺人署名差异候选')
    # 6. Title flagged
    render_table(classified['titleFlagged'], '6. 待确认标题差异候选')
    # 7. Duration flagged
    render_table(classified['durationFlagged'], '7. 时长差超标候选')
    # 8. Confidence flagged
    render_table(classified['confidenceFlagged'], '8. 置信度未达保守门禁候选')
    # 9. Mojibake flagged
    render_table(classified['mojibakeFlagged'], '9. 编码乱码排除候选')
    # 10. Already identified
    render_table(classified['alreadyIdentified'], '10. 排除任何既有身份候选')

    with open(output_file, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))


def main():
    parser = argparse.ArgumentParser(description='Extract conservative candidates from shadow run report.')
    parser.add_argument('--report', required=True, help='Path to shadow run report JSON')
    parser.add_argument('--expected-report-sha', required=True, help='Expected SHA-256 of report JSON (fail-closed)')
    parser.add_argument('--existing-identities', required=True, help='Path to existing-identities.json snapshot')
    parser.add_argument('--expected-identities-snapshot-sha', required=True, help='Expected SHA-256 of existing-identities snapshot (fail-closed)')
    parser.add_argument('--out', required=True, help='Output markdown file path')
    parser.add_argument('--max-dur-diff', type=float, default=3.0, help='Max duration difference in seconds')
    parser.add_argument('--min-conf', type=float, default=0.995, help='Min confidence threshold')
    parser.add_argument('--top-n', type=int, default=20, help='Top N candidates to highlight')

    args = parser.parse_args()

    # 1. Fail-closed report SHA check
    try:
        report_sha = verify_report_sha(args.report, args.expected_report_sha)
    except Exception as e:
        print(f"FATAL: Report SHA verification failed: {e}", file=sys.stderr)
        sys.exit(1)

    # 2. Fail-closed snapshot loading and SHA check
    try:
        excluded_ids, snapshot_meta, snapshot_sha = load_existing_identities(
            args.existing_identities,
            expected_sha=args.expected_identities_snapshot_sha
        )
    except Exception as e:
        print(f"FATAL: Existing identities snapshot verification failed: {e}", file=sys.stderr)
        sys.exit(1)

    with open(args.report, 'r', encoding='utf-8') as f:
        report_data = json.load(f)

    try:
        classified = classify_candidates(
            report_data,
            existing_ids=excluded_ids,
            max_dur_diff=args.max_dur_diff,
            min_confidence=args.min_conf
        )
    except Exception as e:
        print(f"FATAL: Candidate classification failed: {e}", file=sys.stderr)
        sys.exit(1)

    generate_markdown_audit_sheet(classified, report_sha, snapshot_meta, args.out, top_n=args.top_n)

    excluded_mids = [r['mediaId'] for r in classified['alreadyIdentified']]
    print("Done.")
    print(f"Report SHA-256: {report_sha}")
    print(f"Identities Snapshot SHA-256: {snapshot_sha} (Excluded MIDs in report: {len(excluded_mids)} -> {excluded_mids})")
    print(f"Conservative Pristine Candidates: {len(classified['conservativePristine'])}")
    print(f"Report written to: {args.out}")


if __name__ == '__main__':
    main()
