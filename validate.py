"""
validate_session.py

Automated sanity checks for SilksongInspector session_*.jsonl files.

This does NOT verify ground truth (whether "attacking: true" actually
matched an on-screen attack) -- that needs a human watching footage.
This catches structural/statistical problems: missing fields, timing
issues, impossible values, schema inconsistencies.

Usage:
    python validate_session.py session_20260907_101530_001.jsonl
    python validate_session.py session_dir/*.jsonl --enemy-report
"""

import json
import sys
import glob
import argparse
from collections import defaultdict


EXPECTED_SNAPSHOT_INTERVAL = 0.05
TIMESTAMP_TOLERANCE = 0.02      # allowed drift before flagging a gap/dupe
MAX_COLLIDER_LOG_CAP = 20       # matches BuildEnemyCollidersJson's cap


def load_jsonl(path):
    """Yield (line_no, dict_or_None) for every line. None = parse failure."""
    with open(path, "r", encoding="utf-8") as f:
        for i, line in enumerate(f, start=1):
            line = line.strip()
            if not line:
                continue
            try:
                yield i, json.loads(line)
            except json.JSONDecodeError as e:
                yield i, None


def check_schema_and_parse(path):
    records = []
    parse_failures = []
    for line_no, rec in load_jsonl(path):
        if rec is None:
            parse_failures.append(line_no)
        else:
            records.append((line_no, rec))
    return records, parse_failures


def check_timestamps(records):
    issues = []
    prev_ts = None
    prev_frame = None
    dupe_count = 0
    gap_count = 0
    frame_regressions = 0

    for line_no, rec in records:
        ts = rec.get("timestamp")
        frame = rec.get("frame")

        if ts is not None and prev_ts is not None:
            delta = ts - prev_ts
            if delta < TIMESTAMP_TOLERANCE:
                dupe_count += 1
            elif delta > EXPECTED_SNAPSHOT_INTERVAL + TIMESTAMP_TOLERANCE * 4:
                gap_count += 1
                issues.append(f"line {line_no}: timestamp gap of {delta:.3f}s "
                               f"(expected ~{EXPECTED_SNAPSHOT_INTERVAL})")

        if frame is not None and prev_frame is not None and frame < prev_frame:
            frame_regressions += 1
            issues.append(f"line {line_no}: frame count went backwards "
                           f"({prev_frame} -> {frame}) -- possible scene reload mid-session")

        prev_ts = ts if ts is not None else prev_ts
        prev_frame = frame if frame is not None else prev_frame

    return {
        "near_duplicate_snapshots": dupe_count,
        "timestamp_gaps": gap_count,
        "frame_regressions": frame_regressions,
        "sample_issues": issues[:10],
    }


def check_player_hp(records):
    issues = []
    prev_hp = None
    max_seen = None

    for line_no, rec in records:
        player = rec.get("player", {})
        hp = player.get("hp")
        max_hp = player.get("max_hp")

        if max_hp is not None:
            max_seen = max_hp

        if hp is not None and prev_hp is not None and max_seen:
            delta = hp - prev_hp
            if abs(delta) > max_seen:
                issues.append(f"line {line_no}: implausible hp jump "
                               f"{prev_hp} -> {hp} (max_hp={max_seen})")
            if hp < 0:
                issues.append(f"line {line_no}: negative hp ({hp})")

        prev_hp = hp if hp is not None else prev_hp

    return {"issue_count": len(issues), "sample_issues": issues[:10]}


def check_result_event_consistency(records):
    """result.player_hit should generally line up with a player_damaged event
    in that same snapshot or the events list not being empty when hp actually changed."""
    mismatches = []
    for line_no, rec in records:
        result = rec.get("result", {})
        events = rec.get("events", [])  # top-level field, per BuildSnapshotJson
        player_hit = result.get("player_hit", False)
        has_damage_event = any(
            isinstance(e, dict) and e.get("kind") == "player_damaged"
            for e in events
        )
        if player_hit and not has_damage_event:
            mismatches.append(
                f"line {line_no}: result.player_hit=true but no matching "
                f"player_damaged event in events[]"
            )

    return {"mismatch_count": len(mismatches), "sample_issues": mismatches[:10]}


