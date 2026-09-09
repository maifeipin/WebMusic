#!/usr/bin/env python3
"""
Unit tests for extract_shadow_run_candidates.py
"""

import hashlib
import json
import os
import sys
import tempfile
import unittest

# Add scripts directory to path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from extract_shadow_run_candidates import (
    check_version_terms,
    check_artist_match,
    check_title_match,
    check_mojibake,
    classify_candidates,
    load_existing_identities,
    validate_item_fields,
    verify_report_sha,
    normalize_string
)


class TestCandidateExtraction(unittest.TestCase):

    def test_version_terms_english(self):
        self.assertTrue(check_version_terms("Song (Clean)", "Album", "Song", "")[0])
        self.assertTrue(check_version_terms("Song", "Album", "Song (Explicit)", "")[0])
        self.assertTrue(check_version_terms("Song (Live)", "Album", "Song", "")[0])
        self.assertTrue(check_version_terms("Song", "Album [Remix]", "Song", "")[0])
        self.assertTrue(check_version_terms("Song", "Album", "Song (Radio Edit)", "")[0])
        self.assertTrue(check_version_terms("Song (Acoustic)", "Album", "Song", "")[0])
        self.assertTrue(check_version_terms("Song", "Album (Deluxe Version)", "Song", "")[0])
        self.assertTrue(check_version_terms("Song", "Album", "Song", "clean")[0])
        self.assertTrue(check_version_terms("Song", "Album", "Song", "album version")[0])
        self.assertFalse(check_version_terms("Ordinary Song", "Ordinary Album", "Ordinary Song", "")[0])

    def test_version_terms_chinese(self):
        self.assertTrue(check_version_terms("歌曲 (现场版)", "专辑", "歌曲", "")[0])
        self.assertTrue(check_version_terms("歌曲", "2010演唱会", "歌曲", "")[0])
        self.assertTrue(check_version_terms("歌曲 (伴奏)", "专辑", "歌曲", "")[0])
        self.assertTrue(check_version_terms("歌曲", "专辑 (特别版)", "歌曲", "")[0])
        self.assertTrue(check_version_terms("歌曲", "单曲专辑", "歌曲", "")[0])
        self.assertFalse(check_version_terms("普通歌曲", "普通专辑", "普通歌曲", "")[0])

    def test_user_reported_evidence_items(self):
        # ID 82296: Sugar with disambiguation=clean
        has_v, word = check_version_terms("Sugar", "V (Deluxe Version)", "Sugar", "clean")
        self.assertTrue(has_v)

        # ID 67295 & 67297: LIVE 演唱会
        has_v, word = check_version_terms("出离", "此时此刻 演唱会 LIVE记录辑", "出离", "")
        self.assertTrue(has_v)

        # ID 133135: Title Clean, matched explicit
        has_v, word = check_version_terms("Here's To Never Growing Up (Clean)", "NOW 47", "Here’s to Never Growing Up", "explicit")
        self.assertTrue(has_v)

        # ID 133504: Remix album, original version title
        has_v, word = check_version_terms("Cheerleader", "Cheerleader (Felix Jaehn Remix Radio Edit)", "Cheerleader (original version)", "")
        self.assertTrue(has_v)

    def test_artist_matching(self):
        self.assertTrue(check_artist_match("Coldplay", "coldplay"))
        self.assertTrue(check_artist_match("  Taylor Swift  ", "Taylor Swift"))
        self.assertTrue(check_artist_match("Charli xcx", "Charli XCX"))

        self.assertFalse(check_artist_match("卢冠廷, 莫文蔚", "盧冠廷"))
        self.assertFalse(check_artist_match("Joel Hanson", "Joel Hanson, Sara Groves"))
        self.assertFalse(check_artist_match("Beyond", "Beyond / 黄家驹"))

    def test_title_matching(self):
        self.assertTrue(check_title_match("Let Her Go", "let her go"))
        self.assertTrue(check_title_match("What Do You Mean?", "What Do You Mean?"))
        self.assertTrue(check_title_match("Can't Stop", "Can’t Stop"))

        self.assertFalse(check_title_match("Bleeding Love (Album Version)", "Bleeding Love"))
        self.assertFalse(check_title_match("遥望", "遙望"))

    def test_mojibake_detection(self):
        self.assertTrue(check_mojibake("2010Äê9ÔÂÅ·ÃÀÐÂ¸èËÙµÝ3"))
        self.assertFalse(check_mojibake("All The Little Lights"))
        self.assertFalse(check_mojibake("那一年"))

    def test_classification_gates(self):
        report = {
            "AllItems": [
                {
                    "MediaId": 1001,
                    "Title": "Sunny Day",
                    "Artist": "Good Artist",
                    "Album": "Standard Album",
                    "DurationSeconds": 200.0,
                    "MatchedTitle": "Sunny Day",
                    "MatchedArtist": "Good Artist",
                    "MatchedDurationSeconds": 200.5,
                    "MatchedMbid": "11111111-2222-3333-4444-555555555555",
                    "Disambiguation": "",
                    "Confidence": 1.0,
                    "Outcome": "HighConfidence"
                },
                {
                    # Duplicate MBID
                    "MediaId": 1002,
                    "Title": "Sunny Day",
                    "Artist": "Good Artist",
                    "Album": "Standard Album",
                    "DurationSeconds": 200.0,
                    "MatchedTitle": "Sunny Day",
                    "MatchedArtist": "Good Artist",
                    "MatchedDurationSeconds": 200.5,
                    "MatchedMbid": "11111111-2222-3333-4444-555555555555",
                    "Disambiguation": "",
                    "Confidence": 1.0,
                    "Outcome": "HighConfidence"
                },
                {
                    # Duration diff > 3s
                    "MediaId": 1003,
                    "Title": "Long Song",
                    "Artist": "Good Artist",
                    "Album": "Standard Album",
                    "DurationSeconds": 200.0,
                    "MatchedTitle": "Long Song",
                    "MatchedArtist": "Good Artist",
                    "MatchedDurationSeconds": 205.0,
                    "MatchedMbid": "22222222-2222-3333-4444-555555555555",
                    "Disambiguation": "",
                    "Confidence": 1.0,
                    "Outcome": "HighConfidence"
                },
                {
                    # Version in album
                    "MediaId": 1004,
                    "Title": "Live Song",
                    "Artist": "Good Artist",
                    "Album": "Live In Tokyo",
                    "DurationSeconds": 200.0,
                    "MatchedTitle": "Live Song",
                    "MatchedArtist": "Good Artist",
                    "MatchedDurationSeconds": 200.0,
                    "MatchedMbid": "33333333-2222-3333-4444-555555555555",
                    "Disambiguation": "",
                    "Confidence": 1.0,
                    "Outcome": "HighConfidence"
                },
                {
                    # Artist difference
                    "MediaId": 1005,
                    "Title": "Duet Song",
                    "Artist": "Artist A",
                    "Album": "Duets",
                    "DurationSeconds": 200.0,
                    "MatchedTitle": "Duet Song",
                    "MatchedArtist": "Artist A feat. Artist B",
                    "MatchedDurationSeconds": 200.0,
                    "MatchedMbid": "44444444-2222-3333-4444-555555555555",
                    "Disambiguation": "",
                    "Confidence": 1.0,
                    "Outcome": "HighConfidence"
                },
                {
                    # Existing ID
                    "MediaId": 1006,
                    "Title": "Existing Song",
                    "Artist": "Good Artist",
                    "Album": "Standard Album",
                    "DurationSeconds": 200.0,
                    "MatchedTitle": "Existing Song",
                    "MatchedArtist": "Good Artist",
                    "MatchedDurationSeconds": 200.0,
                    "MatchedMbid": "55555555-2222-3333-4444-555555555555",
                    "Disambiguation": "",
                    "Confidence": 1.0,
                    "Outcome": "HighConfidence"
                }
            ]
        }

        res = classify_candidates(report, existing_ids={1006}, max_dur_diff=3.0, min_confidence=0.995)

        self.assertEqual(len(res['conservativePristine']), 1)
        self.assertEqual(res['conservativePristine'][0]['mediaId'], 1001)

        self.assertEqual(len(res['duplicateMbids']), 1)
        self.assertEqual(res['duplicateMbids'][0]['mediaId'], 1002)

        self.assertEqual(len(res['durationFlagged']), 1)
        self.assertEqual(res['durationFlagged'][0]['mediaId'], 1003)

        self.assertEqual(len(res['versionFlagged']), 1)
        self.assertEqual(res['versionFlagged'][0]['mediaId'], 1004)

        self.assertEqual(len(res['artistFlagged']), 1)
        self.assertEqual(res['artistFlagged'][0]['mediaId'], 1005)

        self.assertEqual(len(res['alreadyIdentified']), 1)
        self.assertEqual(res['alreadyIdentified'][0]['mediaId'], 1006)

    # -------------------------------------------------------------
    # Negative Tests Required by Audit Gate
    # -------------------------------------------------------------

    def test_negative_missing_or_invalid_mbid(self):
        """Negative test: Item with missing or non-UUID MBID must fail validation and be excluded from pristine."""
        item_no_mbid = {
            "MediaId": 2001,
            "Title": "Song",
            "Artist": "Artist",
            "DurationSeconds": 180.0,
            "MatchedTitle": "Song",
            "MatchedArtist": "Artist",
            "MatchedDurationSeconds": 180.0,
            "Confidence": 1.0,
            "Outcome": "HighConfidence"
        }
        valid, reason = validate_item_fields(item_no_mbid)
        self.assertFalse(valid)
        self.assertIn("MatchedMbid", reason)

        item_bad_mbid = dict(item_no_mbid)
        item_bad_mbid["MatchedMbid"] = "not-a-valid-uuid"
        valid, reason = validate_item_fields(item_bad_mbid)
        self.assertFalse(valid)
        self.assertIn("MatchedMbid", reason)

        report = {"AllItems": [item_bad_mbid]}
        res = classify_candidates(report, min_confidence=0.995)
        self.assertEqual(len(res['conservativePristine']), 0)
        self.assertEqual(len(res['malformedFlagged']), 1)
        self.assertEqual(res['malformedFlagged'][0]['mediaId'], 2001)

    def test_negative_non_high_confidence_outcome(self):
        """Negative test: Outcome != 'HighConfidence' must be rejected fail-closed even with 0.999 confidence."""
        item_ambiguous = {
            "MediaId": 3001,
            "Title": "Song",
            "Artist": "Artist",
            "DurationSeconds": 180.0,
            "MatchedTitle": "Song",
            "MatchedArtist": "Artist",
            "MatchedDurationSeconds": 180.0,
            "MatchedMbid": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "Confidence": 0.999,
            "Outcome": "Ambiguous"
        }
        report = {"AllItems": [item_ambiguous]}
        res = classify_candidates(report, min_confidence=0.995)
        self.assertEqual(len(res['conservativePristine']), 0)
        self.assertEqual(res['totalHighConfidence'], 0)

    def test_negative_missing_or_invalid_existing_identities_snapshot(self):
        """Negative test: Missing or unparseable snapshot must raise FileNotFoundError or ValueError."""
        with self.assertRaises(FileNotFoundError):
            load_existing_identities("/non/existent/path/identities.json")

        with tempfile.NamedTemporaryFile(mode='w', suffix='.json', delete=False) as tf:
            tf.write(json.dumps({"InvalidKey": []}))
            tf_path = tf.name

        try:
            with self.assertRaises(ValueError):
                load_existing_identities(tf_path)
        finally:
            if os.path.exists(tf_path):
                os.unlink(tf_path)

    def test_negative_report_sha_mismatch(self):
        """Negative test: Report SHA verification must fail when SHA does not match expected hash."""
        with tempfile.NamedTemporaryFile(mode='w', suffix='.json', delete=False) as tf:
            tf.write('{"sample": "data"}')
            tf_path = tf.name

        try:
            expected_wrong_sha = "0000000000000000000000000000000000000000000000000000000000000000"
            with self.assertRaises(ValueError) as ctx:
                verify_report_sha(tf_path, expected_wrong_sha)
            self.assertIn("Report SHA mismatch", str(ctx.exception))
        finally:
            if os.path.exists(tf_path):
                os.unlink(tf_path)

    def test_negative_snapshot_sha_mismatch(self):
        """Negative test: Identities snapshot verification must fail when SHA does not match expected hash."""
        snapshot_data = {
            "TotalIdentities": 1,
            "DistinctMediaFileIdsCount": 1,
            "DistinctMediaFileIds": [100],
            "Identities": [{"Id": 1, "MediaFileId": 100}]
        }
        with tempfile.NamedTemporaryFile(mode='w', suffix='.json', delete=False) as tf:
            tf.write(json.dumps(snapshot_data))
            tf_path = tf.name

        try:
            expected_wrong_sha = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
            with self.assertRaises(ValueError) as ctx:
                load_existing_identities(tf_path, expected_sha=expected_wrong_sha)
            self.assertIn("Identities snapshot SHA mismatch", str(ctx.exception))
        finally:
            if os.path.exists(tf_path):
                os.unlink(tf_path)

    def test_negative_snapshot_count_inconsistent(self):
        """Negative test: Identities snapshot must fail when internal counts don't match actual items."""
        # 1. TotalIdentities declared 5, but Identities has only 1
        bad_count_data = {
            "TotalIdentities": 5,
            "DistinctMediaFileIdsCount": 1,
            "DistinctMediaFileIds": [100],
            "Identities": [{"Id": 1, "MediaFileId": 100}]
        }
        with tempfile.NamedTemporaryFile(mode='w', suffix='.json', delete=False) as tf:
            tf.write(json.dumps(bad_count_data))
            tf_path = tf.name

        try:
            with self.assertRaises(ValueError) as ctx:
                load_existing_identities(tf_path)
            self.assertIn("TotalIdentities mismatch", str(ctx.exception))
        finally:
            if os.path.exists(tf_path):
                os.unlink(tf_path)

        # 2. Distinct count declared 10, but actual is 1
        bad_distinct_data = {
            "TotalIdentities": 1,
            "DistinctMediaFileIdsCount": 10,
            "DistinctMediaFileIds": [100],
            "Identities": [{"Id": 1, "MediaFileId": 100}]
        }
        with tempfile.NamedTemporaryFile(mode='w', suffix='.json', delete=False) as tf:
            tf.write(json.dumps(bad_distinct_data))
            tf_path2 = tf.name

        try:
            with self.assertRaises(ValueError) as ctx:
                load_existing_identities(tf_path2)
            self.assertIn("DistinctMediaFileIdsCount mismatch", str(ctx.exception))
        finally:
            if os.path.exists(tf_path2):
                os.unlink(tf_path2)

    def test_negative_missing_or_non_array_all_items(self):
        """Negative test: Report missing 'AllItems' or where 'AllItems' is not a list must fail."""
        # Missing AllItems
        with self.assertRaises(ValueError) as ctx:
            classify_candidates({"WrongKey": []})
        self.assertIn("AllItems", str(ctx.exception))

        # AllItems is not a list
        with self.assertRaises(ValueError) as ctx:
            classify_candidates({"AllItems": "not-a-list"})
        self.assertIn("AllItems", str(ctx.exception))


if __name__ == '__main__':
    unittest.main()
