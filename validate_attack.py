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
    into (start_ts, end_ts, att_src) windows. Also tags each window with
    context that explains near-zero-duration blips instead of leaving them
    looking like real attacks:
      - player_dead: Hornet's own state was "dead" at the window's start,
        which usually means the window falls inside the death/respawn
        freeze (confirmed cause of two 0.00s windows found by hand).
      - enemy_missing_next: this enemy id doesn't appear in the very next
        snapshot at all (not just attacking=false) -- consistent with the
        despawn/scene-transition case, not a genuine sustained attack.
    Neither tag proves the window is fake on its own; they're context to
    prioritize which windows are worth a manual footage check first.
    """
    open_windows = {}  # id -> {start, last_seen, att_src, player_dead}
    closed_windows = []

    for i, rec in enumerate(records):
        ts = rec.get("timestamp")
        seen_ids_this_frame = set()
        player_state = rec.get("player", {}).get("state")
        player_dead_now = player_state == "dead"

        for enemy in rec.get("enemies", []):
            if enemy.get("type") != enemy_type:
                continue

            eid = enemy.get("id")
            seen_ids_this_frame.add(eid)
            attacking = enemy.get("attacking", False)
            att_src = enemy.get("att_src")

            if attacking:
                if eid not in open_windows:
                    open_windows[eid] = {
                        "start": ts, "last_seen": ts, "att_src": att_src,
                        "player_dead": player_dead_now,
                    }
                else:
                    open_windows[eid]["last_seen"] = ts
                    if att_src:
                        open_windows[eid]["att_src"] = att_src
            else:
                if eid in open_windows:
                    w = open_windows.pop(eid)
                    missing_next = _enemy_missing_in_next(records, i, eid)
                    closed_windows.append((eid, w["start"], w["last_seen"], w["att_src"],
                                            w["player_dead"], missing_next))

        # enemy id present in earlier frames but absent this frame (removed/despawned)
        # -- close any open window for ids no longer seen at all
        for eid in list(open_windows.keys()):
            if eid not in seen_ids_this_frame:
                w = open_windows.pop(eid)
                closed_windows.append((eid, w["start"], w["last_seen"], w["att_src"],
                                        w["player_dead"], True))

    # close anything still open at end of file
    for eid, w in open_windows.items():
        closed_windows.append((eid, w["start"], w["last_seen"], w["att_src"],
                                w["player_dead"], False))

    closed_windows.sort(key=lambda w: w[1])
    return closed_windows


def _enemy_missing_in_next(records, current_index, eid):
    """Check whether this enemy id is absent from the very next snapshot's
    enemies list entirely (as opposed to present but attacking=false)."""
    if current_index + 1 >= len(records):
        return True
    next_ids = {e.get("id") for e in records[current_index + 1].get("enemies", [])}
    return eid not in next_ids


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

    MIN_TRUSTED_DURATION = 0.10  # two snapshots at 0.05s cadence

    real_windows = []
    suspect_windows = []
    for w in windows:
        eid, start, end, att_src, player_dead, missing_next = w
        dur = (end - start) if (start is not None and end is not None) else 0.0
        is_short = dur < MIN_TRUSTED_DURATION
        # A short window is only flagged suspect if it also has a concrete
        # explanation (death/respawn window, or the enemy vanished right
        # after) -- a short window with neither is still worth a look, but
        # isn't automatically explained away.
        if is_short and (player_dead or missing_next):
            suspect_windows.append(w)
        else:
            real_windows.append(w)

    def print_table(rows):
        print(f"{'#':>3s}  {'id':>10s}  {'start':>9s}  {'end':>9s}  {'dur':>6s}  att_src")
        for i, (eid, start, end, att_src, player_dead, missing_next) in enumerate(rows, 1):
            dur = (end - start) if (start is not None and end is not None) else None
            dur_str = f"{dur:.2f}s" if dur is not None else "?"
            flags = []
            if player_dead:
                flags.append("player_dead")
            if missing_next:
                flags.append("enemy_missing_next")
            flag_str = f"  [{', '.join(flags)}]" if flags else ""
            print(f"{i:>3d}  {eid:>10d}  {format_time(start):>9s}  {format_time(end):>9s}  "
                  f"{dur_str:>6s}  {att_src or ''}{flag_str}")

    print(f"{len(real_windows)} likely-real attack window(s) for '{enemy_type}':\n")
    print_table(real_windows)

    if suspect_windows:
        print(f"\n{len(suspect_windows)} suspect window(s) -- short duration (<{MIN_TRUSTED_DURATION}s) "
              f"AND explained by a death/respawn or despawn event nearby.\n"
              f"These are unlikely to be real attacks; check footage before trusting them:\n")
        print_table(suspect_windows)

    print(
        "\nJump to each start/end timestamp (mm:ss.ss, session-relative) in your\n"
        "recording and confirm the enemy is actually attacking there. Then do the\n"
        "reverse: scan your footage for attacks NOT listed above -- those are misses."
    )


if __name__ == "__main__":
    main()