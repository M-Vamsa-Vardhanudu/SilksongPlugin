# SilksongPlugin

This project has two halves:

1. **SilksongInspector** (`Plugin.cs`) — a BepInEx plugin that tracks Hornet,
   enemies, and hazards during gameplay and writes 20Hz JSONL session logs.
2. **Python tooling** — scripts to validate that data, extract a behavior
   cloning (BC) dataset from it, and train a first MVP model.

Status as of this README: movement actions (`left`/`right`/`up`) train
reasonably well from session data. `attack` and `dash` do not yet — see
[Known Issues](#known-issues--limitations) for why, and what's needed to fix
each.

---

## 1. Prerequisites

- Hollow Knight: Silksong with BepInEx installed
- The SilksongInspector plugin built and placed in
  `BepInEx/plugins/` (see the plugin's own build instructions — not covered
  here, this README covers the Python side)
- Python 3.10+ on the machine you'll run the scripts from (can be the same
  machine as the game)
- OBS Studio (free), only needed if you're doing footage-based validation
  of the tracked data, not needed for day-to-day dataset building/training

---

## 2. Python environment setup

### Windows (PowerShell)

```powershell
# from the project folder
python -m venv venv
venv\Scripts\Activate.ps1

# if PowerShell blocks script execution:
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser

pip install -r requirements.txt
```

To deactivate later: `deactivate`

### Linux / macOS

```bash
python3 -m venv venv
source venv/bin/activate

pip install -r requirements.txt
```

To deactivate later: `deactivate`

### `requirements.txt`

```
pandas
scikit-learn
```

(`numpy` comes in as a dependency of both; no need to list it separately.)

---

## 3. Where session data lives

The plugin writes one JSONL file per play session to:

```
<Game Install Folder>\BepInEx\plugins\SilksongInspectorData\session_<timestamp>.jsonl
```

Each line is one 0.05s snapshot: Hornet's state/position/velocity/hp, every
tracked enemy's state/position/attack-hitbox info, active hazards, the raw
button state that frame, and any discrete events (damage, death, etc.).

BepInEx's own diagnostic log (separate from the above — useful for
debugging *why* something isn't tracked, e.g. checking real hitbox names)
is at:

```
<Game Install Folder>\BepInEx\LogOutput.log
```

---

## 4. Recording good sessions

- **Target enemies matter.** Enemies with confirmed, reliable attack
  detection so far: Song Reed, Song Pilgrim 01/03, Mite Heavy. Some enemies
  (Song Pilgrim Maestro, Citadel Bat, Lightbearer in sessions checked so
  far) never show `attacking: true` — this may mean they're genuinely
  non-attacking support units, or may mean their attack hitboxes aren't
  covered by the plugin's naming whitelist yet. Don't assume a new,
  unchecked enemy's silence means "no attacks" without a footage check.
- **If you die, stop that recording and start a new one** rather than
  continuing through the respawn. The ~5 second post-death/respawn window
  produces frozen-position, unusable data (see Known Issues) — it isn't
  worth trying to "recover" a session through it. Everything recorded
  *before* the death is still perfectly good data.
- **Play normally.** Don't perform for the dataset — behavior cloning
  learns whatever's actually in the data, so natural play is what you want
  captured, not exaggerated or repeated button-mashing.
- More continuous, low-death minutes per session is better than more
  short, fragmented sessions.

---

## 5. Validation scripts

These check that the plugin's tracked data actually matches reality — run
these on new enemy types or after any plugin change, not something you
need for every training run once you trust the data.

### `validate_overall.py` — aggregate data-quality checks

Checks timestamp cadence, HP sanity, internal consistency (do
`result`/`events` agree, does `action_phase` map deterministically from
`state`), and per-enemy-type field coverage (is `hp`/`attacking` reliable
for this enemy).

```bash
python validate_overall.py "path/to/session_XXXX.jsonl" --enemy-report

# If you use uv to run python
uv run python validate_overall.py "path/to/session_XXXX.jsonl" --enemy-report
```

