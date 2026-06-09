#!/usr/bin/env python3
"""
IOF Phase 2 Post-Processor
--------------------------
Reads the signals_raw.csv written by IOF_SignalEngine, simulates trade outcomes
using zone top/bottom as stop reference, then produces a full statistical report.

Usage:
    python phase2_analyze.py                          # uses default paths
    python phase2_analyze.py --csv D:/path/raw.csv   # custom CSV
    python phase2_analyze.py --rr 2.0                # target R:R (default 2.0)
    python phase2_analyze.py --forward 20            # bars to look forward (default 20)

Requirements:
    pip install pandas tabulate matplotlib

Output:
    phase2_report.txt   — full text report
    phase2_charts.png   — win rate / R:R charts
"""

import argparse
import sys
import os
from datetime import datetime, timedelta
from pathlib import Path

import pandas as pd
import numpy as np

try:
    from tabulate import tabulate
except ImportError:
    print("Missing dependency: pip install pandas tabulate matplotlib")
    sys.exit(1)

try:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    import matplotlib.gridspec as gridspec
    HAS_MPL = True
except ImportError:
    HAS_MPL = False

# ─────────────────────────────────────────────────────────────────────────────
# Constants
# ─────────────────────────────────────────────────────────────────────────────

DEFAULT_CSV    = r"D:\Custom\iof_discovery\out\signals_raw.csv"
TARGET_RR      = 2.0   # default reward:risk ratio for outcome simulation
FORWARD_BARS   = 20    # how many bars after signal to search for outcome
MIN_SAMPLES    = 10    # minimum signals in a slice to report it

# ─────────────────────────────────────────────────────────────────────────────
# Load CSV
# ─────────────────────────────────────────────────────────────────────────────

def load_csv(path: str) -> pd.DataFrame:
    if not os.path.exists(path):
        print(f"ERROR: CSV not found at {path}")
        print("Enable 'Phase 2: enable CSV log' in IOF_SignalEngine settings and collect data first.")
        sys.exit(1)

    df = pd.read_csv(path, parse_dates=["ts"])
    required = {"ts","symbol","tf","price","zone_id","is_demand","zone_score_raw",
                "zone_type","touch_num","total","grade",
                "zone_q","delta_q","abs_q","day_q","met_q","fresh_q",
                "ib_formed","ib_high","ib_low",
                "bar_delta","bar_close","bar_open","bar_high","bar_low","bar_volume"}
    missing = required - set(df.columns)
    if missing:
        print(f"ERROR: CSV is missing columns: {missing}")
        sys.exit(1)

    df["is_demand"] = df["is_demand"].astype(bool)
    df["ib_formed"] = df["ib_formed"].astype(bool)
    df.sort_values("ts", inplace=True)
    df.reset_index(drop=True, inplace=True)
    print(f"Loaded {len(df):,} signals from {path}")
    return df

# ─────────────────────────────────────────────────────────────────────────────
# Outcome simulation
# ─────────────────────────────────────────────────────────────────────────────

