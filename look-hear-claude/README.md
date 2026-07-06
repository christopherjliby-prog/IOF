# look-hear-claude — IOF order-flow & Quantower strategy research

**For the Claude building the IOF bot.** This folder is a calibration + strategy reference compiled 2026-07-06. Every external number here is an **anchor to compare against**, not a threshold to adopt — calibrate from Chris's own MNQ/MES recordings.

## Read in this order

| # | File | What it is |
|---|---|---|
| 1 | [`1_orderflow_code_and_calibration.md`](./1_orderflow_code_and_calibration.md) | **Start here.** 15 order-flow sources (9 Quantower/C#, 6 Python) with extracted computation code + a consolidated per-metric threshold table (imbalance, delta/CVD, absorption, exhaustion, divergence, book/OFI, regime, TP/SL sizing) — units flagged raw vs normalized. Part 3 sits IOF's own constants beside the external corpus. |
| 2 | [`2_scaleout_multiTP_systems.md`](./2_scaleout_multiTP_systems.md) | The 3 Quantower bots that actually run multi-TP scale-out (bank-and-run / 3-tranche % split / tiered API), verbatim, + a scale-out boilerplate. |
| 3 | [`3_strategy_microscope_full_corpus.md`](./3_strategy_microscope_full_corpus.md) | Full ~29-bot corpus, verbatim Entry/Exit/SL/TP grouped by family, correlation analysis, and three boilerplates (order-flow / universal / futures ES-NQ-micros). |
| 4 | [`4_calibration_anchors_literature.md`](./4_calibration_anchors_literature.md) | Published-literature reference ranges (source → number/formula → rationale → link), with a blank column for Chris's live-derived values. |

## The three findings that matter most for calibration

1. **Imbalance 3:1 / ≥3 stacked rows is bulletproof** — six independent implementations agree with IOF's `ImbalanceRatio 3.0` / `MinStackedRows 3` exactly. Keep it; don't re-derive.
2. **IOF's ATR-normalized absorption** (`range < 0.6×ATR` & `|delta| > 1.5×20-bar-avg`) is **more portable than every external raw-tick/raw-contract version** — confirm the multipliers on MNQ/MES recordings, but the approach is the strongest in the corpus.
3. **Multi-TP scale-out is rare** — of ~29 bots only 3 do it; IOF's 3-TP zone-multiple cascade (`1×/2×/3× zoneHeight`, TP1→BE→trail) is more sophisticated than any of them.

## Hard rules when using these numbers

- Every `[raw]` value (e.g. delta 500/100, sweep 20, book >15) was set for **someone else's instrument** — rescale to MNQ/MES before comparing (micro = 1/10 of full-size in dollar terms).
- Normalized values (ratio, ATR-multiple, %-of-volume, z-score, quantile) compare directly across instruments.
- `iof-specs` (the IOF system's own repo) is private, so its C# was captured as **search fragments** — the absorption threshold literals live in the spec `.md`s, not captured code lines. Verify against the real source.

## Coverage / provenance

Compiled via GitHub `search_code` + `raw.githubusercontent.com` verbatim reads + WebSearch. The local `agent-reach` stack (yt-dlp/YouTube, Bilibili, RSS, Jina) was **not available** in the build environment — zero video/RSS coverage; all findings rest on GitHub + web.