### `extract_attack_windows.py` — per-enemy attack timing for footage checks

Collapses raw per-snapshot `attacking: true` flags into distinct attack
windows (start/end/duration) for one enemy type, and flags likely-fake
short windows caused by death/respawn transitions.

```bash
# list all enemy types + how often each shows attacking=true
python extract_attack_windows.py "path/to/session_XXXX.jsonl"

# get windows for one specific enemy type
python extract_attack_windows.py "path/to/session_XXXX.jsonl" "Song Reed"

Note: If you use uv to run python, just add "uv run" before the above commands.
```

Cross-check a handful of the printed windows against footage; then do the
reverse — watch a stretch of footage and check whether every real attack
you see has a matching window. Missing windows there are your real miss
rate, not just what's flagged automatically.

### `validate_hornet.py` — Hornet's own state timeline

Same idea as the above, but for Hornet specifically (no enemy-id ambiguity
since there's only one Hornet — simpler to validate).

```bash
python validate_hornet.py "path/to/session_XXXX.jsonl"

# filter to one state, e.g. to check attack timing specifically
python validate_hornet.py "path/to/session_XXXX.jsonl" --state attacking

Note: If you use uv to run python, just add "uv run" before the above commands.
```

---

## 6. Building the BC dataset

`build_bc_dataset.py` turns one or more session JSONL files into a single
CSV of (state, action) rows, ready for training. It excludes snapshots
inside the known death/respawn freeze window automatically.

```bash
# single session
python build_bc_dataset.py "path/to/session_A.jsonl" -o bc_dataset.csv

# multiple sessions combined into one dataset (recommended once you have 2+)
python build_bc_dataset.py "path/to/session_A.jsonl" "path/to/session_B.jsonl" "path/to/session_C.jsonl" -o bc_dataset.csv

# or glob an entire folder
python build_bc_dataset.py "path/to/SilksongInspectorData/session_*.jsonl" -o bc_dataset.csv

# or if you use uv
Note: If you use uv to run python, just add "uv run" before the above commands.
```

This always overwrites `-o`'s target with one combined file — if you want
to add newly recorded sessions, just re-run against all your JSONL files
together, don't try to append to the CSV directly. Keep the original
JSONL files around; if the extracted feature set changes later (e.g. once
Hornet's own hurtbox/attack-AoE tracking is added to the plugin), you'll
need to rebuild from raw JSONL, not from an old CSV.

---

## 7. Training the BC model

```bash
# single session, or a quick smoke test on a combined dataset
# (time-ordered split within/across sessions -- weaker validation)
python train_bc_model.py bc_dataset.csv

# proper held-out-session validation (needs 2+ distinct sessions in the CSV)
python train_bc_model.py bc_dataset.csv --session-split
```

Read the output in this order:
1. **Action frequency table** — tells you upfront which buttons have
   enough examples to learn at all. Anything under ~1% of frames is
   unlikely to train well regardless of model quality.
2. **Per-action precision/recall** — the real result. Ignore `support: 0`
   rows (never pressed in validation data — nothing to score). For
   everything else, both precision and recall matter: high precision/low
   recall means the model is too conservative about that button; the
   reverse means it's over-triggering it.

---

## 8. Quick reference — the full pipeline, start to finish

```bash
# 1. one-time setup
python -m venv venv
venv\Scripts\Activate.ps1          # Windows
# source venv/bin/activate         # Linux/macOS
pip install -r requirements.txt

# 2. record sessions in-game (see section 4)

# 3. (optional) validate a session against footage
python validate_session.py "path/to/session.jsonl" --enemy-report
python extract_attack_windows.py "path/to/session.jsonl" "Song Reed"
python validate_hornet.py "path/to/session.jsonl"

# 4. build the training dataset from all recorded sessions
python build_bc_dataset.py "path/to/SilksongInspectorData/session_*.jsonl" -o bc_dataset.csv

# 5. train and evaluate
python train_bc_model.py bc_dataset.csv --session-split
```