def simulate_outcomes(df: pd.DataFrame, target_rr: float) -> pd.DataFrame:
    """
    For each signal, estimate stop distance from zone_score_raw and zone boundaries
    implied by bar context, then mark WIN/LOSS/OPEN based on forward bar movement.

    Because the CSV only has the entry bar (not subsequent bars), we use a
    heuristic: zone_score_raw carries zone top/bottom indirectly via zone_q.
    We approximate stop distance as:
        - Demand: entry_price - bar_low  (conservative: zone holds above bar low)
        - Supply: bar_high - entry_price

    Target = entry ± (stop_dist * target_rr)

    Forward outcome is determined by checking bar_high/bar_low progression
    within the same symbol+tf group sorted by ts.
    """
    df = df.copy()
    df["stop_dist"]  = np.where(
        df["is_demand"],
        (df["price"] - df["bar_low"]).clip(lower=0.25),
        (df["bar_high"] - df["price"]).clip(lower=0.25)
    )
    df["target_dist"] = df["stop_dist"] * target_rr
    df["target_price"] = np.where(
        df["is_demand"],
        df["price"] + df["target_dist"],
        df["price"] - df["target_dist"]
    )
    df["stop_price"] = np.where(
        df["is_demand"],
        df["price"] - df["stop_dist"],
        df["price"] + df["stop_dist"]
    )
    df["outcome"] = "OPEN"
    df["outcome_r"] = np.nan

    # Group by symbol+tf so we only look forward within same instrument/timeframe
    for key, grp in df.groupby(["symbol", "tf"]):
        idxs = grp.index.tolist()
        for pos, idx in enumerate(idxs):
            row = df.loc[idx]
            future = idxs[pos+1 : pos+1+FORWARD_BARS]
            if not future:
                continue
            fut = df.loc[future]
            is_demand = row["is_demand"]
            target    = row["target_price"]
            stop      = row["stop_price"]
            stop_dist = row["stop_dist"]

            outcome = "OPEN"
            outcome_r = np.nan
            for _, frow in fut.iterrows():
                if is_demand:
                    if frow["bar_high"] >= target:
                        outcome = "WIN"
                        outcome_r = target_rr
                        break
                    if frow["bar_low"] <= stop:
                        outcome = "LOSS"
                        outcome_r = -1.0
                        break
                else:
                    if frow["bar_low"] <= target:
                        outcome = "WIN"
                        outcome_r = target_rr
                        break
                    if frow["bar_high"] >= stop:
                        outcome = "LOSS"
                        outcome_r = -1.0
                        break
            df.at[idx, "outcome"]   = outcome
            df.at[idx, "outcome_r"] = outcome_r

    return df

# ─────────────────────────────────────────────────────────────────────────────
# Analysis helpers
# ─────────────────────────────────────────────────────────────────────────────

def slice_stats(df: pd.DataFrame) -> dict:
    closed = df[df["outcome"] != "OPEN"]
    n      = len(closed)
    if n == 0:
        return {"n": 0, "win_pct": 0.0, "avg_r": 0.0, "expectancy": 0.0, "open": len(df)}
    wins   = (closed["outcome"] == "WIN").sum()
    avg_r  = closed["outcome_r"].mean()
    win_pct = wins / n * 100
    expectancy = avg_r  # already signed
    return {
        "n": n,
        "wins": int(wins),
        "losses": int(n - wins),
        "open": int((df["outcome"] == "OPEN").sum()),
        "win_pct": round(win_pct, 1),
        "avg_r": round(avg_r, 2),
        "expectancy": round(expectancy, 2),
    }

def slice_table(df: pd.DataFrame, column: str, label: str) -> list[list]:
    rows = []
    for val in sorted(df[column].unique()):
        sub = df[df[column] == val]
        s = slice_stats(sub)
        if s["n"] < MIN_SAMPLES:
            continue
        rows.append([
            f"{label}={val}", s["n"], s.get("wins","-"), s.get("losses","-"),
            s["open"], f"{s['win_pct']}%", s["avg_r"], s["expectancy"]
        ])
    return rows

HEADERS = ["Slice", "N", "W", "L", "Open", "Win%", "Avg R", "Expectancy"]

# ─────────────────────────────────────────────────────────────────────────────
# Hour-of-day bucketing
# ─────────────────────────────────────────────────────────────────────────────

def add_hour_bucket(df: pd.DataFrame) -> pd.DataFrame:
    df = df.copy()
    # Convert ts to Eastern time (approximate: UTC-4 during DST, UTC-5 standard)
    # Simple approach: user's machine likely already logs in local time
    df["hour_et"] = df["ts"].dt.hour
    buckets = []
    for h in df["hour_et"]:
        if 9 <= h < 10:
            buckets.append("09:30-10:30")
        elif 10 <= h < 12:
            buckets.append("10:30-12:00")
        elif 12 <= h < 14:
            buckets.append("12:00-14:00")
        elif 14 <= h < 16:
            buckets.append("14:00-16:00")
        else:
            buckets.append("other")
    df["session_window"] = buckets
    return df

