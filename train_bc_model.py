"""
train_bc_model.py

First MVP behavior cloning model. Loads bc_dataset.csv (from
build_bc_dataset.py), trains a small multi-label neural net to predict
Hornet's action buttons from game state, and reports per-action
precision/recall on a held-out tail of the data.

IMPORTANT SCOPE NOTE: with a single session, this is a smoke test of the
pipeline (does data flow end-to-end and does *something* get learned), not
a real generalization test. The split here is time-ordered within-session
(train on the first N%, validate on the last 100-N%) rather than a proper
held-out-session split, because a session-level split needs 2+ sessions.
Once you have more sessions, re-run with --session-split instead.

Usage:
    python train_bc_model.py bc_dataset.csv
    python train_bc_model.py bc_dataset.csv --train-frac 0.8
    python train_bc_model.py bc_dataset.csv bc_dataset2.csv --session-split
"""

import sys
import argparse
import numpy as np
import pandas as pd
from sklearn.neural_network import MLPClassifier
from sklearn.metrics import precision_score, recall_score, f1_score
from sklearn.preprocessing import StandardScaler

STATE_COLUMNS = [
    "hp", "max_hp", "pos_x", "pos_y", "vel_x", "vel_y", "grounded", "facing",
    "enemy0_present", "enemy0_rel_x", "enemy0_rel_y", "enemy0_distance", "enemy0_attacking",
    "enemy1_present", "enemy1_rel_x", "enemy1_rel_y", "enemy1_distance", "enemy1_attacking",
]

ACTION_COLUMNS = [
    "action_left", "action_right", "action_up", "action_down", "action_jump",
    "action_dash", "action_attack", "action_skill", "action_heal",
]


def load_dataset(paths):
    frames = [pd.read_csv(p) for p in paths]
    df = pd.concat(frames, ignore_index=True)
    missing = [c for c in STATE_COLUMNS + ACTION_COLUMNS if c not in df.columns]
    if missing:
        raise ValueError(f"Dataset is missing expected columns: {missing}")
    return df


def time_ordered_split(df, train_frac):
    """Split within each session by timestamp, not randomly -- adjacent
    frames are highly correlated, so a random row split would leak
    near-duplicate information between train and validation."""
    train_parts, val_parts = [], []
    for session_id, group in df.groupby("session_id"):
        group = group.sort_values("timestamp")
        cutoff = int(len(group) * train_frac)
        train_parts.append(group.iloc[:cutoff])
        val_parts.append(group.iloc[cutoff:])
    return pd.concat(train_parts), pd.concat(val_parts)


def session_split(df):
    """Proper held-out-session split: needs 2+ distinct session_id values."""
    sessions = df["session_id"].unique()
    if len(sessions) < 2:
        raise ValueError(
            f"--session-split requires 2+ sessions, found {len(sessions)}. "
            "Use the default time-ordered split for a single session."
        )
    # Hold out the last session (by name sort, which is usually chronological
    # given the plugin's timestamped filenames) as validation.
    sessions_sorted = sorted(sessions)
    val_session = sessions_sorted[-1]
    train_df = df[df["session_id"] != val_session]
    val_df = df[df["session_id"] == val_session]
    print(f"Held out session for validation: {val_session}")
    return train_df, val_df


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("inputs", nargs="+", help="Dataset CSV file(s) from build_bc_dataset.py")
    parser.add_argument("--train-frac", type=float, default=0.8,
                         help="Fraction of each session used for training in time-ordered split (default 0.8)")
    parser.add_argument("--session-split", action="store_true",
                         help="Use a proper held-out-session split instead of time-ordered (needs 2+ sessions)")
    args = parser.parse_args()

    df = load_dataset(args.inputs)
    n_sessions = df["session_id"].nunique()
    print(f"Loaded {len(df)} rows from {n_sessions} session(s).")

    if args.session_split:
        train_df, val_df = session_split(df)
    else:
        if n_sessions == 1:
            print("NOTE: single session -- this is a pipeline smoke test, "
                  "not a generalization test. See the script's docstring.")
        train_df, val_df = time_ordered_split(df, args.train_frac)

    print(f"Train rows: {len(train_df)}  |  Validation rows: {len(val_df)}")

    X_train_raw = train_df[STATE_COLUMNS].to_numpy(dtype=float)
    X_val_raw = val_df[STATE_COLUMNS].to_numpy(dtype=float)
    Y_train = train_df[ACTION_COLUMNS].to_numpy(dtype=int)
    Y_val = val_df[ACTION_COLUMNS].to_numpy(dtype=int)

    if len(val_df) == 0:
        print("No validation rows available -- check --train-frac or your session split.")
        sys.exit(1)

    # Standardize state features (helps the MLP converge; the model itself
    # doesn't care about the raw scale of e.g. position vs a 0/1 flag, but
    # gradient-based training does).
    scaler = StandardScaler()
    X_train = scaler.fit_transform(X_train_raw)
    X_val = scaler.transform(X_val_raw)

    # A first-pass action label distribution check -- if any action is
    # never (or almost never) pressed in the training split, the model
    # can't learn it, and that's worth knowing before blaming the model.
    print("\nAction frequency in training data (fraction of frames pressed):")
    for i, col in enumerate(ACTION_COLUMNS):
        frac = Y_train[:, i].mean()
        flag = "  <-- rare, may not learn well" if frac < 0.01 else ""
        print(f"  {col:18s} {frac:6.3f}{flag}")

    print("\nTraining MLP (small, single hidden layer -- this is an MVP smoke test)...")
    model = MLPClassifier(
        hidden_layer_sizes=(64,),
        max_iter=300,
        random_state=0,
        early_stopping=True,
        validation_fraction=0.1,
    )
    model.fit(X_train, Y_train)

    Y_pred = model.predict(X_val)

    print("\nPer-action validation metrics (held-out data):")
    print(f"  {'action':18s} {'precision':>10s} {'recall':>10s} {'f1':>10s} {'support':>8s}")
    for i, col in enumerate(ACTION_COLUMNS):
        support = int(Y_val[:, i].sum())
        if support == 0:
            print(f"  {col:18s} {'--':>10s} {'--':>10s} {'--':>10s} {support:>8d}  (never pressed in val data)")
            continue
        p = precision_score(Y_val[:, i], Y_pred[:, i], zero_division=0)
        r = recall_score(Y_val[:, i], Y_pred[:, i], zero_division=0)
        f1 = f1_score(Y_val[:, i], Y_pred[:, i], zero_division=0)
        print(f"  {col:18s} {p:10.3f} {r:10.3f} {f1:10.3f} {support:8d}")

    print(
        "\nHow to read this: precision/recall near 0 for a rare action usually just\n"
        "means there wasn't enough training data for that button, not that the\n"
        "pipeline is broken. Focus on actions with reasonable support (say, >5%\n"
        "of frames) as the real signal on whether this approach is working."
    )


if __name__ == "__main__":
    main()