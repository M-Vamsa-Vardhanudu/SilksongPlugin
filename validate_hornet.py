"""
validate_hornet.py

Builds a clean timeline of Hornet's own state changes -- grounded/airborne,
facing, state (attacking/dashing/jumping/etc), and position -- with
session-relative timestamps you can jump to directly in your recording.

Unlike the enemy attack-window extractor, there's no id ambiguity here:
there's only one Hornet. This is meant to be the simpler, less confusing
validation pass -- just watch each listed moment and confirm what the
data says matches what you see.

Usage:
    python validate_hornet.py session_XXXX.jsonl
    python validate_hornet.py session_XXXX.jsonl --grounded-only
    python validate_hornet.py session_XXXX.jsonl --state attacking
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


def format_time(seconds):
    if seconds is None:
        return "?"
    m = int(seconds // 60)
    s = seconds % 60
    return f"{m}:{s:05.2f}"


def build_state_timeline(records):
    """One row per moment Hornet's `state` field changes. Collapses
    consecutive identical states into a single (start, end, state) span,
    same idea as the enemy attack windows but for exactly one actor."""
    spans = []
    current = None  # {"state": str, "start": ts, "end": ts, "grounded": bool, "facing": ...}

    for rec in records:
        ts = rec.get("timestamp")
        player = rec.get("player", {})
        state = player.get("state")
        grounded = player.get("grounded")
        facing = player.get("facing")
        pos = player.get("position", {})

        if current is None or current["state"] != state:
            if current is not None:
                spans.append(current)
            current = {
                "state": state, "start": ts, "end": ts,
                "grounded": grounded, "facing": facing,
                "pos_start": pos,
            }
        else:
            current["end"] = ts

    if current is not None:
        spans.append(current)

    return spans


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    path = sys.argv[1]
    filter_state = None
    grounded_only = False

    args = sys.argv[2:]
    i = 0
    while i < len(args):
        if args[i] == "--state" and i + 1 < len(args):
            filter_state = args[i + 1]
            i += 2
        elif args[i] == "--grounded-only":
            grounded_only = True
            i += 1
        else:
            i += 1

    records = load_records(path)
    print(f"Loaded {len(records)} snapshots from {path}\n")

    spans = build_state_timeline(records)

    if filter_state:
        spans = [s for s in spans if s["state"] == filter_state]

    print(f"{'#':>4s}  {'state':<18s} {'start':>9s}  {'end':>9s}  {'dur':>6s}  "
          f"{'grounded':>8s}  facing  start_pos")
    for i, s in enumerate(spans, 1):
        dur = (s["end"] - s["start"]) if (s["start"] is not None and s["end"] is not None) else None
        dur_str = f"{dur:.2f}s" if dur is not None else "?"
        pos = s["pos_start"]
        pos_str = f"({pos.get('x', '?')}, {pos.get('y', '?')})" if pos else "?"
        grounded_str = str(s["grounded"]) if s["grounded"] is not None else "?"
        print(f"{i:>4d}  {str(s['state']):<18s} {format_time(s['start']):>9s}  "
              f"{format_time(s['end']):>9s}  {dur_str:>6s}  {grounded_str:>8s}  "
              f"{str(s['facing']):>6s}  {pos_str}")

    print(
        f"\n{len(spans)} state span(s) shown."
        "\nJump to each start timestamp in your recording and confirm Hornet's\n"
        "visible action matches the listed state, grounded flag, and facing.\n"
        "Use --state <name> to filter to one state (e.g. --state attacking),\n"
        "or --grounded-only to only show ground-truth-checkable spans."
    )


if __name__ == "__main__":
    main()