# ─────────────────────────────────────────────────────────────────────────────
# Report generation
# ─────────────────────────────────────────────────────────────────────────────

def generate_report(df: pd.DataFrame, target_rr: float) -> str:
    lines = []
    def h(title): lines.append(f"\n{'='*70}\n{title}\n{'='*70}")
    def sub(title): lines.append(f"\n--- {title} ---")

    lines.append("IOF PHASE 2 SIGNAL ANALYSIS REPORT")
    lines.append(f"Generated : {datetime.now():%Y-%m-%d %H:%M}")
    lines.append(f"Target R:R : {target_rr}")
    lines.append(f"Forward    : {FORWARD_BARS} bars")

    # ── Overview ──────────────────────────────────────────────────────────────
    h("OVERVIEW")
    s = slice_stats(df)
    lines.append(f"Total signals   : {len(df):,}")
    lines.append(f"Closed          : {s['n']:,}  (Win {s['wins']} / Loss {s['losses']})")
    lines.append(f"Still open      : {s['open']:,}")
    lines.append(f"Overall win %   : {s['win_pct']}%")
    lines.append(f"Avg R per trade : {s['avg_r']}")
    lines.append(f"Expectancy      : {s['expectancy']} R")

    date_range = f"{df['ts'].min():%Y-%m-%d}  →  {df['ts'].max():%Y-%m-%d}"
    lines.append(f"Date range      : {date_range}")
    lines.append(f"Symbols         : {', '.join(df['symbol'].unique())}")

    # ── By Grade ──────────────────────────────────────────────────────────────
    h("BY GRADE")
    rows = slice_table(df, "grade", "grade")
    lines.append(tabulate(rows, headers=HEADERS, tablefmt="simple"))

    # ── By Zone Type ──────────────────────────────────────────────────────────
    h("BY ZONE TYPE  (RBR / DBD / DBR / RBD)")
    rows = slice_table(df, "zone_type", "type")
    lines.append(tabulate(rows, headers=HEADERS, tablefmt="simple"))

    # ── Demand vs Supply ──────────────────────────────────────────────────────
    h("DEMAND vs SUPPLY")
    for label, flag in [("Demand", True), ("Supply", False)]:
        sub_df = df[df["is_demand"] == flag]
        s2 = slice_stats(sub_df)
        lines.append(f"{label:8s}  N={s2['n']:4d}  Win={s2['win_pct']}%  AvgR={s2['avg_r']}  Exp={s2['expectancy']}")

    # ── By Touch Number ───────────────────────────────────────────────────────
    h("BY TOUCH COUNT")
    df["touch_bucket"] = df["touch_num"].clip(upper=3).map({1:"1st",2:"2nd",3:"3rd+"})
    rows = slice_table(df, "touch_bucket", "touch")
    lines.append(tabulate(rows, headers=HEADERS, tablefmt="simple"))

    # ── By Session Window ─────────────────────────────────────────────────────
    h("BY SESSION WINDOW")
    rows = slice_table(df, "session_window", "window")
    lines.append(tabulate(rows, headers=HEADERS, tablefmt="simple"))

    # ── Absorption filter ─────────────────────────────────────────────────────
    h("ABSORPTION FILTER  (abs_q > 0 vs abs_q = 0)")
    for label, mask in [("Absorbed (abs_q>0)", df["abs_q"] > 0), ("No absorption", df["abs_q"] == 0)]:
        s2 = slice_stats(df[mask])
        if s2["n"] >= MIN_SAMPLES:
            lines.append(f"{label:30s}  N={s2['n']:4d}  Win={s2['win_pct']}%  AvgR={s2['avg_r']}  Exp={s2['expectancy']}")

    # ── IB context ────────────────────────────────────────────────────────────
    h("INITIAL BALANCE CONTEXT")
    for label, mask in [("IB formed", df["ib_formed"] == True), ("IB not formed yet", df["ib_formed"] == False)]:
        s2 = slice_stats(df[mask])
        if s2["n"] >= MIN_SAMPLES:
            lines.append(f"{label:25s}  N={s2['n']:4d}  Win={s2['win_pct']}%  AvgR={s2['avg_r']}  Exp={s2['expectancy']}")

    # ── Score component correlations ──────────────────────────────────────────
    h("SCORE COMPONENT CORRELATION WITH OUTCOME")
    closed = df[df["outcome"] != "OPEN"].copy()
    closed["win_bin"] = (closed["outcome"] == "WIN").astype(int)
    components = ["zone_q","delta_q","abs_q","day_q","met_q","fresh_q","total"]
    lines.append(f"{'Component':<12}  {'Corr with WIN':>14}  {'Win avg':>8}  {'Loss avg':>9}")
    for c in components:
        corr = closed[c].corr(closed["win_bin"])
        win_avg  = closed.loc[closed["win_bin"]==1, c].mean()
        loss_avg = closed.loc[closed["win_bin"]==0, c].mean()
        lines.append(f"  {c:<12}  {corr:>14.3f}  {win_avg:>8.1f}  {loss_avg:>9.1f}")

    # ── Best filter combos ────────────────────────────────────────────────────
    h("RECOMMENDED FILTER COMBOS  (A+ signals only)")
    ap = df[df["grade"] == "A+"]
    if len(ap) >= MIN_SAMPLES:
        combos = [
            ("A+ only",                  ap),
            ("A+ + touch=1",             ap[ap["touch_num"] == 1]),
            ("A+ + touch=1 + abs>0",     ap[(ap["touch_num"] == 1) & (ap["abs_q"] > 0)]),
            ("A+ + IB formed",           ap[ap["ib_formed"] == True]),
            ("A+ + abs>0",               ap[ap["abs_q"] > 0]),
            ("A+ + 09:30-12:00",         ap[ap["session_window"].isin(["09:30-10:30","10:30-12:00"])]),
        ]
        rows = []
        for label, sub_df in combos:
            s2 = slice_stats(sub_df)
            if s2["n"] >= MIN_SAMPLES:
                rows.append([label, s2["n"], s2.get("wins","-"), s2.get("losses","-"),
                             s2["open"], f"{s2['win_pct']}%", s2["avg_r"], s2["expectancy"]])
        lines.append(tabulate(rows, headers=HEADERS, tablefmt="simple"))
    else:
        lines.append(f"Not enough A+ signals yet (need {MIN_SAMPLES}, have {len(ap)}).")

    # ── Bot config recommendation ─────────────────────────────────────────────
    h("BOT CONFIG RECOMMENDATION")
    best_combo = None
    best_exp   = -99
    closed_sub = df[df["outcome"] != "OPEN"]
    for mask_label, mask_df in [
        ("A+ + touch=1 + abs>0", closed_sub[(closed_sub["grade"]=="A+") & (closed_sub["touch_num"]==1) & (closed_sub["abs_q"]>0)]),
        ("A+ + abs>0",           closed_sub[(closed_sub["grade"]=="A+") & (closed_sub["abs_q"]>0)]),
        ("A+ only",              closed_sub[closed_sub["grade"]=="A+"]),
        ("A only",               closed_sub[closed_sub["grade"]=="A"]),
    ]:
        s2 = slice_stats(mask_df)
        if s2["n"] >= MIN_SAMPLES and s2["expectancy"] > best_exp:
            best_exp   = s2["expectancy"]
            best_combo = (mask_label, s2)

    if best_combo:
        label, s2 = best_combo
        lines.append(f"Best filter   : {label}")
        lines.append(f"Win rate      : {s2['win_pct']}%")
        lines.append(f"Expectancy    : {s2['expectancy']} R")
        lines.append(f"Sample size   : {s2['n']}")
        if s2["win_pct"] >= 55 and s2["expectancy"] > 0.3:
            lines.append("\nSTATUS: EDGE CONFIRMED — sufficient to consider bot execution")
        elif s2["win_pct"] >= 50 and s2["expectancy"] > 0:
            lines.append("\nSTATUS: MARGINAL EDGE — collect more data before automating")
        else:
            lines.append("\nSTATUS: NO EDGE DETECTED — do not automate; review signal logic")
    else:
        lines.append("Insufficient data for recommendation.")

    return "\n".join(lines)

