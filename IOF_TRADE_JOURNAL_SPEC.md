# IOF Trade Journal — Build Specification
**Version:** 1.0 | **Date:** 2026-06-03 | **Author:** Claude (Anthropic)
**Project:** TradePhantoms IOF v2 — Quantower Indicator Suite

---

## Overview

The IOF Trade Journal is a Quantower indicator that runs silently alongside the
IOF v2 indicator. It acts as an accountability partner — capturing every trade
fill with full IOF zone context, environment state, and entry quality grade,
then writing a structured log in real time that can be opened in Excel or
Google Sheets.

No external services. No Rithmic API fees. No manual import. Zero friction.

---

## How It Works

```
Quantower Position Feed
        ↓
IOF_TradeJournal Indicator
        ↓
  ┌─────────────┬──────────────┬──────────────┐
  │ Trade Data  │ Zone Context │ Environment  │
  │ (fills)     │ (IOFRegistry)│ (bars/trend) │
  └─────────────┴──────────────┴──────────────┘
        ↓
  Entry Quality Grade (A/B/C/D/F)
        ↓
  CSV + JSON log file (real time)
        ↓
  Daily HTML Summary Dashboard
```

---

## Data Captured Per Trade

### 1. Trade Identification
| Field | Type | Example |
|---|---|---|
| TradeId | string | "20260603-143201-MNQ" |
| Date | date | 2026-06-03 |
| EntryTime | datetime | 14:32:01.452 |
| ExitTime | datetime | 14:38:47.891 |
| HoldTimeSeconds | int | 406 |
| HoldTimeFormatted | string | "6m 46s" |
| Session | string | "NY_OPEN" / "NY_MID" / "NY_CLOSE" / "LONDON" |

### 2. Trade Execution
| Field | Type | Example |
|---|---|---|
| Symbol | string | "MNQ" |
| Direction | string | "LONG" / "SHORT" |
| Contracts | int | 3 |
| EntryPrice | double | 19842.50 |
| ExitPrice | double | 19887.25 |
| GrossPnL | double | 268.50 |
| Commission | double | 5.40 |
| NetPnL | double | 263.10 |
| ExitReason | string | "TP2" / "SL" / "TRAIL" / "MANUAL" / "BE" |
| RMultiple | double | 2.1 |

### 3. IOF Zone Context
| Field | Type | Example |
|---|---|---|
| NearestZoneType | string | "RBR" / "DBR" / "DBD" / "RBD" |
| NearestZoneTop | double | 19848.00 |
| NearestZoneBottom | double | 19838.00 |
| ZoneScore | double | 14.5 |
| ZoneScoreMax | int | 21 |
| EntryInsideZone | bool | true |
| EntryDistanceFromZoneEdge | int | 3 (ticks above/below zone edge) |
| DepartureMultiplier | double | 3.2 |
| AbsorptionMultiplier | double | 2.1 |
| MtfcBonus | double | 1.5 |
| HvnConfluence | bool | true |
| ZoneTouchCountAtEntry | int | 0 |
| ZonePurityAtEntry | string | "FRESH" / "TESTED" / "DEGRADED" |
| ZoneTimeframe | string | "5m" |

### 4. Environment
| Field | Type | Example |
|---|---|---|
| HtfTrend | string | "BULL" / "BEAR" / "FLAT" |
| ItfTrend | string | "BULL" / "BEAR" / "FLAT" |
| TradeWithTrend | bool | true |
| NearestHtfZoneDistance | int | 42 (ticks) |
| BarAtr20 | double | 18.5 (points) |
| TimeOfDayBucket | string | "0930-1000" |

### 5. Entry Quality Grade
| Grade | Criteria |
|---|---|
| A | Inside zone + MTFC + HVN + D≥2× + ABS≥2× |
| B | Inside zone + any 2 confluence factors |
| C | Inside zone + any 1 confluence factor |
| D | Inside zone, no confluence |
| F | Entry outside zone (chased) |

### 6. Exit Analysis
| Field | Type | Example |
|---|---|---|
| HitTp1 | bool | true |
| HitTp2 | bool | true |
| HitTp3 | bool | false |
| EarlyExit | bool | false |
| StopMoved | bool | false |
| MaxAdverseExcursion | double | -6.25 (points against position) |
| MaxFavorableExcursion | double | 52.50 (points in favor) |

---

## Output Files

### CSV Log (one row per trade)
Path: `C:\Quantower\Logs\IOF_Journal\trades_YYYY-MM-DD.csv`

All fields above as columns. Opens directly in Excel/Google Sheets.
New file per trading day. Appended in real time as trades close.

### JSON Log (machine-readable)
Path: `C:\Quantower\Logs\IOF_Journal\trades_YYYY-MM-DD.json`

Array of trade objects. Used by the HTML dashboard and any future
analytics tools.

