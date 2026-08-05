# IOF Trade State Dashboard

A self-contained, browser-based dashboard for the IOF v2 bot. It reads **live
per-account JSON** the bot rewrites on every trade close (and on a short live
timer), and aggregates a **trade-log CSV** for week / all-time stats. No build
step, no server dependency, no external network calls — one HTML file.

![sections: Live State · Risk Guards · Today's Edge · Equity Curve · Exit Quality · All-Time/Rolling]

---

## Two data sources

| Source | Role | Written by |
|--------|------|------------|
| `iof_v2_dashboard_<account>.json` | The **"now"** view — live state + today's rolling stats + today's equity curve. One file per account; the dashboard reads **every** one it finds, and sums them into a **Portfolio** tab. | The bot, on each trade close and a short live-state timer. |
| `iof_v2_tradelog_<account>.csv` | **Source of truth** for anything multi-day. The dashboard aggregates **all** of them for week / all-time edge stats + full equity curve (filtered per account, or summed). | The bot (already building it). |

Point the dashboard at the **folder** the bot writes to and it globs both
patterns automatically — no per-file setup, and new accounts appear on their
own as their files show up.

Everything the dashboard shows is derivable from the trade-log fields
(`pnl_r`, `mae`, `mfe`, `exit_reason`, `side`, `qty`, …) plus the bot's live
state. **R-first, dollars alongside.**

---

## What it displays

**① Live state** (per account) — WORKING/STOPPED · halt (NONE / PROFIT_LOCK /
DAILY_LOSS / DISABLED) · in-position card (side, qty, entry, unrealized R + $,
open MAE / MFE) · trend gate (OFF / LONG_ONLY / SHORT_ONLY / NO_TRADE).

**② Today's edge** (resets at the 3 PM PDT roll) — net P&L (R + $), trades,
W/L/scratch, volume, win rate, payoff ratio, profit factor, breakeven WR, edge
margin, expectancy (R + $), avg win/loss, best/worst, max consecutive W/L.

**③ Exit & execution quality** — exit breakdown (TARGET / STOP / ADVERSE_TAPE /
ZONE_INVALID, counts + %), avg winner MAE vs avg loser MAE (room-to-breathe),
avg MFE, % of winners that hit full TP.

**④ Risk guards** — daily-loss remaining, distance to profit lock (or LOCKED),
giveback from peak, day peak, R value ($/R).

**⑤ Equity curve** — today's cumulative, toggle R / $.

**⑥ All-time & rolling** — the same edge + exit stats and a full equity curve,
recomputed from `trade_log.csv`, over a **WEEK** or **ALL-TIME** window,
filtered to the active account (or all).

Plus an **All accounts** roll-up tab that sums P&L and re-derives win rate,
payoff, profit factor, edge margin, and the exit mix across every connected
account, with a per-account roster.

---

## Connecting it

Open `dashboard/index.html`. Ways to feed it:

1. **Connect data folder (recommended, Chrome/Edge)** — click **📁 Connect data
   folder** and pick the folder your bot writes to (e.g.
   `C:\Users\chris\absorption_study\`). The dashboard globs it for every
   `iof_v2_dashboard_*.json` and `iof_v2_tradelog_*.csv`, re-reads them every
   2 s as the bot rewrites them, and adds new accounts automatically as their
   files appear. One click, all accounts.
2. **Serve the folder** — if `index.html` is served over http(s) alongside the
   files, it auto-loads `accounts.json` (a manifest listing the account JSONs
   and tradelog CSVs) on startup and polls them. Browsers can't list a
   directory over http, which is why served mode needs the manifest:
   ```bash
   python3 -m http.server 8080     # then open http://localhost:8080/
   ```
3. **Add one account file / Load a trade-log CSV / Upload snapshot / drag-and-drop**
   — per-file fallbacks; CSV loading accepts multiple files at once.

> Folder + live-file access use the File System Access API (Chrome/Edge).
> Firefox/Safari fall back to URL polling or snapshot upload.

---

## The JSON the bot writes

Per account, rewritten on each trade close and on a short timer for live state.
Real JSON has no comments — the annotations below are for reference. See
[`iof_v2_dashboard_lucid.json`](./iof_v2_dashboard_lucid.json) and
[`iof_v2_dashboard_mffu.json`](./iof_v2_dashboard_mffu.json) for working samples.

```jsonc
{
  "account": "LT-ON5X6Q58",
  "label": "Lucid",
  "symbol": "MNQ",
  "planId": "lucid",              // stable key — used as the tab id
  "updatedUtc": "2026-08-05T11:10:49Z",  // change-detect signal; bump every write
  "dollarsPerR": 41.0,            // avg realized 1R in $, for display

  "live": {
    "status": "WORKING",          // WORKING | STOPPED
    "halt": "PROFIT_LOCK",        // NONE | PROFIT_LOCK | DAILY_LOSS | DISABLED
    "inPosition": false,
    "side": null,                 // LONG | SHORT | null
    "qty": 0,
    "entry": null,
    "unrealizedUsd": 0.0,
    "unrealizedR": 0.0,
    "openMaePts": 0.0,
    "openMfePts": 0.0,
    "trendGate": "OFF",           // OFF | LONG_ONLY | SHORT_ONLY | NO_TRADE
    "distToProfitLockUsd": 44.5,  // P&L room before profit-lock arms (0 if halted)
    "distToDailyLossUsd": 1135.0  // room before the daily-loss halt
  },

  "day": {                        // resets at the 3 PM PDT roll
    "date": "2026-08-05",
    "netUsd": 135.0,  "netR": 3.29,
    "peakUsd": 479.5, "peakR": 11.7,
    "givebackFromPeakUsd": 344.5,
    "trades": 33, "volume": 96,
    "wins": 7, "losses": 26, "scratches": 0,
    "winRatePct": 21.2,
    "payoffR": 3.47,              // avg win R ÷ avg loss R
    "profitFactor": 0.93,
    "breakevenWrPct": 22.4,       // 1 / (1 + payoff)
    "edgeMarginPct": -1.2,        // winRate − breakevenWR
    "expectancyR": -0.01, "expectancyUsd": -0.4,
    "avgWinR": 0.79,  "avgWinUsd": 118.0,
    "avgLossR": 0.23, "avgLossUsd": 41.0,   // stored positive
    "bestR": 0.91, "worstR": -0.46,
    "maxConsecLosses": 5, "maxConsecWins": 2,
    "exits": { "TARGET": 5, "STOP": 2, "ADVERSE_TAPE": 25, "ZONE_INVALID": 1 },
    "avgWinnerMaePts": 2.1,       // room-to-breathe: how far winners dip
    "avgLoserMaePts": 5.4,
    "avgMfePts": 8.2,
    "winnersHitFullTpPct": 31.0
  },

  "equityCurve": [                // today's booked cumulative, one point per close
    { "t": "2026-08-05T03:40:45Z", "cumUsd": 403.0, "cumR": 9.83 },
    { "t": "2026-08-05T07:05:55Z", "cumUsd": 403.5, "cumR": 9.84 }
  ]
}
```

These are exactly the fields the bot provides — nothing extra. Missing fields
render as `—`.

### `accounts.json` (served mode only)

Folder mode globs the directory and needs no manifest. Served (http) mode
can't list a directory, so it reads this index of the files to load:

```json
{
  "accounts": ["iof_v2_dashboard_lucid.json", "iof_v2_dashboard_mffu.json"],
  "tradelogs": ["iof_v2_tradelog_lucid.csv", "iof_v2_tradelog_mffu.csv"]
}
```

---

## The trade-log CSVs

One file per account: `iof_v2_tradelog_<account>.csv`. The dashboard loads and
concatenates all of them, then filters by the active account tab (or sums for
Portfolio). Header row + one row per closed trade. Column order is free; these
names are matched case-insensitively. Extra columns are ignored.

```
timestamp,account,label,symbol,side,qty,entry,exit,exit_reason,pnl_r,pnl_usd,mae,mfe
2026-08-05T03:54:00Z,LT-ON5X6Q58,Lucid,MNQ,LONG,1,5337,5336.66,ADVERSE_TAPE,-0.34,-13.94,2.44,1.76
```

Used columns: `timestamp` (ISO 8601), `account` (matched to the JSON's
`account` to filter by tab), `side`, `qty`, `exit_reason`, `pnl_r`, `pnl_usd`,
`mae`, `mfe`. Week = trades within 7 days of the latest row.

---

## Notes

- Pure client-side. No data leaves the browser; no external requests (the page
  makes no CDN/font/analytics calls).
- Polls every 2 s; change-detected via `updatedUtc` (falls back to a content
  hash), so re-renders only happen when a file actually changes.
- Colors are status-coded: green = up / long / good, red = down / short / at
  risk, amber = caution (giveback, adverse tape), cyan/purple = neutral accents.