# ─────────────────────────────────────────────────────────────────────────────
# Charts
# ─────────────────────────────────────────────────────────────────────────────

def generate_charts(df: pd.DataFrame, out_path: str):
    if not HAS_MPL:
        print("matplotlib not installed — skipping charts")
        return

    closed = df[df["outcome"] != "OPEN"].copy()
    if len(closed) < MIN_SAMPLES:
        print("Not enough closed trades for charts.")
        return

    fig = plt.figure(figsize=(16, 12), facecolor="#1a1a2e")
    fig.suptitle("IOF Phase 2 Signal Analysis", color="white", fontsize=16, y=0.98)
    gs = gridspec.GridSpec(2, 3, figure=fig, hspace=0.45, wspace=0.4)

    dark = "#1a1a2e"
    panel = "#16213e"
    green = "#00ff88"
    red   = "#ff4444"
    blue  = "#4a9eff"
    text  = "#e0e0e0"

    def bar_chart(ax, labels, win_pcts, ns, title):
        colors = [green if w >= 55 else (blue if w >= 50 else red) for w in win_pcts]
        bars = ax.bar(labels, win_pcts, color=colors, edgecolor="#333")
        ax.set_facecolor(panel)
        ax.set_title(title, color=text, fontsize=10)
        ax.set_ylabel("Win %", color=text)
        ax.tick_params(colors=text)
        ax.axhline(50, color="white", linestyle="--", linewidth=0.8, alpha=0.5)
        for bar, n in zip(bars, ns):
            ax.text(bar.get_x() + bar.get_width()/2, bar.get_height() + 0.5,
                    f"n={n}", ha="center", va="bottom", color=text, fontsize=8)
        ax.set_ylim(0, 100)
        for spine in ax.spines.values(): spine.set_edgecolor("#333")

    # 1. Win% by grade
    ax1 = fig.add_subplot(gs[0, 0])
    grade_order = ["A+", "A", "B", "C"]
    grades = [g for g in grade_order if g in closed["grade"].values]
    win_pcts = [((closed[closed["grade"]==g]["outcome"]=="WIN").sum() / len(closed[closed["grade"]==g]) * 100) for g in grades]
    ns = [len(closed[closed["grade"]==g]) for g in grades]
    bar_chart(ax1, grades, win_pcts, ns, "Win% by Grade")

    # 2. Win% by zone type
    ax2 = fig.add_subplot(gs[0, 1])
    ztypes = sorted(closed["zone_type"].unique())
    wp2 = [((closed[closed["zone_type"]==z]["outcome"]=="WIN").sum() / len(closed[closed["zone_type"]==z]) * 100) for z in ztypes]
    ns2 = [len(closed[closed["zone_type"]==z]) for z in ztypes]
    bar_chart(ax2, ztypes, wp2, ns2, "Win% by Zone Type")

    # 3. Win% by touch count
    ax3 = fig.add_subplot(gs[0, 2])
    closed["touch_bucket"] = closed["touch_num"].clip(upper=3).map({1:"1st",2:"2nd",3:"3rd+"})
    tbuckets = ["1st","2nd","3rd+"]
    tbuckets = [t for t in tbuckets if t in closed["touch_bucket"].values]
    wp3 = [((closed[closed["touch_bucket"]==t]["outcome"]=="WIN").sum() / len(closed[closed["touch_bucket"]==t]) * 100) for t in tbuckets]
    ns3 = [len(closed[closed["touch_bucket"]==t]) for t in tbuckets]
    bar_chart(ax3, tbuckets, wp3, ns3, "Win% by Touch Count")

    # 4. Score distribution: wins vs losses
    ax4 = fig.add_subplot(gs[1, 0])
    wins_scores  = closed[closed["outcome"]=="WIN"]["total"]
    loss_scores  = closed[closed["outcome"]=="LOSS"]["total"]
    ax4.hist(wins_scores,  bins=15, alpha=0.7, color=green, label="Win",  edgecolor="#333")
    ax4.hist(loss_scores,  bins=15, alpha=0.7, color=red,   label="Loss", edgecolor="#333")
    ax4.set_facecolor(panel)
    ax4.set_title("Score Distribution: Win vs Loss", color=text, fontsize=10)
    ax4.set_xlabel("Total Score", color=text)
    ax4.tick_params(colors=text)
    ax4.legend(facecolor=panel, labelcolor=text)
    for spine in ax4.spines.values(): spine.set_edgecolor("#333")

    # 5. Win% by session window
    ax5 = fig.add_subplot(gs[1, 1])
    windows = ["09:30-10:30","10:30-12:00","12:00-14:00","14:00-16:00","other"]
    windows = [w for w in windows if w in closed["session_window"].values
               and len(closed[closed["session_window"]==w]) >= MIN_SAMPLES]
    wp5 = [((closed[closed["session_window"]==w]["outcome"]=="WIN").sum() / len(closed[closed["session_window"]==w]) * 100) for w in windows]
    ns5 = [len(closed[closed["session_window"]==w]) for w in windows]
    bar_chart(ax5, windows, wp5, ns5, "Win% by Session Window")
    ax5.tick_params(axis='x', labelrotation=20)

    # 6. Equity curve (cumulative R)
    ax6 = fig.add_subplot(gs[1, 2])
    closed_sorted = closed.sort_values("ts")
    cum_r = closed_sorted["outcome_r"].fillna(0).cumsum()
    ax6.plot(range(len(cum_r)), cum_r.values, color=blue, linewidth=1.5)
    ax6.axhline(0, color="white", linestyle="--", linewidth=0.8, alpha=0.5)
    ax6.fill_between(range(len(cum_r)), cum_r.values, 0,
                     where=(cum_r.values >= 0), alpha=0.2, color=green)
    ax6.fill_between(range(len(cum_r)), cum_r.values, 0,
                     where=(cum_r.values < 0),  alpha=0.2, color=red)
    ax6.set_facecolor(panel)
    ax6.set_title("Equity Curve (cumulative R)", color=text, fontsize=10)
    ax6.set_xlabel("Trade #", color=text)
    ax6.set_ylabel("Cumulative R", color=text)
    ax6.tick_params(colors=text)
    for spine in ax6.spines.values(): spine.set_edgecolor("#333")

    plt.savefig(out_path, facecolor=dark, bbox_inches="tight", dpi=150)
    print(f"Charts saved → {out_path}")

