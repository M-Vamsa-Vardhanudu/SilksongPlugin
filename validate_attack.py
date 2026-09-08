"""
extract_attack_windows.py

Pulls out every moment a given enemy type shows attacking=true, with its
timestamp, and groups consecutive true-frames into "attack windows" (start
time -> end time) instead of a flat list of every 0.05s snapshot. This is
what you actually want to check against footage -- not 40 individual
snapshot lines for one 2-second attack, but "Song Reed attacked from
12.3s to 14.1s" as a single window to jump to in your recording.

Usage:
    python extract_attack_windows.py session_XXXX.jsonl "Song Reed"
    python extract_attack_windows.py session_XXXX.jsonl        (lists all enemy types + counts)
"""

import json
import sys


def load_records(path):
    records = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return records


def list_enemy_types(records):
    counts = {}
    attack_counts = {}
    for rec in records:
        for enemy in rec.get("enemies", []):
            t = enemy.get("type", "UNKNOWN")
            counts[t] = counts.get(t, 0) + 1
            if enemy.get("attacking"):
                attack_counts[t] = attack_counts.get(t, 0) + 1

    print(f"{'Enemy type':30s} {'snapshots':>10s} {'attacking=true':>15s}")
    for t in sorted(counts):
        print(f"{t:30s} {counts[t]:>10d} {attack_counts.get(t, 0):>15d}")


def extract_windows(records, enemy_type):
    """Track attacking state per enemy id, collapse consecutive true-frames
    into (start_ts, end_ts, att_src) windows."""
    open_windows = {}  # id -> {start, last_seen, att_src}
    closed_windows = []

    for rec in records:
        ts = rec.get("timestamp")
        seen_ids_this_frame = set()

        for enemy in rec.get("enemies", []):
            if enemy.get("type") != enemy_type:
                continue

            eid = enemy.get("id")
            seen_ids_this_frame.add(eid)
            attacking = enemy.get("attacking", False)
            att_src = enemy.get("att_src")

            if attacking:
                if eid not in open_windows:
                    open_windows[eid] = {"start": ts, "last_seen": ts, "att_src": att_src}
                else:
                    open_windows[eid]["last_seen"] = ts
                    if att_src:
                        open_windows[eid]["att_src"] = att_src
            else:
                if eid in open_windows:
                    w = open_windows.pop(eid)
                    closed_windows.append((eid, w["start"], w["last_seen"], w["att_src"]))

        # enemy id present in earlier frames but absent this frame (removed/despawned)
        # -- close any open window for ids no longer seen at all
        for eid in list(open_windows.keys()):
            if eid not in seen_ids_this_frame:
                w = open_windows.pop(eid)
                closed_windows.append((eid, w["start"], w["last_seen"], w["att_src"]))

    # close anything still open at end of file
    for eid, w in open_windows.items():
        closed_windows.append((eid, w["start"], w["last_seen"], w["att_src"]))

    closed_windows.sort(key=lambda w: w[1])
    return closed_windows


def format_time(seconds):
    if seconds is None:
        return "?"
    m = int(seconds // 60)
    s = seconds % 60
    return f"{m}:{s:05.2f}"


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    path = sys.argv[1]
    records = load_records(path)
    print(f"Loaded {len(records)} snapshots from {path}\n")

    if len(sys.argv) < 3:
        list_enemy_types(records)
        return

    enemy_type = sys.argv[2]
    windows = extract_windows(records, enemy_type)

    if not windows:
        print(f"No attacking=true windows found for '{enemy_type}'.")
        print("(Either it never attacks, or the naming/scan-cadence issue is hiding it -- "
              "cross-check against footage regardless.)")
        return

    print(f"Found {len(windows)} attack window(s) for '{enemy_type}':\n")
    print(f"{'#':>3s}  {'id':>10s}  {'start':>9s}  {'end':>9s}  {'dur':>6s}  att_src")
    for i, (eid, start, end, att_src) in enumerate(windows, 1):
        dur = (end - start) if (start is not None and end is not None) else None
        dur_str = f"{dur:.2f}s" if dur is not None else "?"
        print(f"{i:>3d}  {eid:>10d}  {format_time(start):>9s}  {format_time(end):>9s}  "
              f"{dur_str:>6s}  {att_src or ''}")

    print(
        "\nJump to each start/end timestamp (mm:ss.ss, session-relative) in your\n"
        "recording and confirm the enemy is actually attacking there. Then do the\n"
        "reverse: scan your footage for attacks NOT listed above -- those are misses."
    )


if __name__ == "__main__":
    main()