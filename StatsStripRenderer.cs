// =============================================================================
// StatsStripRenderer.cs
// -----------------------------------------------------------------------------
// Self-contained Quantower-side renderer for the IOF QuickEntry "Stats Strip".
//
// Modeled directly on the Pine Script v1.4 layout (6 columns x 4 rows) defined
// in C:\IOF2\previous_pinescript\message (4).txt around lines 1612-1700 and
// described in IOF_QuickEntry_Project_Audit.md Section 8.
//
// Rendering contract:
//   - Active trade present: rows 0-2 carry live trade context, row 3 carries
//     running stats.
//   - No active trade: row 0 shows "No live trade" placeholder, rows 1-2 show
//     dashes, row 3 shows running stats.
//
// LOCK / UNRL / FLOAT semantics (R units, scaled to $ via DollarRisk):
//   LOCK  = (curSL  - entry) / slDist            (long)
//         = (entry - curSL ) / slDist            (short)
//   UNRL  = (close  - entry) / slDist            (long)
//         = (entry - close ) / slDist            (short)
//   FLOAT = UNRL - LOCK                          (give-back gap)
//
// This file has no Quantower SDK dependency. Callers wire it up from inside
// OnPaintChart(args) via:
//   StatsStripRenderer.DrawStrip(args.Graphics, mainWindow.ClientRectangle, ...)
// =============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace TradePhantomsIOF.UI
{
    // -------------------------------------------------------------------------
    // Public data contract. Defined locally so this file does not depend on
    // any other IOF type. The integrating indicator constructs and populates
    // a StripData instance per paint.
    // -------------------------------------------------------------------------
    public class StripData
    {
        // Lifecycle gate: when false, the strip renders a "Lifecycle OFF" hint.
        public bool LifecycleOn;

        // Whether a live (state == ACTIVE) trade exists.
        public bool HasActiveTrade;

        // Active trade scalars.
        public int    TradeId;
        public string DirText;       // "L H" / "S L" / "L I" / etc.
        public int    Contracts;
        public double DollarRisk;    // configured $ risk for the trade.
        public int    HitTps;
        public int    TotTps;
        public double Entry;
        public double OrigSL;        // the original SL set at fill (for slDist).
        public double CurSL;         // the live trailed SL.
        public double[] Tps;         // length up to 5; only first TotTps used.
        public bool[]   TpHit;       // parallel to Tps.
        public double Close;         // current bar close.
        public string  NextTrigger;  // e.g. "TP2@4260 -> swing trail continues".
        public double  SlDist;       // |Entry - OrigSL|. If 0, LOCK/UNRL render as dashes.
        public string  SymbolPrefix; // "$" or "" — kept for future use.

        // Direction flag — used to flip LOCK/UNRL signs for shorts. We accept
        // either a boolean here or infer from DirText if the indicator did not
        // set it. Default true (long).
        public bool IsLong = true;

        // Running stats (closed trades).
        public int    Wins, Losses, BreakEvens;
        public double TotalR;
        public double TotalDollar;
        public double WinRate;       // 0..100
        public double ProfitFactor;
        public double AvgWinR;
        public double AvgLossR;      // typically negative (or absolute); render as-is.
        public int    MaxWinStreak;
        public int    MaxLossStreak;

        // Trend feature: trend state additions (per TP/PDF trend doctrine).
        // TrendState: "FLAT" / "BULL" / "BEAR" — drives the trend cell color/glyph.
        // ControllingPivotPrice: the HL (bull) or LH (bear) price; NaN if FLAT.
        // LastBreakAgo: pre-formatted age string supplied by master ("—", "5m ago").
        // TrendAligned: true if active trade direction matches trend; when false
        //   AND HasActiveTrade, the trend cell paints with a red warning bg.
        public string TrendState = "FLAT";
        public double ControllingPivotPrice = double.NaN;
        public string LastBreakAgo = "—";
        public bool   TrendAligned = true;
    }

    public enum StripPosition
    {
        TopLeft, TopCenter, TopRight,
        MiddleLeft, MiddleCenter, MiddleRight,
        BottomLeft, BottomCenter, BottomRight
    }

    // -------------------------------------------------------------------------
    // Renderer
    // -------------------------------------------------------------------------
    public static class StatsStripRenderer
    {
        // ---- Layout constants ------------------------------------------------
        private const int COLS         = 6;
        private const int ROWS         = 4;
        private const int CELL_PADDING = 4;       // px on each side, per cell.
        private const int EDGE_MARGIN  = 16;      // px from chartArea edge.
        private const int BORDER_WIDTH = 1;       // grid line thickness.

        // Background: semi-transparent dark, per spec (RGBA 20,20,30,200).
        private static readonly Color BG_COLOR        = Color.FromArgb(200,  20,  20,  30);
        private static readonly Color BORDER_COLOR    = Color.FromArgb(180,  90,  90, 110);
        private static readonly Color HEADER_BLUE_BG  = Color.FromArgb( 90,  20,  60, 160); // Lifecycle: ON cell.
        private static readonly Color HEADER_GREEN_BG = Color.FromArgb( 80,  20, 140,  60); // Trade #N cell when active.
        private static readonly Color HEADER_GRAY_BG  = Color.FromArgb( 80,  90,  90,  90); // Trade #N cell when idle.
        private static readonly Color NEXT_BG         = Color.FromArgb( 80,  90,  90,  90); // NEXT trigger cell.
        private static readonly Color LOCK_BG_TINT    = Color.FromArgb( 60,   0,   0,   0); // dimmer bg for LOCK/UNRL.

        // Foreground colors.
        private static readonly Color TXT_WHITE   = Color.White;
        private static readonly Color TXT_AQUA    = Color.Aqua;
        private static readonly Color TXT_LIME    = Color.Lime;
        private static readonly Color TXT_RED     = Color.Red;
        private static readonly Color TXT_ORANGE  = Color.Orange;
        private static readonly Color TXT_YELLOW  = Color.Yellow;
        private static readonly Color TXT_GRAY    = Color.FromArgb(160, 160, 160);
        private static readonly Color TXT_DIM_TP  = Color.FromArgb(120, 120, 120); // hit-TP de-emphasis.
        private static readonly Color TXT_TP_PEND = Color.LightCyan;               // pending TP.

        // Trend feature: dedicated colors for the trend cell (Row 0, col 5).
        // BULL uses LightGreen for slightly softer punch than TXT_LIME so the
        // header doesn't compete with the LOCK/UNRL cells. BEAR uses IndianRed
        // for the same reason. FLAT stays gray. Misalignment fills with a low-
        // alpha red wash so the warning is visible without obscuring text.
        private static readonly Color TREND_BULL_FG   = Color.LightGreen;
        private static readonly Color TREND_BEAR_FG   = Color.IndianRed;
        private static readonly Color TREND_FLAT_FG   = TXT_GRAY;
        private static readonly Color TREND_WARN_BG   = Color.FromArgb(80, 200, 50, 50);

        // Invariant culture so number formatting is stable in EU locales.
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        // ---- Public entry point ---------------------------------------------
        //
        // Draws the 6x4 stats strip. Safe to call every paint; performs no
        // allocation beyond cell strings and a Font instance per call.
        //
        // textSize: 1=Tiny(8pt) 2=Small(10) 3=Medium(12) 4=Large(14) 5=Huge(16).
        public static void DrawStrip(
            Graphics g,
            Rectangle chartArea,
            StripData data,
            StripPosition position,
            int textSize)
        {
            if (g == null || data == null) return;
            if (chartArea.Width <= 0 || chartArea.Height <= 0) return;

            // ---- Resolve font ----
            float ptSize = ResolvePtSize(textSize);
            // Consolas first; Courier New is the universal monospace fallback.
            using (var font = BuildMonoFont(ptSize))
            {
                // ---- Build the 6x4 cell matrix (text + colors) ----
                Cell[,] cells = BuildCells(data, font, g);

                // ---- Compute per-column widths and uniform row height ----
                float[] colWidths = new float[COLS];
                float   rowHeight = 0f;
                for (int r = 0; r < ROWS; r++)
                {
                    for (int c = 0; c < COLS; c++)
                    {
                        SizeF sz = cells[c, r].Size;
                        if (sz.Width  + 2 * CELL_PADDING > colWidths[c])
                            colWidths[c] = sz.Width + 2 * CELL_PADDING;
                        if (sz.Height + 2 * CELL_PADDING > rowHeight)
                            rowHeight = sz.Height + 2 * CELL_PADDING;
                    }
                }

                float totalWidth  = 0f;
                for (int c = 0; c < COLS; c++) totalWidth += colWidths[c];
                float totalHeight = rowHeight * ROWS;

                // Add the border allowance so the outer border does not clip.
                totalWidth  += BORDER_WIDTH;
                totalHeight += BORDER_WIDTH;

                // ---- Resolve top-left origin from StripPosition ----
                PointF origin = ResolveOrigin(chartArea, position, totalWidth, totalHeight);

                // ---- Save graphics state and elevate quality ----
                var prevSmoothing  = g.SmoothingMode;
                var prevTextRender = g.TextRenderingHint;
                g.SmoothingMode    = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                try
                {
                    // ---- Paint backdrop ----
                    using (var bgBrush = new SolidBrush(BG_COLOR))
                    {
                        g.FillRectangle(bgBrush, origin.X, origin.Y, totalWidth, totalHeight);
                    }

                    // ---- Paint cells (bg + text) row by row ----
                    using (var borderPen = new Pen(BORDER_COLOR, BORDER_WIDTH))
                    using (var textBrush = new SolidBrush(TXT_WHITE))
                    {
                        float y = origin.Y;
                        for (int r = 0; r < ROWS; r++)
                        {
                            float x = origin.X;
                            for (int c = 0; c < COLS; c++)
                            {
                                Cell cell = cells[c, r];
                                float w = colWidths[c];
                                float h = rowHeight;

                                // Cell background, if any.
                                if (cell.BgColor.A > 0)
                                {
                                    using (var cellBg = new SolidBrush(cell.BgColor))
                                    {
                                        g.FillRectangle(cellBg, x, y, w, h);
                                    }
                                }

                                // Cell text — vertically centered, left-padded.
                                textBrush.Color = cell.TextColor;
                                float tx = x + CELL_PADDING;
                                float ty = y + (h - cell.Size.Height) / 2f;
                                g.DrawString(cell.Text, font, textBrush, tx, ty);

                                // Cell border.
                                g.DrawRectangle(borderPen, x, y, w, h);

                                x += w;
                            }
                            y += rowHeight;
                        }

                        // Outer border once more for crisp edge.
                        g.DrawRectangle(borderPen, origin.X, origin.Y, totalWidth, totalHeight);
                    }
                }
                finally
                {
                    g.SmoothingMode    = prevSmoothing;
                    g.TextRenderingHint = prevTextRender;
                }
            }
        }

        // =====================================================================
        // Cell construction
        // =====================================================================

        private struct Cell
        {
            public string Text;
            public Color  TextColor;
            public Color  BgColor;     // alpha 0 means "no fill".
            public SizeF  Size;        // measured glyph size (no padding).
        }

        private static Cell[,] BuildCells(StripData d, Font font, Graphics g)
        {
            var cells = new Cell[COLS, ROWS];

            // ---- Fast path: lifecycle disabled --------------------------------
            if (!d.LifecycleOn)
            {
                FillCell(cells, 0, 0, "Lifecycle OFF", TXT_YELLOW, Color.Empty);
                FillCell(cells, 1, 0, "Enable in settings to track trades",
                         TXT_YELLOW, Color.Empty);
                for (int c = 2; c < COLS; c++) FillCell(cells, c, 0, "—", TXT_GRAY, Color.Empty);
                for (int r = 1; r < ROWS; r++)
                    for (int c = 0; c < COLS; c++)
                        FillCell(cells, c, r, "—", TXT_GRAY, Color.Empty);
                MeasureAll(cells, font, g);
                return cells;
            }

            // ---- Row 3 (running stats) is the same in both branches ----------
            BuildRow3RunningStats(cells, d);

            if (!d.HasActiveTrade)
            {
                // ---- No active trade: row 0 placeholder, rows 1-2 dashes -----
                BuildRow0NoTrade(cells, d);
                BuildRows1And2Dashes(cells);
            }
            else
            {
                // ---- Active trade: full live readout -------------------------
                BuildRow0Header(cells, d);
                BuildRow1Prices (cells, d);
                BuildRow2PnL    (cells, d);
            }

            MeasureAll(cells, font, g);
            return cells;
        }

        // ----- Row 0 (no trade) ----------------------------------------------
        private static void BuildRow0NoTrade(Cell[,] cells, StripData d)
        {
            // Lifecycle: ON | No live trade | — | — | — | Trend: ...
            // Trend feature: trend cell replaces the old "Hits: —" cell at col 5.
            FillCell(cells, 0, 0, "Lifecycle: ON", TXT_AQUA,  HEADER_BLUE_BG);
            FillCell(cells, 1, 0, "No live trade", TXT_WHITE, HEADER_GRAY_BG);
            FillCell(cells, 2, 0, "—",             TXT_GRAY,  Color.Empty);
            FillCell(cells, 3, 0, "—",             TXT_GRAY,  Color.Empty);
            FillCell(cells, 4, 0, "—",             TXT_GRAY,  Color.Empty);

            // Trend feature: render trend cell. Misalignment warn bg is suppressed
            // when no active trade (only meaningful for live trades).
            string trendText; Color trendFg; Color trendBg;
            BuildTrendCellContent(d, hasActiveTrade: false,
                                  out trendText, out trendFg, out trendBg);
            FillCell(cells, 5, 0, trendText, trendFg, trendBg);
        }

        // ----- Rows 1 and 2 (no trade) ---------------------------------------
        private static void BuildRows1And2Dashes(Cell[,] cells)
        {
            for (int r = 1; r <= 2; r++)
                for (int c = 0; c < COLS; c++)
                    FillCell(cells, c, r, "—", TXT_GRAY, Color.Empty);
        }

        // ----- Row 0 (active trade) ------------------------------------------
        private static void BuildRow0Header(Cell[,] cells, StripData d)
        {
            // Lifecycle: ON | Trade #N H/T | DirText | Cons: N | Risk: $N | Trend
            // Trend feature: replaced the old "Hits: H/T" cell at col 5 with a
            // trend cell. To avoid losing the hits readout we suffix Trade#N with
            // the H/T count (e.g. "Trade #7 1/3"), which keeps the strip at 6
            // columns (no width expansion needed).
            string hitsSuffix = " " + d.HitTps.ToString(CI) + "/" + d.TotTps.ToString(CI);
            string trIdStr    = "Trade #" + d.TradeId.ToString(CI) + hitsSuffix;
            string dirText    = string.IsNullOrEmpty(d.DirText) ? "—" : d.DirText;
            string consStr    = "Cons: "  + d.Contracts.ToString(CI);
            string riskStr    = "Risk: $" + d.DollarRisk.ToString("0", CI);

            // Direction color: lime for long, red for short. We treat the first
            // char of DirText (or IsLong flag) as direction marker.
            bool isLong = ResolveIsLong(d);
            Color dirColor = isLong ? TXT_LIME : TXT_RED;

            FillCell(cells, 0, 0, "Lifecycle: ON", TXT_AQUA,  HEADER_BLUE_BG);
            FillCell(cells, 1, 0, trIdStr,         TXT_WHITE, HEADER_GREEN_BG);
            FillCell(cells, 2, 0, dirText,         dirColor,  Color.Empty);
            FillCell(cells, 3, 0, consStr,         TXT_WHITE, Color.Empty);
            FillCell(cells, 4, 0, riskStr,         TXT_WHITE, Color.Empty);

            // Trend feature: trend cell with optional misalignment warning bg.
            string trendText; Color trendFg; Color trendBg;
            BuildTrendCellContent(d, hasActiveTrade: true,
                                  out trendText, out trendFg, out trendBg);
            FillCell(cells, 5, 0, trendText, trendFg, trendBg);
        }

        // Trend feature: produces (text, fg, bg) for the Row 0 col 5 trend cell.
        // - BULL: "BULL ↑" in light green
        // - BEAR: "BEAR ↓" in indian red
        // - FLAT: "FLAT —" in gray (default for null/empty/unknown TrendState)
        // - When hasActiveTrade && !TrendAligned, paints a subtle red warning bg.
        private static void BuildTrendCellContent(StripData d, bool hasActiveTrade,
                                                  out string text, out Color fg, out Color bg)
        {
            string state = d.TrendState;
            if (string.IsNullOrEmpty(state)) state = "FLAT";

            // Case-insensitive compare so master capitalization quirks don't
            // accidentally fall through to FLAT.
            if (state.Equals("BULL", StringComparison.OrdinalIgnoreCase))
            {
                text = "BULL ↑";
                fg   = TREND_BULL_FG;
            }
            else if (state.Equals("BEAR", StringComparison.OrdinalIgnoreCase))
            {
                text = "BEAR ↓";
                fg   = TREND_BEAR_FG;
            }
            else
            {
                text = "FLAT —";
                fg   = TREND_FLAT_FG;
            }

            // Misalignment warning only matters when a trade is live AND the
            // master computed an explicit direction mismatch. Suppressed for
            // FLAT trend (alignment is undefined when there's no trend).
            bool trendIsDirectional =
                state.Equals("BULL", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("BEAR", StringComparison.OrdinalIgnoreCase);
            if (hasActiveTrade && trendIsDirectional && !d.TrendAligned)
                bg = TREND_WARN_BG;
            else
                bg = Color.Empty;
        }

        // ----- Row 1 (active trade): price levels + NEXT trigger -------------
        private static void BuildRow1Prices(Cell[,] cells, StripData d)
        {
            // Entry, SL, TP1, TP2, TP3, NEXT
            string entryCell = "Entry: " + FmtPx(d.Entry);
            string slCell    = "SL: "    + FmtPx(d.CurSL);

            // TP cells: render up to TP3 here. Pine v1.4 only shows TP1..TP3 in
            // the strip. Hit TPs get a tick prefix and dimmed color.
            string tp1Text, tp2Text, tp3Text;
            Color  tp1Col,  tp2Col,  tp3Col;
            BuildTpCell(d, 0, out tp1Text, out tp1Col);
            BuildTpCell(d, 1, out tp2Text, out tp2Col);
            BuildTpCell(d, 2, out tp3Text, out tp3Col);

            string nextCell = "NEXT: " + (string.IsNullOrEmpty(d.NextTrigger) ? "—" : d.NextTrigger);

            FillCell(cells, 0, 1, entryCell, TXT_WHITE,  Color.Empty);
            FillCell(cells, 1, 1, slCell,    TXT_ORANGE, Color.Empty);
            FillCell(cells, 2, 1, tp1Text,   tp1Col,     Color.Empty);
            FillCell(cells, 3, 1, tp2Text,   tp2Col,     Color.Empty);
            FillCell(cells, 4, 1, tp3Text,   tp3Col,     Color.Empty);
            FillCell(cells, 5, 1, nextCell,  TXT_YELLOW, NEXT_BG);
        }

        // Builds one TP cell. Index is zero-based (0 -> TP1, etc.)
        private static void BuildTpCell(StripData d, int idx, out string text, out Color col)
        {
            int tpNum   = idx + 1;
            bool active = d.TotTps >= tpNum;
            if (!active || d.Tps == null || d.Tps.Length <= idx)
            {
                text = "—";
                col  = TXT_GRAY;
                return;
            }

            bool hit = d.TpHit != null && d.TpHit.Length > idx && d.TpHit[idx];
            // Pine wrote: "TPN " + price; "TPN✓ " + price if hit. Prefix tick
            // if hit. Dim if hit, bright cyan if pending.
            string label = "TP" + tpNum.ToString(CI) + (hit ? "✓ " : " ");
            text = label + FmtPx(d.Tps[idx]);
            col  = hit ? TXT_DIM_TP : TXT_TP_PEND;
        }

        // ----- Row 2 (active trade): LOCK / UNRL / FLOAT ---------------------
        private static void BuildRow2PnL(Cell[,] cells, StripData d)
        {
            // Compute slDist defensively. If StripData.SlDist is provided and >0,
            // honor it. Otherwise fall back to |Entry - OrigSL|.
            double slDist = d.SlDist > 0 ? d.SlDist : Math.Abs(d.Entry - d.OrigSL);
            bool isLong   = ResolveIsLong(d);

            bool   pnlValid;
            double lockR, unrR, floatR, lockD, unrD, floatD;

            if (slDist <= 0 || double.IsNaN(slDist) || double.IsInfinity(slDist))
            {
                pnlValid = false;
                lockR = unrR = floatR = lockD = unrD = floatD = double.NaN;
            }
            else
            {
                pnlValid = true;
                // LOCK = R already secured if curSL hits now.
                // Long:  (curSL - entry)/slDist     Short: (entry - curSL)/slDist
                lockR = isLong
                      ? (d.CurSL - d.Entry) / slDist
                      : (d.Entry - d.CurSL) / slDist;
                lockD = lockR * d.DollarRisk;

                // UNRL = mark-to-market R if we close at this instant.
                unrR  = isLong
                      ? (d.Close - d.Entry) / slDist
                      : (d.Entry - d.Close) / slDist;
                unrD  = unrR * d.DollarRisk;

                // FLT = give-back gap (>= 0 in healthy state).
                floatR = unrR - lockR;
                floatD = unrD - lockD;
            }

            // ---- Render strings ----
            string lockRstr, lockDstr, unrRstr, unrDstr, flRstr, flDstr;
            Color  lockCol,   unrCol,   flCol;

            if (!pnlValid)
            {
                lockRstr = lockDstr = unrRstr = unrDstr = flRstr = flDstr = "—";
                lockCol  = unrCol = flCol = TXT_GRAY;
            }
            else
            {
                lockRstr = "LOCK " + SignedR(lockR);
                lockDstr = SignedDollar(lockD);
                unrRstr  = "UNRL " + SignedR(unrR);
                unrDstr  = SignedDollar(unrD);
                flRstr   = "FLT "  + SignedR(floatR);
                flDstr   = SignedDollar(floatD);

                // LOCK / UNRL color: green if R >= 0 else red. FLT amber/yellow.
                lockCol  = lockR >= 0 ? TXT_LIME : TXT_RED;
                unrCol   = unrR  >= 0 ? TXT_LIME : TXT_RED;
                flCol    = TXT_YELLOW; // amber per spec.
            }

            // Background tint: faint dark for LOCK / UNRL pair so the green/red
            // text reads as a status badge. FLT cells stay clean.
            FillCell(cells, 0, 2, lockRstr, lockCol, LOCK_BG_TINT);
            FillCell(cells, 1, 2, lockDstr, lockCol, LOCK_BG_TINT);
            FillCell(cells, 2, 2, unrRstr,  unrCol,  LOCK_BG_TINT);
            FillCell(cells, 3, 2, unrDstr,  unrCol,  LOCK_BG_TINT);
            FillCell(cells, 4, 2, flRstr,   flCol,   Color.Empty);
            FillCell(cells, 5, 2, flDstr,   flCol,   Color.Empty);
        }

        // ----- Row 3 (running stats) — both branches share this -------------
        private static void BuildRow3RunningStats(Cell[,] cells, StripData d)
        {
            // W/L/BE | WR | TotalR | Total$ | AvgW | PF
            string wlbeStr   = "W:" + d.Wins.ToString(CI)
                             + " L:" + d.Losses.ToString(CI)
                             + " BE:" + d.BreakEvens.ToString(CI);

            string wrStr     = "WR:" + d.WinRate.ToString("0.0", CI) + "%";
            string totalRstr = (d.TotalR      >= 0 ? "+" : "")
                             + d.TotalR.ToString("0.00", CI) + "R";
            string totalDstr = (d.TotalDollar < 0  ? "-$" : "$")
                             + Math.Abs(d.TotalDollar).ToString("0.00", CI);
            string avgWstr   = "AvgW:" + (d.AvgWinR >= 0 ? "+" : "")
                             + d.AvgWinR.ToString("0.00", CI) + "R";
            string pfStr     = "PF:" + d.ProfitFactor.ToString("0.00", CI);

            FillCell(cells, 0, 3, wlbeStr,   TXT_WHITE, Color.Empty);
            FillCell(cells, 1, 3, wrStr,     TXT_AQUA,  Color.Empty);
            FillCell(cells, 2, 3, totalRstr, d.TotalR      >= 0 ? TXT_LIME : TXT_RED, Color.Empty);
            FillCell(cells, 3, 3, totalDstr, d.TotalDollar >= 0 ? TXT_LIME : TXT_RED, Color.Empty);
            FillCell(cells, 4, 3, avgWstr,   TXT_LIME,  Color.Empty);
            FillCell(cells, 5, 3, pfStr,     TXT_WHITE, Color.Empty);
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static void FillCell(Cell[,] cells, int c, int r, string text, Color fg, Color bg)
        {
            cells[c, r] = new Cell { Text = text ?? "", TextColor = fg, BgColor = bg };
        }

        // Measure each cell's text in the chosen font. Done once after all cells
        // are populated so we avoid measuring incrementally.
        private static void MeasureAll(Cell[,] cells, Font font, Graphics g)
        {
            for (int r = 0; r < ROWS; r++)
            {
                for (int c = 0; c < COLS; c++)
                {
                    string txt = cells[c, r].Text ?? "";
                    SizeF sz = g.MeasureString(txt, font);
                    Cell cell = cells[c, r];
                    cell.Size = sz;
                    cells[c, r] = cell;
                }
            }
        }

        // Build a monospaced font, falling back to Courier New if Consolas is
        // not installed. Disposed by the caller via the using() block.
        private static Font BuildMonoFont(float ptSize)
        {
            try
            {
                var f = new Font("Consolas", ptSize, FontStyle.Regular, GraphicsUnit.Point);
                if (f.Name.Equals("Consolas", StringComparison.OrdinalIgnoreCase))
                    return f;
                f.Dispose();
            }
            catch
            {
                // fall through
            }
            return new Font("Courier New", ptSize, FontStyle.Regular, GraphicsUnit.Point);
        }

        private static float ResolvePtSize(int textSize)
        {
            // 1=Tiny .. 5=Huge as per spec. Out-of-range clamps to Medium.
            switch (textSize)
            {
                case 1: return  8f;
                case 2: return 10f;
                case 3: return 12f;
                case 4: return 14f;
                case 5: return 16f;
                default: return 12f;
            }
        }

        // Position math: anchor strip top-left to the requested corner / edge of
        // the chartArea, applying EDGE_MARGIN on all sides. Center variants
        // split remaining width/height in half.
        private static PointF ResolveOrigin(Rectangle chartArea, StripPosition pos,
                                            float w, float h)
        {
            float left   = chartArea.Left   + EDGE_MARGIN;
            float right  = chartArea.Right  - EDGE_MARGIN - w;
            float top    = chartArea.Top    + EDGE_MARGIN;
            float bottom = chartArea.Bottom - EDGE_MARGIN - h;
            float midX   = chartArea.Left + (chartArea.Width  - w) / 2f;
            float midY   = chartArea.Top  + (chartArea.Height - h) / 2f;

            // Clamp so we never push the strip off the left/top edge if the
            // chart is narrower than the strip.
            if (right  < left)   right  = left;
            if (bottom < top)    bottom = top;

            switch (pos)
            {
                case StripPosition.TopLeft:      return new PointF(left,  top);
                case StripPosition.TopCenter:    return new PointF(midX,  top);
                case StripPosition.TopRight:     return new PointF(right, top);
                case StripPosition.MiddleLeft:   return new PointF(left,  midY);
                case StripPosition.MiddleCenter: return new PointF(midX,  midY);
                case StripPosition.MiddleRight:  return new PointF(right, midY);
                case StripPosition.BottomLeft:   return new PointF(left,  bottom);
                case StripPosition.BottomCenter: return new PointF(midX,  bottom);
                case StripPosition.BottomRight:  return new PointF(right, bottom);
                default:                         return new PointF(left,  top);
            }
        }

        // Format an R value with explicit sign and 2 decimals.
        private static string SignedR(double r)
        {
            string sign = r >= 0 ? "+" : "";
            return sign + r.ToString("0.00", CI) + "R";
        }

        // Format a $ value as "$N.NN" or "-$N.NN" (sign before $, magnitude after).
        private static string SignedDollar(double d)
        {
            if (d < 0) return "-$" + Math.Abs(d).ToString("0.00", CI);
            return "$" + d.ToString("0.00", CI);
        }

        // Format a price. We do not have access to the symbol's mintick from
        // here, so we render with 2 decimals which is correct for ES/NQ/MES/MNQ.
        // Indicators that need tick-precision can pre-format and stuff into a
        // string field; in this minimal contract we keep it simple.
        private static string FmtPx(double p)
        {
            if (double.IsNaN(p) || double.IsInfinity(p)) return "—";
            return p.ToString("0.00", CI);
        }

        // Resolve "is long" preferring the explicit IsLong flag, falling back
        // to the first character of DirText ("L" -> long).
        private static bool ResolveIsLong(StripData d)
        {
            if (!string.IsNullOrEmpty(d.DirText))
            {
                char c = char.ToUpperInvariant(d.DirText[0]);
                if (c == 'L') return true;
                if (c == 'S') return false;
            }
            return d.IsLong;
        }
    }
}