# ─────────────────────────────────────────────────────────────────────────────
# Entry point
# ─────────────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="IOF Phase 2 Post-Processor")
    parser.add_argument("--csv",     default=DEFAULT_CSV,    help="Path to signals_raw.csv")
    parser.add_argument("--rr",      default=TARGET_RR,      type=float, help="Target R:R (default 2.0)")
    parser.add_argument("--forward", default=FORWARD_BARS,   type=int,   help="Forward bars for outcome sim")
    parser.add_argument("--out",     default="",             help="Output directory (default: same as CSV)")
    args = parser.parse_args()

    global FORWARD_BARS
    FORWARD_BARS = args.forward

    csv_dir  = os.path.dirname(os.path.abspath(args.csv))
    out_dir  = args.out if args.out else csv_dir

    report_path = os.path.join(out_dir, "phase2_report.txt")
    chart_path  = os.path.join(out_dir, "phase2_charts.png")

    df = load_csv(args.csv)
    df = add_hour_bucket(df)
    df = simulate_outcomes(df, args.rr)

    closed = df[df["outcome"] != "OPEN"]
    print(f"Outcomes resolved: {len(closed)} closed, {(df['outcome']=='OPEN').sum()} still open")

    report = generate_report(df, args.rr)
    print(report)

    with open(report_path, "w") as f:
        f.write(report)
    print(f"\nReport saved → {report_path}")

    generate_charts(df, chart_path)

if __name__ == "__main__":
    main()
