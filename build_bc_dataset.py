"""
build_bc_dataset.py

Extracts (state_vector, action_vector) pairs from SilksongInspector session
JSONL files, for behavior cloning. Produces a single CSV you can load into
any ML library (pandas -> numpy -> PyTorch/sklearn/whatever).

What's included, and why:
  - Hornet's own state: position, velocity, hp/max_hp, grounded, facing.
    Validated field-by-field earlier (validate_hornet.py) -- trustworthy.
  - Nearest-enemy features: relative_position, distance, attacking, for the
    K nearest enemies (K=2 by default). Padded with zeros + a presence flag
    when fewer than K enemies exist. Enemy IDENTITY (id, name) is
    deliberately excluded from the feature vector -- the model should learn
    from relative geometry and state, not from memorizing specific enemy
    instances, since generalization depends on that.
  - Action labels: the 9 action.* booleans, used as-is (multi-hot).

What's deliberately excluded, and why:
  - Any snapshot where player.state == "dead" -- these fall inside the
    known ~5s respawn freeze (position/velocity frozen, garbage for
    training). Confirmed via validate_hornet.py across multiple sessions.
  - staggered field -- 100% null in every session checked so far.
  - Hornet's own hurtbox/attack-AoE bounds -- not captured by Plugin.cs yet.
  - enemy.phase / windup heuristic -- unvalidated clip-name matching.

Usage:
    python build_bc_dataset.py session1.jsonl session2.jsonl ... -o dataset.csv
    python build_bc_dataset.py "C:\\path\\to\\SilksongInspectorData\\*.jsonl" -o dataset.csv
"""

import json
import sys
import csv
import glob
import argparse

K_NEAREST_ENEMIES = 2

ACTION_KEYS = ["left", "right", "up", "down", "jump", "dash", "attack", "skill", "heal"]

STATE_COLUMNS = (
    ["session_id", "timestamp"] +
    ["hp", "max_hp", "pos_x", "pos_y", "vel_x", "vel_y", "grounded", "facing"] +
    sum(
        (
            [f"enemy{i}_present", f"enemy{i}_rel_x", f"enemy{i}_rel_y",
             f"enemy{i}_distance", f"enemy{i}_attacking"]
            for i in range(K_NEAREST_ENEMIES)
        ),
        [],
    )
)

ACTION_COLUMNS = [f"action_{k}" for k in ACTION_KEYS]


def load_records(path):
    records = []
    malformed = 0
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except json.JSONDecodeError:
                malformed += 1
    if malformed:
        print(f"  [{path}] skipped {malformed} malformed line(s)")
    return records


def bool_to_int(v):
    if v is True:
        return 1
    if v is False:
        return 0
    return 0  # treat None/missing as false rather than dropping the row


def extract_row(session_id, rec):
    player = rec.get("player", {})
    state = player.get("state")

    # Exclude anything inside the known death/respawn freeze window.
    if state == "dead":
        return None

    pos = player.get("position", {}) or {}
    vel = player.get("velocity", {}) or {}
    hp = player.get("hp")
    max_hp = player.get("max_hp")
    grounded = player.get("grounded")
    facing = player.get("facing")

    if hp is None or max_hp is None:
        return None  # incomplete snapshot, skip rather than guess

    row = {
        "session_id": session_id,
        "timestamp": rec.get("timestamp"),
        "hp": hp,
        "max_hp": max_hp,
        "pos_x": pos.get("x"),
        "pos_y": pos.get("y"),
        "vel_x": vel.get("x", 0.0),
        "vel_y": vel.get("y", 0.0),
        "grounded": bool_to_int(grounded),
        "facing": facing if facing in (1, -1) else 0,
    }

    enemies = rec.get("enemies", []) or []
    # Sort by distance; nearest first. Enemies missing a distance field are
    # pushed to the end rather than crashing the sort.
    enemies_sorted = sorted(
        enemies, key=lambda e: e.get("distance", float("inf"))
    )

    for i in range(K_NEAREST_ENEMIES):
        prefix = f"enemy{i}_"
        if i < len(enemies_sorted):
            e = enemies_sorted[i]
            rel = e.get("relative_position", {}) or {}
            row[prefix + "present"] = 1
            row[prefix + "rel_x"] = rel.get("x", 0.0)
            row[prefix + "rel_y"] = rel.get("y", 0.0)
            row[prefix + "distance"] = e.get("distance", 0.0)
            row[prefix + "attacking"] = bool_to_int(e.get("attacking"))
        else:
            row[prefix + "present"] = 0
            row[prefix + "rel_x"] = 0.0
            row[prefix + "rel_y"] = 0.0
            row[prefix + "distance"] = 0.0
            row[prefix + "attacking"] = 0

    action = rec.get("action", {}) or {}
    for k in ACTION_KEYS:
        row[f"action_{k}"] = bool_to_int(action.get(k))

    return row


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("inputs", nargs="+", help="Session JSONL file(s) or glob pattern(s)")
    parser.add_argument("-o", "--output", default="bc_dataset.csv", help="Output CSV path")
    args = parser.parse_args()

    paths = []
    for pattern in args.inputs:
        matched = glob.glob(pattern)
        if matched:
            paths.extend(matched)
        else:
            paths.append(pattern)  # let load_records report if it's genuinely missing

    if not paths:
        print("No input files matched.")
        sys.exit(1)

    all_rows = []
    total_snapshots = 0
    dropped_dead = 0
    dropped_incomplete = 0

    for path in sorted(set(paths)):
        session_id = path
        records = load_records(path)
        total_snapshots += len(records)
        session_rows = 0

        for rec in records:
            row = extract_row(session_id, rec)
            if row is None:
                # distinguish why, for the summary -- helps sanity-check
                # the drop rate isn't hiding a bigger problem
                if (rec.get("player", {}) or {}).get("state") == "dead":
                    dropped_dead += 1
                else:
                    dropped_incomplete += 1
                continue
            all_rows.append(row)
            session_rows += 1

        print(f"  [{path}] {len(records)} snapshots -> {session_rows} usable rows")

    if not all_rows:
        print("No usable rows extracted. Check that input files are valid session JSONL.")
        sys.exit(1)

    fieldnames = STATE_COLUMNS + ACTION_COLUMNS
    with open(args.output, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(all_rows)

    print(f"\nTotal snapshots read: {total_snapshots}")
    print(f"Dropped (dead/respawn window): {dropped_dead}")
    print(f"Dropped (incomplete data): {dropped_incomplete}")
    print(f"Usable rows written: {len(all_rows)}")
    print(f"Output: {args.output}")
    print(f"\nColumns: {', '.join(fieldnames)}")


if __name__ == "__main__":
    main()