### Daily HTML Dashboard
Path: `C:\Quantower\Logs\IOF_Journal\dashboard_YYYY-MM-DD.html`

Auto-generated at end of session. Shows:
- Day summary (total trades, win rate, net P&L, avg hold time)
- Grade distribution (how many A/B/C/D/F setups)
- Win rate by grade (A setups vs C setups — should be dramatically different)
- Win rate by zone type (RBR vs DBR vs DBD vs RBD)
- Win rate by session (NY Open vs Midday)
- Win rate with MTFC vs without
- Win rate with HVN vs without
- Best/worst D:× and ABS:× thresholds
- MAE/MFE analysis (how far against you before turning, how much left on table)

---

## Indicator Parameters

| Parameter | Default | Description |
|---|---|---|
| Log Directory | C:\Quantower\Logs\IOF_Journal | Where to write files |
| IOF Chart Symbol | (auto-detect) | Symbol to cross-reference with IOFZoneRegistry |
| IOF Timeframe | 5 | Timeframe period to look up zones |
| Zone Proximity Ticks | 8 | How close to zone counts as "inside zone" |
| Enable CSV Log | true | Write CSV file |
| Enable JSON Log | true | Write JSON file |
| Enable HTML Dashboard | true | Generate daily HTML summary |
| Show Overlay Labels | true | Show grade labels on chart at entry/exit |
| Alert on Grade D/F | true | Pop-up alert if you chase an entry |

---

## Accountability Features

### Real-Time Chart Overlay
- At each entry: shows grade label (A/B/C/D/F) and zone context
- At each exit: shows R multiple achieved and exit reason
- Color coded: A=green, B=cyan, C=yellow, D=orange, F=red

### Grade D/F Alert
If you enter outside a zone (Grade F) or inside a zone with zero
confluence (Grade D), an immediate pop-up alert fires:
> "⚠️ Grade F Entry — No IOF zone nearby. Are you chasing?"

This is the accountability partner moment — before the trade is
settled, you see the grade.

### Session Summary (EOD)
At 4:00 PM ET, the indicator writes the HTML dashboard and shows
a quick summary panel on the chart:
> "Today: 6 trades | 83% WR | +$401 net | 4×A 1×B 1×C | Avg hold 5m"

---

## Integration Points

### IOFZoneRegistry (existing)
`IOFZoneRegistry.GetZones(key)` — pulls the live zone snapshot
including Type, Score, Top, Bottom, TouchCount, IsTradeable.

The journal extends ZoneSnapshot to also read DepartureMultiplier,
AbsorptionMultiplier, MtfcBonus, and HvnConfluence from the
_zoneMetrics dictionary exposed via a new static accessor on the
IOF v2 indicator.

### Quantower Position Feed
`Core.Instance.Positions` — position opened/closed events.
`Core.Instance.OrderHistory` — filled order records with price/qty/time.

### Historical Data (for environment)
`this.HistoricalData` on the journal indicator — reads the last
20 bars for ATR calculation and trend context at entry time.

---

## Build Phases

### Phase 1 — Core Journal (build now)
- Position feed subscription
- CSV + JSON logging
- Basic trade fields (execution data only)
- File I/O infrastructure

### Phase 2 — Zone Context Layer
- IOFZoneRegistry lookup at entry time
- Zone proximity detection
- Grade computation
- Label overlay on chart

### Phase 3 — Environment Layer
- HTF/ITF trend at entry
- Session detection
- ATR / volatility context
- MAE/MFE tracking

### Phase 4 — Analytics Dashboard
- HTML report generation
- Win rate by grade/zone type/session
- TradeZella-inspired analytics layout
- Exportable to Google Sheets

---

## Files to Be Created

| File | Purpose |
|---|---|
| `IOF_TradeJournal.cs` | Main indicator |
| `JournalEntry.cs` | Trade data model / serialization |
| `JournalWriter.cs` | CSV/JSON/HTML file writer |
| `EntryGrader.cs` | Grade computation logic |
| `SessionClassifier.cs` | Time-of-day session detection |
| `IOF_TradeJournal.csproj` | Project file |
| `build_zip_journal.sh` | Build + package script |

---

## What This Replaces / Improves vs TradeZella

| Feature | TradeZella | IOF Journal |
|---|---|---|
| Trade import | Manual CSV upload | Automatic (live position feed) |
| Zone context | None | Full IOF zone data at entry |
| Setup grading | Manual tag | Automatic A/B/C/D/F grade |
| IOF-specific analytics | None | Win rate by zone type, D×, ABS×, MTFC |
| Cost | $49/month | Free (built into your indicator) |
| Data ownership | Their servers | Your machine |
| Accountability alerts | None | Real-time Grade D/F pop-up |
| Rithmic API needed | No | No |

---

*This spec will be updated with TradeZella analytics details once
the research is complete.*