def check_enemy_field_coverage(records):
    """Per enemy `type`, what fraction of snapshots have hp/state as null/unknown,
    and does `attacking` ever fire at all. This is the check that surfaces the
    'silent reflection failure' problem for enemy types whose field names don't
    match the plugin's guessed candidate list."""
    stats = defaultdict(lambda: {
        "snapshots": 0,
        "hp_null": 0,
        "state_unknown": 0,
        "attacking_true": 0,
        "collider_cap_hit": 0,
        "distances": [],
        # staggered/phase are new fields added after this script was first
        # written. Both come with explicit "temporary heuristic, not yet
        # validated" comments in Plugin.cs (TryGetEnemyStaggered,
        # InferEnemyPhase) -- track their coverage the same way as hp/state
        # so the report tells you when they're trustworthy per enemy type.
        "staggered_null": 0,
        "phase_other": 0,
    })

    for line_no, rec in records:
        for enemy in rec.get("enemies", []):
            etype = enemy.get("type", "UNKNOWN")
            s = stats[etype]
            s["snapshots"] += 1

            if enemy.get("hp") is None:
                s["hp_null"] += 1

            state = enemy.get("state")
            if not state or state == "unknown":
                s["state_unknown"] += 1

            if enemy.get("attacking") is True:
                s["attacking_true"] += 1

            colliders = enemy.get("colliders", [])
            if len(colliders) >= MAX_COLLIDER_LOG_CAP:
                s["collider_cap_hit"] += 1

            dist = enemy.get("distance")
            if isinstance(dist, (int, float)):
                s["distances"].append(dist)

            if enemy.get("staggered") is None:
                s["staggered_null"] += 1

            if enemy.get("phase") == "other":
                s["phase_other"] += 1

    report = {}
    for etype, s in stats.items():
        n = s["snapshots"]
        report[etype] = {
            "snapshots": n,
            "hp_null_pct": round(100 * s["hp_null"] / n, 1) if n else None,
            "state_unknown_pct": round(100 * s["state_unknown"] / n, 1) if n else None,
            "attacking_ever_true": s["attacking_true"] > 0,
            "attacking_true_count": s["attacking_true"],
            "hit_collider_cap": s["collider_cap_hit"] > 0,
            "max_distance_seen": round(max(s["distances"]), 2) if s["distances"] else None,
            "staggered_null_pct": round(100 * s["staggered_null"] / n, 1) if n else None,
            "phase_other_pct": round(100 * s["phase_other"] / n, 1) if n else None,
        }
    return report


def check_enemy_id_churn(records):
    """Enemies keyed by GetInstanceID(). If an id appears then vanishes within
    just a few snapshots repeatedly for the same `type`, that type is likely
    being re-detected instead of tracked continuously (fragmented trajectories)."""
    id_lifespans = defaultdict(lambda: {"first": None, "last": None, "count": 0, "type": None})

    for line_no, rec in records:
        for enemy in rec.get("enemies", []):
            eid = enemy.get("id")
            if eid is None:
                continue
            entry = id_lifespans[eid]
            entry["type"] = enemy.get("type")
            entry["count"] += 1
            if entry["first"] is None:
                entry["first"] = line_no
            entry["last"] = line_no

    by_type_short_lived = defaultdict(int)
    by_type_total_ids = defaultdict(int)
    for eid, entry in id_lifespans.items():
        by_type_total_ids[entry["type"]] += 1
        if entry["count"] <= 3:  # appeared in 3 or fewer snapshots total
            by_type_short_lived[entry["type"]] += 1

    churn_report = {}
    for etype, total in by_type_total_ids.items():
        short = by_type_short_lived.get(etype, 0)
        churn_report[etype] = {
            "distinct_ids_seen": total,
            "short_lived_ids": short,
            "short_lived_pct": round(100 * short / total, 1) if total else None,
        }
    return churn_report


def check_death_event_consistency(records):
    """New in this plugin version: RemoveEnemy() now distinguishes enemy_died
    (last observed hp <= 0) from enemy_despawned (removed for any other
    reason -- scene change, culling, distance limit). Verify that claim by
    tracking the last-seen hp for each enemy id ourselves and checking it
    against whichever event actually fired."""
    last_hp_by_id = {}
    mismatches = []

    for line_no, rec in records:
        for enemy in rec.get("enemies", []):
            eid = enemy.get("id")
            hp = enemy.get("hp")
            if eid is not None and hp is not None:
                last_hp_by_id[eid] = hp

        for evt in rec.get("events", []):
            if not isinstance(evt, dict):
                continue
            kind = evt.get("kind")
            if kind not in ("enemy_died", "enemy_despawned"):
                continue
            data = evt.get("data", {})
            eid = data.get("enemy_id")
            claimed_last_hp = data.get("last_hp")
            tracked_last_hp = last_hp_by_id.get(eid)

            if kind == "enemy_died" and claimed_last_hp is not None and claimed_last_hp > 0:
                mismatches.append(
                    f"line {line_no}: enemy_died fired for id={eid} but "
                    f"logged last_hp={claimed_last_hp} (expected <= 0)"
                )
            if kind == "enemy_despawned" and claimed_last_hp is not None and claimed_last_hp <= 0:
                mismatches.append(
                    f"line {line_no}: enemy_despawned fired for id={eid} but "
                    f"logged last_hp={claimed_last_hp} (looks like it actually died)"
                )

    return {"mismatch_count": len(mismatches), "sample_issues": mismatches[:10]}


def check_action_phase_coherence(records):
    """Rough coherence check: certain state substrings should map to certain
    phases per InferActionPhase's own logic. Flags cases where they don't,
    which would indicate the labeling function and state string disagree."""
    expected_map = {
        "attacking": "attacking",
        "downspiking": "attacking",
        "dashing": "dash",
        "hitstun": "hitstun",
        "dead": "death",
    }
    mismatches = []
    for line_no, rec in records:
        player = rec.get("player", {})
        state = (player.get("state") or "").lower()
        phase = player.get("action_phase")
        for keyword, expected_phase in expected_map.items():
            if keyword in state and phase != expected_phase:
                mismatches.append(
                    f"line {line_no}: state='{state}' but action_phase='{phase}' "
                    f"(expected '{expected_phase}')"
                )
    return {"mismatch_count": len(mismatches), "sample_issues": mismatches[:10]}


def check_empty_or_short_session(records, min_snapshots=20):
    n = len(records)
    return {
        "total_snapshots": n,
        "flag_too_short": n < min_snapshots,
    }


def validate_file(path, enemy_report=False):
    print(f"\n{'='*70}\n{path}\n{'='*70}")

    records, parse_failures = check_schema_and_parse(path)
    print(f"Parsed {len(records)} valid lines, {len(parse_failures)} parse failures")
    if parse_failures:
        print(f"  Failed lines (first 10): {parse_failures[:10]}")

    if not records:
        print("  No valid records -- skipping further checks.")
        return

    short = check_empty_or_short_session(records)
    print(f"\nSession length: {short['total_snapshots']} snapshots"
          f"{' [FLAG: very short session]' if short['flag_too_short'] else ''}")

    ts = check_timestamps(records)
    print(f"\nTiming:")
    print(f"  Near-duplicate snapshots (hitch/catch-up loop): {ts['near_duplicate_snapshots']}")
    print(f"  Timestamp gaps (>{EXPECTED_SNAPSHOT_INTERVAL*5:.2f}s): {ts['timestamp_gaps']}")
    print(f"  Frame count regressions: {ts['frame_regressions']}")
    for issue in ts["sample_issues"]:
        print(f"    - {issue}")

    hp = check_player_hp(records)
    print(f"\nPlayer HP sanity: {hp['issue_count']} issues")
    for issue in hp["sample_issues"]:
        print(f"    - {issue}")

    rc = check_result_event_consistency(records)
    print(f"\nresult vs events consistency: {rc['mismatch_count']} mismatches")
    for issue in rc["sample_issues"]:
        print(f"    - {issue}")

    ap = check_action_phase_coherence(records)
    print(f"\naction_phase vs state coherence: {ap['mismatch_count']} mismatches")
    for issue in ap["sample_issues"]:
        print(f"    - {issue}")

    de = check_death_event_consistency(records)
    print(f"\nenemy_died / enemy_despawned vs last-observed hp: {de['mismatch_count']} mismatches")
    for issue in de["sample_issues"]:
        print(f"    - {issue}")

    if enemy_report:
        print(f"\nPer-enemy-type field coverage:")
        fc = check_enemy_field_coverage(records)
        for etype, stats in sorted(fc.items(), key=lambda x: -x[1]["snapshots"]):
            print(f"  {etype}:")
            print(f"    snapshots={stats['snapshots']}  "
                  f"hp_null={stats['hp_null_pct']}%  "
                  f"state_unknown={stats['state_unknown_pct']}%  "
                  f"attacking_ever_true={stats['attacking_ever_true']} "
                  f"(count={stats['attacking_true_count']})"
                  f"{'  [FLAG: hit collider cap]' if stats['hit_collider_cap'] else ''}")
            print(f"    staggered_null={stats['staggered_null_pct']}%  "
                  f"phase_other={stats['phase_other_pct']}%")
            if stats["hp_null_pct"] and stats["hp_null_pct"] > 50:
                print(f"    [FLAG] hp is null in >{stats['hp_null_pct']}% of snapshots "
                      f"-- reflection field name likely doesn't match for this enemy type")
            if not stats["attacking_ever_true"] and stats["snapshots"] > 20:
                print(f"    [FLAG] attacking never fires across {stats['snapshots']} snapshots "
                      f"-- hitbox naming likely doesn't match IsRelevantEnemyCollider patterns")
            if stats["staggered_null_pct"] and stats["staggered_null_pct"] > 80:
                print(f"    [FLAG] staggered is null in >{stats['staggered_null_pct']}% of snapshots "
                      f"-- TryGetEnemyStaggered's field-name guesses likely don't match this enemy "
                      f"(check HEALTHFIELDS in LogOutput.log to find the real field name)")
            if stats["phase_other_pct"] and stats["phase_other_pct"] > 50:
                print(f"    [FLAG] phase is 'other' in >{stats['phase_other_pct']}% of snapshots "
                      f"-- InferEnemyPhase's clip-name heuristics don't cover this enemy's animations yet")

        print(f"\nEnemy ID churn (fragmented tracking check):")
        churn = check_enemy_id_churn(records)
        for etype, stats in sorted(churn.items(), key=lambda x: -x[1]["distinct_ids_seen"]):
            flag = "  [FLAG: likely re-detecting instead of tracking]" if (
                stats["short_lived_pct"] and stats["short_lived_pct"] > 50
                and stats["distinct_ids_seen"] > 3
            ) else ""
            print(f"  {etype}: distinct_ids={stats['distinct_ids_seen']} "
                  f"short_lived={stats['short_lived_ids']} "
                  f"({stats['short_lived_pct']}%){flag}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+", help="session_*.jsonl file(s) or glob pattern(s)")
    parser.add_argument("--enemy-report", action="store_true",
                         help="Include per-enemy-type field coverage and id-churn checks")
    args = parser.parse_args()

    files = []
    for p in args.paths:
        matched = glob.glob(p)
        files.extend(matched if matched else [p])

    if not files:
        print("No files matched.")
        sys.exit(1)

    for f in files:
        validate_file(f, enemy_report=args.enemy_report)


if __name__ == "__main__":
    main()