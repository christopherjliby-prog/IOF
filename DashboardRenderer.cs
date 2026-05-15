// =============================================================================
// DashboardRenderer.cs — TradePhantoms IOF QuickEntry v2.0 (Quantower port)
// -----------------------------------------------------------------------------
// Self-contained static helper for rendering the Closed Trades Dashboard table
// AND the Status Dashboard panel for the Quantower port of the Pine v1.4
// "TradePhantoms IOF QuickEntry" indicator.
//
// Modeled on Pine v1.4 source (message (4).txt lines 1700-2085) and the Pine
// IOF Project Audit (Section 8). Drawing API is GDI+ via OnPaintChart.
//
// Public surface:
//   TradePhantomsIOF.UI.DashboardRenderer.DrawClosedTradesTable(...)
//   TradePhantomsIOF.UI.DashboardRenderer.DrawStatusDashboard(...)
//
// All drawing is performed inside OnPaintChart of the host indicator. The
// methods are pure with respect to the input rows / settings — no static state.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace TradePhantomsIOF.UI
{
    // ── Public data types ────────────────────────────────────────────────────

    /// <summary>One row in the Closed Trades table. Caller passes newest-first.</summary>
    public class ClosedTradeRow
    {
        public int TradeNum;
        public string Tier;        // "LTF" / "ITF" / "HTF"
        public string Dir;         // "L" / "S"
        public string Outcome;     // "WIN" / "LOSS" / "BE"
        public int HitTps;
        public int TotTps;
        public string ExitReason;  // "SL", "TP1".."TP5", "TRL0".."TRL5", "BE"
        public double R;
        public double DollarPnL;
        public DateTime CloseTime;
    }

    /// <summary>One ARMED or ACTIVE trade in the Status Dashboard's LIVE TRADES section.</summary>
    public class ActiveTradeRow
    {
        public int TradeNum;
        public string State;       // "ARMED" / "ACTIVE"
        public string Dir;         // "L H" (long, HTF), "S L" etc.
        public int Contracts;
        public double DollarRisk;
        public double Entry;
        public double CurSL;
    }

    /// <summary>One eligible-zone row for the NEXT TRADES panel — a preview of
    /// the closest tradeable setups (entry/SL/3R/qty) sorted by proximity to
    /// current price. Built from non-invalidated zones that meet MinScore and
    /// have a structural R:R ≥ 3 — i.e. setups the trader could actually take.</summary>
    public class NextTradeRow
    {
        public bool IsLong;            // true → Demand (long), false → Supply (short)
        public double Entry;
        public double Sl;
        public double Target3R;        // entry ± 3 × |entry − sl| (long: +, short: −)
        public int Contracts;
        public double DistPts;         // signed: long-side is negative (price below entry pulls back UP to fill); short-side positive
    }

    /// <summary>Settings block displayed in the Status Dashboard SETTINGS section.</summary>
    public class DashboardSettings
    {
        public string IndicatorVersion = "IOF QuickEntry v2.0";
        public string Ticker = "";
        public string Timeframe = "";
        public bool LifecycleOn;
        public string SLStrategyName = "Cascade";
        public bool SeqGate;
        public double RiskPerTrade;
        public int MaxContracts;
        public int TpCount;
        public double TpStep;
        public double ArmProx;
        public int MaxPlans;
        public string OFEntry = "0%";
        public int SLBufferTicks;
    }

    /// <summary>Per-tier zone counts and nearest demand/supply prices.</summary>
    public class DashboardZones
    {
        public int LtfDemandCount, LtfSupplyCount;
        public int ItfDemandCount, ItfSupplyCount;
        public int HtfDemandCount, HtfSupplyCount;
        public double NearestDemandPrice = double.NaN;
        public double NearestSupplyPrice = double.NaN;
        // Optional per-tier nearest D/S — if NaN, the renderer falls back to the
        // single global NearestDemandPrice / NearestSupplyPrice for all rows.
        public double LtfNearestDemand = double.NaN, LtfNearestSupply = double.NaN;
        public double ItfNearestDemand = double.NaN, ItfNearestSupply = double.NaN;
        public double HtfNearestDemand = double.NaN, HtfNearestSupply = double.NaN;
    }

    /// <summary>Trend-state snapshot displayed in the Status Dashboard TREND section.</summary>
    public class DashboardTrend
    {
        public string CurrentState = "FLAT";          // "FLAT" / "BULL" / "BEAR"
        public DateTime LastStateChange;              // when current state was entered
        public double ControllingPivotPrice;          // NaN if FLAT
        public DateTime LastControlPointTime;
        public double LastControlPointPrice;
        public string LastBreakReason;                // human-readable, e.g. "HL 5295.00 broken at 11:32"
        public int RecentControlPointCount;           // last hour
        public int AlignedZoneCount;                  // chart zones aligned with current trend
        public int OpposedZoneCount;                  // chart zones opposed to current trend
    }

    /// <summary>Aggregate stats for the Status Dashboard STATS section.</summary>
    public class DashboardStats
    {
        public int Wins, Losses, BreakEvens;
        public double WinRate;
        public double ProfitFactor;
        public double TotalR;
        public double TotalDollar;
        public double AvgWinR, AvgLossR;
        public int MaxWinStreak, MaxLossStreak;
    }

    /// <summary>Anchor corner for both panels. Maps to Pine's table.position_*.</summary>
    public enum PanelPosition
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        MiddleLeft,
        MiddleRight,
    }

    // ── Renderer ─────────────────────────────────────────────────────────────

    public static class DashboardRenderer
    {
        // Padding constants (px). Cell padding sits inside each cell; section
        // padding is the gap between two adjacent sections (e.g. SETTINGS vs ZONES).
        private const int CELL_PAD_X = 4;
        private const int CELL_PAD_Y = 2;
        private const int SECTION_PAD_Y = 6;
        private const int PANEL_PAD = 8;
        private const int PANEL_BORDER = 1;
        private const int PANEL_MARGIN = 12;   // gap between chart edge and panel
        private const int MIN_PANEL_WIDTH = 200;
        private const int MAX_PANEL_WIDTH = 600;

        // Colors — modeled on Pine palette (color.lime / color.red / color.aqua / etc.)
        private static readonly Color BG_PANEL        = Color.FromArgb(200, 20, 20, 30);
        private static readonly Color BORDER_PANEL    = Color.FromArgb(200, 128, 128, 128);
        private static readonly Color HEADER_BG       = Color.FromArgb(140, 0, 64, 192);     // blue accent
        private static readonly Color SECTION_BG_GRAY = Color.FromArgb(140, 90, 90, 95);
        private static readonly Color SECTION_BG_TEAL = Color.FromArgb(140, 0, 128, 128);
        private static readonly Color SECTION_BG_OLIV = Color.FromArgb(140, 128, 128, 0);
        private static readonly Color SECTION_BG_PURP = Color.FromArgb(140, 128, 0, 128);

        private static readonly Color FG_WHITE        = Color.WhiteSmoke;
        private static readonly Color FG_GRAY         = Color.FromArgb(255, 170, 170, 170);
        private static readonly Color FG_LIME         = Color.FromArgb(255, 60, 220, 90);
        private static readonly Color FG_LIGHT_LIME   = Color.FromArgb(255, 140, 230, 150);
        private static readonly Color FG_RED          = Color.FromArgb(255, 235, 80, 80);
        private static readonly Color FG_LIGHT_RED    = Color.FromArgb(255, 235, 150, 150);
        private static readonly Color FG_AQUA         = Color.FromArgb(255, 90, 220, 230);
        private static readonly Color FG_AMBER        = Color.FromArgb(255, 245, 170, 50);
        private static readonly Color FG_YELLOW       = Color.FromArgb(255, 240, 220, 60);
        private static readonly Color FG_ORANGE       = Color.FromArgb(255, 240, 140, 40);

        // ── Public entry — Closed Trades Dashboard ───────────────────────────

        /// <summary>
        /// Render the Closed Trades table. <paramref name="rows"/> must be in
        /// newest-first order (BUG F prevention — caller has already reversed).
        /// </summary>
        public static void DrawClosedTradesTable(
            Graphics g,
            Rectangle chartArea,
            IList<ClosedTradeRow> rows,
            int maxRows,
            PanelPosition position,
            int textSize)
        {
            if (g == null || chartArea.Width <= 0 || chartArea.Height <= 0)
                return;

            if (maxRows <= 0) maxRows = 21;
            int fontPt = MapTextSize(textSize);

            using (var font = new Font("Consolas", fontPt, FontStyle.Regular, GraphicsUnit.Point))
            using (var fontBold = new Font("Consolas", fontPt, FontStyle.Bold, GraphicsUnit.Point))
            {
                // Column titles (8 columns: # | Tier | Dir | Outcome | Hit | Exit | R | $)
                string[] headers = { "#", "Tier", "Dir", "Out", "Hit", "Exit", "R", "$" };

                // ── Compute column widths from header text + actual data ─────
                int[] widths = new int[headers.Length];
                for (int i = 0; i < headers.Length; i++)
                    widths[i] = (int)Math.Ceiling(g.MeasureString(headers[i], fontBold).Width) + CELL_PAD_X * 2;

                int dataRows = (rows == null) ? 0 : Math.Min(rows.Count, maxRows);
                for (int r = 0; r < dataRows; r++)
                {
                    var row = rows[r];
                    if (row == null) continue;
                    string[] cells = BuildClosedRowCells(row);
                    for (int c = 0; c < cells.Length && c < widths.Length; c++)
                    {
                        int w = (int)Math.Ceiling(g.MeasureString(cells[c], font).Width) + CELL_PAD_X * 2;
                        if (w > widths[c]) widths[c] = w;
                    }
                }

                int contentW = 0;
                for (int i = 0; i < widths.Length; i++) contentW += widths[i];

                int totalRows = 1 /*title*/ + 1 /*header*/ + Math.Max(1, dataRows);
                int rowH = (int)Math.Ceiling(g.MeasureString("Hg", fontBold).Height) + CELL_PAD_Y * 2;
                int contentH = totalRows * rowH;

                int panelW = ClampPanelWidth(contentW + PANEL_PAD * 2);
                int panelH = contentH + PANEL_PAD * 2;

                Point origin = ResolvePanelOrigin(chartArea, position, panelW, panelH);
                var panelRect = new Rectangle(origin.X, origin.Y, panelW, panelH);

                // ── Draw panel background + border ───────────────────────────
                DrawPanelBackground(g, panelRect);

                int x0 = panelRect.X + PANEL_PAD;
                int y  = panelRect.Y + PANEL_PAD;

                // ── Title row ────────────────────────────────────────────────
                var titleRect = new Rectangle(x0, y, contentW, rowH);
                FillCell(g, titleRect, HEADER_BG);
                DrawCellText(g, titleRect, "Closed Trades", FG_WHITE, fontBold, StringAlignment.Center);
                y += rowH;

                // ── Column header row ────────────────────────────────────────
                int cx = x0;
                for (int i = 0; i < headers.Length; i++)
                {
                    var hRect = new Rectangle(cx, y, widths[i], rowH);
                    FillCell(g, hRect, SECTION_BG_PURP);
                    DrawCellText(g, hRect, headers[i], FG_WHITE, fontBold, StringAlignment.Center);
                    cx += widths[i];
                }
                y += rowH;

                // Separator under header
                using (var sepPen = new Pen(BORDER_PANEL, 1f))
                    g.DrawLine(sepPen, x0, y, x0 + contentW, y);

                // ── Data rows ────────────────────────────────────────────────
                if (dataRows == 0)
                {
                    // empty marker row
                    var emptyRect = new Rectangle(x0, y, contentW, rowH);
                    DrawCellText(g, emptyRect, "(no closed trades yet)", FG_GRAY, font, StringAlignment.Center);
                }
                else
                {
                    // Iterate in caller order = newest first. Index 0 = most recent.
                    for (int r = 0; r < dataRows; r++)
                    {
                        var row = rows[r];
                        if (row == null) continue;
                        string[] cells = BuildClosedRowCells(row);

                        Color outcomeC = OutcomeColor(row.Outcome);
                        Color rC       = RColor(row.R);
                        Color exitC    = ExitColor(row.ExitReason, row.R);
                        Color dirC     = (row.Dir != null && row.Dir.StartsWith("S")) ? FG_RED : FG_LIME;
                        Color hitC     = HitColor(row.HitTps, row.TotTps);
                        Color dollarC  = (row.DollarPnL >= 0) ? FG_LIME : FG_RED;

                        Color[] colors =
                        {
                            FG_WHITE, FG_WHITE, dirC, outcomeC, hitC, exitC, rC, dollarC,
                        };

                        cx = x0;
                        for (int c = 0; c < cells.Length && c < widths.Length; c++)
                        {
                            var cellRect = new Rectangle(cx, y, widths[c], rowH);
                            // No fill — keep transparent, color text only
                            DrawCellText(g, cellRect, cells[c], colors[c], font,
                                c == 0 ? StringAlignment.Near : StringAlignment.Center);
                            cx += widths[c];
                        }
                        y += rowH;
                    }
                }
            }
        }

        // ── Public entry — Next Trades preview panel ─────────────────────────

        /// <summary>
        /// Render the NEXT TRADES preview panel — a standalone compact table
        /// showing the closest tradeable zones to current price (entry, SL,
        /// 3R target, contract count). Rows already filtered + sorted by the
        /// caller; this method just draws them.
        ///
        /// Designed to sit in a corner opposite the main Status Dashboard.
        /// Renders nothing (panel suppressed) when <paramref name="rows"/> is
        /// null or empty.
        /// </summary>
        public static void DrawNextTradesPanel(
            Graphics g,
            Rectangle chartArea,
            IList<NextTradeRow> rows,
            double currentPrice,
            PanelPosition position,
            int textSize)
        {
            if (g == null || chartArea.Width <= 0 || chartArea.Height <= 0) return;
            if (rows == null || rows.Count == 0) return;

            int fontPt = MapTextSize(textSize);

            using (var font     = new Font("Consolas", fontPt,     FontStyle.Regular, GraphicsUnit.Point))
            using (var fontBold = new Font("Consolas", fontPt,     FontStyle.Bold,    GraphicsUnit.Point))
            using (var fontHdr  = new Font("Consolas", fontPt + 1, FontStyle.Bold,    GraphicsUnit.Point))
            {
                // 5-column grid: Dir | Entry | SL | 3R | Qty
                const int COLS = 5;

                int rowH    = (int)Math.Ceiling(g.MeasureString("Hg", fontBold).Height) + CELL_PAD_Y * 2;
                int rowHdrH = (int)Math.Ceiling(g.MeasureString("Hg", fontHdr ).Height) + CELL_PAD_Y * 2;

                // Title — caller-supplied current price helps the trader orient.
                string title = "NEXT TRADES  @ "
                             + currentPrice.ToString("0.##", CultureInfo.InvariantCulture);
                string[] colHeader = { "Dir", "Entry", "SL", "3R", "Qty" };

                var dataCells = new List<string[]>(rows.Count);
                var dataColors = new List<Color[]>(rows.Count);
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    string arrow = r.IsLong ? "L ▲" : "S ▼";
                    Color dirC = r.IsLong ? FG_LIME : FG_RED;
                    dataCells.Add(new[]
                    {
                        arrow,
                        r.Entry.ToString("0.##", CultureInfo.InvariantCulture),
                        r.Sl.ToString("0.##", CultureInfo.InvariantCulture),
                        r.Target3R.ToString("0.##", CultureInfo.InvariantCulture),
                        r.Contracts.ToString(CultureInfo.InvariantCulture),
                    });
                    dataColors.Add(new[] { dirC, FG_WHITE, FG_RED, FG_LIME, FG_WHITE });
                }

                // Column widths — measure header + every data row.
                int[] widths = new int[COLS];
                int titleW = (int)Math.Ceiling(g.MeasureString(title, fontHdr).Width) + CELL_PAD_X * 2;
                MeasureRow(g, fontBold, colHeader, widths);
                foreach (var cells in dataCells) MeasureRow(g, font, cells, widths);
                for (int i = 0; i < COLS; i++)
                    if (widths[i] < 50) widths[i] = 50;

                int contentW = 0;
                for (int i = 0; i < COLS; i++) contentW += widths[i];
                if (contentW < titleW)
                {
                    // Spread the title shortfall across all columns so the
                    // grid stays proportionally balanced. (Stretching only the
                    // last column made it visually lopsided.)
                    int shortfall = titleW - contentW;
                    int per = shortfall / COLS;
                    int rem = shortfall - per * COLS;
                    for (int i = 0; i < COLS; i++) widths[i] += per;
                    widths[COLS - 1] += rem;
                    contentW = titleW;
                }

                int contentH = rowHdrH                  // title
                             + rowH                     // column header
                             + rows.Count * rowH;       // data rows

                int panelW = ClampPanelWidth(contentW + PANEL_PAD * 2);
                int panelH = contentH + PANEL_PAD * 2;

                Point origin = ResolvePanelOrigin(chartArea, position, panelW, panelH);
                var panelRect = new Rectangle(origin.X, origin.Y, panelW, panelH);
                DrawPanelBackground(g, panelRect);

                int x0 = panelRect.X + PANEL_PAD;
                int y  = panelRect.Y + PANEL_PAD;

                // Title bar — single cell spanning the full grid width so the
                // title text isn't clipped to column 0. Draw directly: blue
                // accent bg, centered white text.
                int titleRowW = 0;
                for (int i = 0; i < COLS; i++) titleRowW += widths[i];
                var titleRect = new Rectangle(x0, y, titleRowW, rowHdrH);
                FillCell(g, titleRect, HEADER_BG);
                DrawCellText(g, titleRect, title, FG_WHITE, fontHdr, StringAlignment.Center);
                y += rowHdrH;

                // Column header row — teal accent like other section bars.
                var hdrColors = new Color[COLS];
                for (int i = 0; i < COLS; i++) hdrColors[i] = FG_WHITE;
                DrawGridRow(g, x0, y, widths, colHeader, hdrColors, fontBold, SECTION_BG_TEAL);
                y += rowH;

                // Data rows — alternating bg would be nice but Pine parity says flat.
                for (int i = 0; i < dataCells.Count; i++)
                {
                    DrawGridRow(g, x0, y, widths, dataCells[i], dataColors[i], font, Color.Empty);
                    y += rowH;
                }
            }
        }

        // ── Public entry — Status Dashboard ──────────────────────────────────

        /// <summary>
        /// Render the comprehensive Status Dashboard panel: header, settings,
        /// zones, live trades, stats. Each section can handle null/empty inputs.
        /// </summary>
        public static void DrawStatusDashboard(
            Graphics g,
            Rectangle chartArea,
            DashboardSettings settings,
            DashboardZones zones,
            IList<ActiveTradeRow> activeTrades,
            DashboardStats stats,
            PanelPosition position,
            int textSize,
            DashboardTrend trend = null)
        {
            if (g == null || chartArea.Width <= 0 || chartArea.Height <= 0)
                return;

            if (settings == null) settings = new DashboardSettings();
            if (zones    == null) zones    = new DashboardZones();
            if (stats    == null) stats    = new DashboardStats();

            int fontPt = MapTextSize(textSize);

            using (var font     = new Font("Consolas",   fontPt,     FontStyle.Regular, GraphicsUnit.Point))
            using (var fontBold = new Font("Consolas",   fontPt,     FontStyle.Bold,    GraphicsUnit.Point))
            using (var fontHdr  = new Font("Consolas",   fontPt + 1, FontStyle.Bold,    GraphicsUnit.Point))
            {
                int rowH    = (int)Math.Ceiling(g.MeasureString("Hg", fontBold).Height) + CELL_PAD_Y * 2;
                int rowHdrH = (int)Math.Ceiling(g.MeasureString("Hg", fontHdr ).Height) + CELL_PAD_Y * 2;

                // ── Build all section data ───────────────────────────────────
                var headerCells = BuildHeaderCells(settings);
                var settingsRows = BuildSettingsRows(settings);
                // TREND section is optional — caller passes null to skip it entirely.
                var trendRows = (trend == null) ? null : BuildTrendRows(trend);
                var zoneRows = BuildZoneRows(zones);
                var activeRows = BuildActiveTradeRows(activeTrades, settings.LifecycleOn);
                var statsRows = BuildStatsRows(stats, settings.LifecycleOn);

                // ── Compute column widths for the 4-column grid ──────────────
                // Pine v1.4 uses a 4-column layout. We allow each content row up
                // to 4 strings; non-content (single-cell section bars) span all 4.
                int[] widths = new int[4];
                MeasureRow(g, fontHdr,  headerCells, widths);
                foreach (var r in settingsRows) MeasureRow(g, font, r.Cells, widths);
                if (trendRows != null)
                    foreach (var r in trendRows) MeasureRow(g, font, r.Cells, widths);
                foreach (var r in zoneRows)     MeasureRow(g, font, r.Cells, widths);
                foreach (var r in activeRows)   MeasureRow(g, font, r.Cells, widths);
                foreach (var r in statsRows)    MeasureRow(g, font, r.Cells, widths);

                // Floor each column to a reasonable minimum so single-cell labels
                // (e.g. "ZONES" header) still get a sane stretch.
                for (int i = 0; i < widths.Length; i++)
                    if (widths[i] < 60) widths[i] = 60;

                int contentW = 0;
                for (int i = 0; i < widths.Length; i++) contentW += widths[i];

                // ── Row layout: header (1) + settings (N) + sectionPad
                //   + ZONES (1 + 3) + sectionPad
                //   + LIVE TRADES (1 header + N rows) + sectionPad
                //   + STATS (5 rows). Lifecycle OFF → STATS is replaced by notice.
                int contentH = rowHdrH                                 // header
                             + settingsRows.Count * rowH
                             + SECTION_PAD_Y
                             + (trendRows != null ? trendRows.Count * rowH + SECTION_PAD_Y : 0)
                             + zoneRows.Count * rowH
                             + SECTION_PAD_Y
                             + activeRows.Count * rowH
                             + SECTION_PAD_Y
                             + statsRows.Count * rowH;

                int panelW = ClampPanelWidth(contentW + PANEL_PAD * 2);
                int panelH = contentH + PANEL_PAD * 2;

                Point origin = ResolvePanelOrigin(chartArea, position, panelW, panelH);
                var panelRect = new Rectangle(origin.X, origin.Y, panelW, panelH);
                DrawPanelBackground(g, panelRect);

                int x0 = panelRect.X + PANEL_PAD;
                int y  = panelRect.Y + PANEL_PAD;

                // ── HEADER (4 cells, fontHdr, blue accent bg) ────────────────
                Color hdrLifecycleC = settings.LifecycleOn ? FG_LIME : FG_GRAY;
                Color[] hdrColors = { FG_WHITE, FG_WHITE, FG_WHITE, hdrLifecycleC };
                DrawGridRow(g, x0, y, widths, headerCells, hdrColors, fontHdr, HEADER_BG);
                y += rowHdrH;

                // ── SETTINGS section ─────────────────────────────────────────
                foreach (var r in settingsRows)
                {
                    DrawGridRow(g, x0, y, widths, r.Cells, r.Colors, font, r.RowBg);
                    y += rowH;
                }
                y += SECTION_PAD_Y;

                // ── TREND section (optional — skipped when trend == null) ────
                if (trendRows != null)
                {
                    foreach (var r in trendRows)
                    {
                        DrawGridRow(g, x0, y, widths, r.Cells, r.Colors, font, r.RowBg);
                        y += rowH;
                    }
                    y += SECTION_PAD_Y;
                }

                // ── ZONES section ────────────────────────────────────────────
                foreach (var r in zoneRows)
                {
                    DrawGridRow(g, x0, y, widths, r.Cells, r.Colors, font, r.RowBg);
                    y += rowH;
                }
                y += SECTION_PAD_Y;

                // ── LIVE TRADES section ──────────────────────────────────────
                foreach (var r in activeRows)
                {
                    DrawGridRow(g, x0, y, widths, r.Cells, r.Colors, font, r.RowBg);
                    y += rowH;
                }
                y += SECTION_PAD_Y;

                // ── STATS section ────────────────────────────────────────────
                foreach (var r in statsRows)
                {
                    DrawGridRow(g, x0, y, widths, r.Cells, r.Colors, font, r.RowBg);
                    y += rowH;
                }
            }
        }

        // ── Closed-trade cell builders ───────────────────────────────────────

        private static string[] BuildClosedRowCells(ClosedTradeRow row)
        {
            string idStr      = "#" + row.TradeNum.ToString(CultureInfo.InvariantCulture);
            string tierLbl    = AbbrevTier(row.Tier);
            string dirLbl     = row.Dir ?? "";
            string outcomeLbl = row.Outcome ?? "";
            string hitStr     = row.HitTps.ToString(CultureInfo.InvariantCulture)
                              + "/" + row.TotTps.ToString(CultureInfo.InvariantCulture);
            string exitStr    = row.ExitReason ?? "";
            string rStr       = (row.R >= 0 ? "+" : "")
                              + row.R.ToString("0.00", CultureInfo.InvariantCulture) + "R";
            string dStr       = (row.DollarPnL < 0 ? "-$" : "$")
                              + Math.Abs(row.DollarPnL).ToString("0.00", CultureInfo.InvariantCulture);
            return new[] { idStr, tierLbl, dirLbl, outcomeLbl, hitStr, exitStr, rStr, dStr };
        }

        private static string AbbrevTier(string tier)
        {
            if (string.IsNullOrEmpty(tier)) return "?";
            // Pine prints "L"/"I"/"H"; we accept either spelling.
            switch (tier.ToUpperInvariant())
            {
                case "LTF": case "L": return "L";
                case "ITF": case "I": return "I";
                case "HTF": case "H": return "H";
                default: return tier;
            }
        }

        private static Color OutcomeColor(string outcome)
        {
            if (string.IsNullOrEmpty(outcome)) return FG_GRAY;
            switch (outcome.ToUpperInvariant())
            {
                case "WIN":  return FG_LIME;
                case "LOSS": return FG_RED;
                case "BE":   return FG_GRAY;
                default:     return FG_WHITE;
            }
        }

        private static Color RColor(double r)
        {
            if (r > 0) return FG_LIME;
            if (r < 0) return FG_RED;
            return FG_GRAY;
        }

        private static Color HitColor(int hit, int tot)
        {
            if (tot > 0 && hit >= tot) return FG_LIME;
            if (hit > 0)               return FG_AQUA;
            return FG_GRAY;
        }

        /// <summary>
        /// Exit-reason color mapping per the spec:
        ///   SL              → red
        ///   TP1..TP5        → green
        ///   TRL0            → amber  (pre-TP1 trail-out, distinct from SL)
        ///   TRL1..TRL5      → light green if R≥0 else light red
        ///   TRND / TBR      → amber  (trend-broken discretionary close)
        ///   BE              → gray
        /// </summary>
        private static Color ExitColor(string exit, double r)
        {
            if (string.IsNullOrEmpty(exit)) return FG_WHITE;
            string e = exit.ToUpperInvariant();

            if (e == "SL") return FG_RED;
            if (e == "BE") return FG_GRAY;

            // Trend-broken discretionary close — accept either short label until
            // TradeLifecycle.cs settles on one. Both render as amber/yellow since
            // it's a discretionary exit, not a clean SL or TP.
            if (e == "TRND" || e == "TBR") return FG_AMBER;

            if (e.StartsWith("TRL"))
            {
                // TRL0 = pre-TP1 trail-out (amber). TRL1..5 take light green / light red.
                if (e == "TRL0") return FG_AMBER;
                return r >= 0 ? FG_LIGHT_LIME : FG_LIGHT_RED;
            }

            if (e.StartsWith("TP")) return FG_LIME;

            return FG_WHITE;
        }

        // ── Status dashboard: row builders ───────────────────────────────────

        /// <summary>One logical row in the 4-column status grid.</summary>
        private struct StatusRow
        {
            public string[] Cells;
            public Color[]  Colors;
            public Color    RowBg;     // Color.Empty == no background fill
        }

        private static string[] BuildHeaderCells(DashboardSettings s)
        {
            string lc = s.LifecycleOn ? "Lifecycle ON" : "Lifecycle OFF";
            return new[]
            {
                s.IndicatorVersion ?? "IOF QuickEntry v2.0",
                s.Ticker ?? "",
                s.Timeframe ?? "",
                lc,
            };
        }

        private static List<StatusRow> BuildSettingsRows(DashboardSettings s)
        {
            var list = new List<StatusRow>();

            // Section header row (single label spread across cell 0; cells 1-3 are filler bg).
            list.Add(new StatusRow
            {
                Cells  = new[] { "SETTINGS", "", "", "" },
                Colors = new[] { FG_WHITE, FG_WHITE, FG_WHITE, FG_WHITE },
                RowBg  = SECTION_BG_GRAY,
            });

            // SL Strategy | Seq Gate
            list.Add(new StatusRow
            {
                Cells = new[]
                {
                    "SL Strategy",
                    s.SLStrategyName ?? "—",
                    "Seq Gate",
                    s.SeqGate ? "ON" : "OFF",
                },
                Colors = new[] { FG_WHITE, FG_AQUA, FG_WHITE, s.SeqGate ? FG_LIME : FG_GRAY },
                RowBg  = Color.Empty,
            });

            // Risk/trade | Max cts
            list.Add(new StatusRow
            {
                Cells = new[]
                {
                    "Risk/trade",
                    "$" + s.RiskPerTrade.ToString("0", CultureInfo.InvariantCulture),
                    "Max cts",
                    s.MaxContracts.ToString(CultureInfo.InvariantCulture),
                },
                Colors = new[] { FG_WHITE, FG_WHITE, FG_WHITE, FG_WHITE },
                RowBg  = Color.Empty,
            });

            // TP count | TP step
            list.Add(new StatusRow
            {
                Cells = new[]
                {
                    "TP count",
                    s.TpCount.ToString(CultureInfo.InvariantCulture),
                    "TP step",
                    s.TpStep.ToString("0.0", CultureInfo.InvariantCulture) + "x",
                },
                Colors = new[] { FG_WHITE, FG_WHITE, FG_WHITE, FG_WHITE },
                RowBg  = Color.Empty,
            });

            // Arm prox | Max plans
            list.Add(new StatusRow
            {
                Cells = new[]
                {
                    "Arm prox",
                    s.ArmProx.ToString("0.0", CultureInfo.InvariantCulture) + "x zH",
                    "Max plans",
                    s.MaxPlans.ToString(CultureInfo.InvariantCulture),
                },
                Colors = new[] { FG_WHITE, FG_WHITE, FG_WHITE, FG_WHITE },
                RowBg  = Color.Empty,
            });

            // OF Entry | SL buffer.
            // Pine highlights OF Entry in aqua iff the configured OF % is > 0.
            // We treat "0%", "0", or empty as zero; any non-zero leading digit
            // (or a leading sign + non-zero digit) lights up the cell.
            bool ofPositive = false;
            if (!string.IsNullOrEmpty(s.OFEntry))
            {
                string trimmed = s.OFEntry.TrimStart('+', '-', ' ');
                if (trimmed.Length > 0 && trimmed[0] != '0')
                    ofPositive = true;
            }
            list.Add(new StatusRow
            {
                Cells = new[]
                {
                    "OF Entry",
                    s.OFEntry ?? "0%",
                    "SL buffer",
                    s.SLBufferTicks.ToString(CultureInfo.InvariantCulture) + "t",
                },
                Colors = new[] { FG_WHITE, ofPositive ? FG_AQUA : FG_WHITE, FG_WHITE, FG_WHITE },
                RowBg  = Color.Empty,
            });

            return list;
        }

        private static List<StatusRow> BuildTrendRows(DashboardTrend t)
        {
            var list = new List<StatusRow>();

            // Section header — TREND
            list.Add(new StatusRow
            {
                Cells  = new[] { "TREND", "", "", "" },
                Colors = new[] { FG_AQUA, FG_AQUA, FG_AQUA, FG_AQUA },
                RowBg  = SECTION_BG_TEAL,
            });

            // Resolve current state label + color
            string state = (t.CurrentState ?? "FLAT").ToUpperInvariant();
            string stateLbl;
            Color stateColor;
            switch (state)
            {
                case "BULL": stateLbl = "BULL ↑"; stateColor = Color.Lime;       break;
                case "BEAR": stateLbl = "BEAR ↓"; stateColor = Color.IndianRed;  break;
                default:     stateLbl = "FLAT —"; stateColor = Color.Gray;       break;
            }

            // Row 1: Current state | Controlling pivot price
            string ctrlStr = double.IsNaN(t.ControllingPivotPrice)
                ? "—"
                : t.ControllingPivotPrice.ToString("0.##", CultureInfo.InvariantCulture);
            list.Add(new StatusRow
            {
                Cells  = new[] { "Current", stateLbl, "Controlling", ctrlStr },
                Colors = new[]
                {
                    FG_WHITE,
                    stateColor,
                    FG_WHITE,
                    double.IsNaN(t.ControllingPivotPrice) ? FG_GRAY : FG_AQUA,
                },
                RowBg  = Color.Empty,
            });

            // Row 2: Last control point — "5298.50 @ 11:30:42" or "no recent"
            bool hasCp = !double.IsNaN(t.LastControlPointPrice) && t.LastControlPointTime != default(DateTime);
            string cpStr = hasCp
                ? t.LastControlPointPrice.ToString("0.##", CultureInfo.InvariantCulture)
                  + " @ " + t.LastControlPointTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : "no recent";
            list.Add(new StatusRow
            {
                Cells  = new[] { "Last CP", cpStr, "", "" },
                Colors = new[] { FG_WHITE, hasCp ? FG_WHITE : FG_GRAY, FG_WHITE, FG_WHITE },
                RowBg  = Color.Empty,
            });

            // Row 3: Last break reason — full-width cell on the right
            string breakStr = string.IsNullOrEmpty(t.LastBreakReason) ? "no recent" : t.LastBreakReason;
            list.Add(new StatusRow
            {
                Cells  = new[] { "Last break", breakStr, "", "" },
                Colors = new[]
                {
                    FG_WHITE,
                    string.IsNullOrEmpty(t.LastBreakReason) ? FG_GRAY : FG_AMBER,
                    FG_WHITE,
                    FG_WHITE,
                },
                RowBg  = Color.Empty,
            });

            // Row 4: Aligned / Opposed zone counts
            Color alignedC = (t.AlignedZoneCount >= t.OpposedZoneCount) ? FG_LIME : FG_GRAY;
            Color opposedC = (t.OpposedZoneCount > 0) ? FG_AMBER : FG_GRAY;
            list.Add(new StatusRow
            {
                Cells = new[]
                {
                    "Aligned zones",
                    t.AlignedZoneCount.ToString(CultureInfo.InvariantCulture),
                    "Opposed zones",
                    t.OpposedZoneCount.ToString(CultureInfo.InvariantCulture),
                },
                Colors = new[] { FG_WHITE, alignedC, FG_WHITE, opposedC },
                RowBg  = Color.Empty,
            });

            return list;
        }

        private static List<StatusRow> BuildZoneRows(DashboardZones z)
        {
            var list = new List<StatusRow>();

            // ZONES section header — labels Demand / Supply / Nearest D/S
            list.Add(new StatusRow
            {
                Cells  = new[] { "ZONES", "Demand", "Supply", "Nearest D/S" },
                Colors = new[] { FG_WHITE, FG_LIME, FG_RED, FG_WHITE },
                RowBg  = SECTION_BG_TEAL,
            });

            list.Add(BuildOneZoneRow("LTF", z.LtfDemandCount, z.LtfSupplyCount,
                FirstValid(z.LtfNearestDemand, z.NearestDemandPrice),
                FirstValid(z.LtfNearestSupply, z.NearestSupplyPrice)));

            list.Add(BuildOneZoneRow("ITF", z.ItfDemandCount, z.ItfSupplyCount,
                FirstValid(z.ItfNearestDemand, z.NearestDemandPrice),
                FirstValid(z.ItfNearestSupply, z.NearestSupplyPrice)));

            list.Add(BuildOneZoneRow("HTF", z.HtfDemandCount, z.HtfSupplyCount,
                FirstValid(z.HtfNearestDemand, z.NearestDemandPrice),
                FirstValid(z.HtfNearestSupply, z.NearestSupplyPrice)));

            return list;
        }

        private static StatusRow BuildOneZoneRow(string tierLabel, int demCount, int supCount,
                                                 double nearestDem, double nearestSup)
        {
            string nearStr =
                (double.IsNaN(nearestDem) ? "—" : nearestDem.ToString("0.##", CultureInfo.InvariantCulture))
                + " / "
                + (double.IsNaN(nearestSup) ? "—" : nearestSup.ToString("0.##", CultureInfo.InvariantCulture));

            return new StatusRow
            {
                Cells = new[]
                {
                    tierLabel,
                    demCount.ToString(CultureInfo.InvariantCulture),
                    supCount.ToString(CultureInfo.InvariantCulture),
                    nearStr,
                },
                Colors = new[]
                {
                    FG_WHITE,
                    demCount > 0 ? FG_LIME : FG_GRAY,
                    supCount > 0 ? FG_RED  : FG_GRAY,
                    FG_WHITE,
                },
                RowBg = Color.Empty,
            };
        }

        private static List<StatusRow> BuildActiveTradeRows(IList<ActiveTradeRow> trades, bool lifecycleOn)
        {
            var list = new List<StatusRow>();

            // Section header
            list.Add(new StatusRow
            {
                Cells  = new[] { "LIVE TRADES", "Dir/Tier", "Cons / Risk", "Entry / SL" },
                Colors = new[] { FG_WHITE, FG_WHITE, FG_WHITE, FG_WHITE },
                RowBg  = SECTION_BG_OLIV,
            });

            if (!lifecycleOn)
            {
                list.Add(new StatusRow
                {
                    Cells  = new[] { "Lifecycle OFF", "—", "—", "—" },
                    Colors = new[] { FG_YELLOW, FG_GRAY, FG_GRAY, FG_GRAY },
                    RowBg  = Color.Empty,
                });
                return list;
            }

            int liveCount = 0;
            if (trades != null)
            {
                for (int i = 0; i < trades.Count && liveCount < 5; i++)
                {
                    var t = trades[i];
                    if (t == null) continue;

                    bool armed  = string.Equals(t.State, "ARMED", StringComparison.OrdinalIgnoreCase);
                    Color stateC = armed ? FG_GRAY : FG_LIME;

                    // Dir cell color: long → lime, short → red
                    Color dirC = (!string.IsNullOrEmpty(t.Dir) && t.Dir.StartsWith("S",
                                  StringComparison.OrdinalIgnoreCase)) ? FG_RED : FG_LIME;

                    string consRiskCell = t.Contracts.ToString(CultureInfo.InvariantCulture)
                                        + " / $"
                                        + t.DollarRisk.ToString("0", CultureInfo.InvariantCulture);

                    string entrySLCell = t.Entry.ToString("0.##", CultureInfo.InvariantCulture)
                                       + " / "
                                       + t.CurSL.ToString("0.##", CultureInfo.InvariantCulture);

                    list.Add(new StatusRow
                    {
                        Cells = new[]
                        {
                            "#" + t.TradeNum.ToString(CultureInfo.InvariantCulture)
                                + " " + (t.State ?? ""),
                            t.Dir ?? "",
                            consRiskCell,
                            entrySLCell,
                        },
                        Colors = new[] { stateC, dirC, FG_WHITE, FG_WHITE },
                        RowBg  = Color.Empty,
                    });
                    liveCount++;
                }
            }

            if (liveCount == 0)
            {
                list.Add(new StatusRow
                {
                    Cells  = new[] { "—", "no live trades", "", "" },
                    Colors = new[] { FG_GRAY, FG_GRAY, FG_GRAY, FG_GRAY },
                    RowBg  = Color.Empty,
                });
            }

            return list;
        }

        private static List<StatusRow> BuildStatsRows(DashboardStats st, bool lifecycleOn)
        {
            var list = new List<StatusRow>();

            if (!lifecycleOn)
            {
                list.Add(new StatusRow
                {
                    Cells  = new[] { "Lifecycle OFF", "Enable in", "settings to", "track stats" },
                    Colors = new[] { FG_YELLOW, FG_YELLOW, FG_YELLOW, FG_YELLOW },
                    RowBg  = SECTION_BG_GRAY,
                });
                return list;
            }

            // STATS header row — W / L / BE counts
            list.Add(new StatusRow
            {
                Cells  = new[]
                {
                    "STATS",
                    "W: " + st.Wins.ToString(CultureInfo.InvariantCulture),
                    "L: " + st.Losses.ToString(CultureInfo.InvariantCulture),
                    "BE: " + st.BreakEvens.ToString(CultureInfo.InvariantCulture),
                },
                Colors = new[] { FG_WHITE, FG_LIME, FG_RED, FG_GRAY },
                RowBg  = SECTION_BG_PURP,
            });

            // Win Rate | Profit Factor
            list.Add(new StatusRow
            {
                Cells = new[]
                {
                    "Win Rate",
                    st.WinRate.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                    "Profit Factor",
                    double.IsInfinity(st.ProfitFactor) || double.IsNaN(st.ProfitFactor)
                        ? "—"
                        : st.ProfitFactor.ToString("0.00", CultureInfo.InvariantCulture),
                },
                Colors = new[] { FG_WHITE, FG_AQUA, FG_WHITE, FG_WHITE },
                RowBg  = Color.Empty,
            });

            // Total R | Total $
            string totalRstr = (st.TotalR >= 0 ? "+" : "")
                             + st.TotalR.ToString("0.00", CultureInfo.InvariantCulture) + "R";
            string totalDstr = (st.TotalDollar < 0 ? "-$" : "$")
                             + Math.Abs(st.TotalDollar).ToString("0.00", CultureInfo.InvariantCulture);
            list.Add(new StatusRow
            {
                Cells  = new[] { "Total R", totalRstr, "Total $", totalDstr },
                Colors = new[]
                {
                    FG_WHITE,
                    st.TotalR      >= 0 ? FG_LIME : FG_RED,
                    FG_WHITE,
                    st.TotalDollar >= 0 ? FG_LIME : FG_RED,
                },
                RowBg  = Color.Empty,
            });

            // Avg Win | Avg Loss
            list.Add(new StatusRow
            {
                Cells  = new[]
                {
                    "Avg Win",
                    "+" + st.AvgWinR.ToString("0.00", CultureInfo.InvariantCulture) + "R",
                    "Avg Loss",
                    st.AvgLossR.ToString("0.00", CultureInfo.InvariantCulture) + "R",
                },
                Colors = new[] { FG_WHITE, FG_LIME, FG_WHITE, FG_RED },
                RowBg  = Color.Empty,
            });

            // Max W Streak | Max L Streak
            list.Add(new StatusRow
            {
                Cells  = new[]
                {
                    "Max W Streak",
                    st.MaxWinStreak.ToString(CultureInfo.InvariantCulture),
                    "Max L Streak",
                    st.MaxLossStreak.ToString(CultureInfo.InvariantCulture),
                },
                Colors = new[] { FG_WHITE, FG_LIME, FG_WHITE, FG_RED },
                RowBg  = Color.Empty,
            });

            return list;
        }

        // ── Drawing helpers ──────────────────────────────────────────────────

        /// <summary>Update <paramref name="widths"/> to fit <paramref name="cells"/>.</summary>
        private static void MeasureRow(Graphics g, Font font, string[] cells, int[] widths)
        {
            if (cells == null) return;
            for (int i = 0; i < cells.Length && i < widths.Length; i++)
            {
                string s = cells[i] ?? "";
                int w = (int)Math.Ceiling(g.MeasureString(s, font).Width) + CELL_PAD_X * 2;
                if (w > widths[i]) widths[i] = w;
            }
        }

        /// <summary>
        /// Draw one 4-column row with optional row background. Cells with empty
        /// strings are still painted for the bg fill — that matches Pine v1.4
        /// behavior for section banners.
        /// </summary>
        private static void DrawGridRow(Graphics g, int x0, int y, int[] widths,
                                        string[] cells, Color[] colors, Font font, Color rowBg)
        {
            int rowH = (int)Math.Ceiling(g.MeasureString("Hg", font).Height) + CELL_PAD_Y * 2;
            int cx = x0;
            for (int i = 0; i < widths.Length; i++)
            {
                var rect = new Rectangle(cx, y, widths[i], rowH);
                if (rowBg != Color.Empty)
                    FillCell(g, rect, rowBg);

                string text = (cells != null && i < cells.Length) ? (cells[i] ?? "") : "";
                Color  col  = (colors != null && i < colors.Length) ? colors[i] : FG_WHITE;

                if (!string.IsNullOrEmpty(text))
                {
                    var align = (i == 0) ? StringAlignment.Near : StringAlignment.Center;
                    DrawCellText(g, rect, text, col, font, align);
                }
                cx += widths[i];
            }
        }

        private static void FillCell(Graphics g, Rectangle rect, Color color)
        {
            using (var br = new SolidBrush(color))
                g.FillRectangle(br, rect);
        }

        private static void DrawCellText(Graphics g, Rectangle rect, string text,
                                         Color color, Font font, StringAlignment align)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (var br = new SolidBrush(color))
            using (var sf = new StringFormat
            {
                Alignment     = align,
                LineAlignment = StringAlignment.Center,
                FormatFlags   = StringFormatFlags.NoWrap,
                Trimming      = StringTrimming.EllipsisCharacter,
            })
            {
                var inner = new RectangleF(
                    rect.X + CELL_PAD_X,
                    rect.Y + CELL_PAD_Y,
                    Math.Max(0, rect.Width  - CELL_PAD_X * 2),
                    Math.Max(0, rect.Height - CELL_PAD_Y * 2));
                g.DrawString(text, font, br, inner, sf);
            }
        }

        private static void DrawPanelBackground(Graphics g, Rectangle panelRect)
        {
            // Anti-aliased rounded background for a slightly softer look. Falls
            // back to a plain rectangle if rounded path construction is undesired.
            var prevSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                using (var bg = new SolidBrush(BG_PANEL))
                    g.FillRectangle(bg, panelRect);
                using (var pen = new Pen(BORDER_PANEL, PANEL_BORDER))
                    g.DrawRectangle(pen, panelRect);
            }
            finally
            {
                g.SmoothingMode = prevSmoothing;
            }
        }

        // ── Position math ────────────────────────────────────────────────────

        /// <summary>
        /// Resolve the top-left point of a panel of the given size for an
        /// anchor corner of the chart. Always clamped within chart bounds so a
        /// huge panel never draws off-screen.
        /// </summary>
        private static Point ResolvePanelOrigin(Rectangle chart, PanelPosition pos, int panelW, int panelH)
        {
            int x = chart.Left + PANEL_MARGIN;
            int y = chart.Top  + PANEL_MARGIN;

            switch (pos)
            {
                case PanelPosition.TopLeft:
                    x = chart.Left + PANEL_MARGIN;
                    y = chart.Top  + PANEL_MARGIN;
                    break;
                case PanelPosition.TopRight:
                    x = chart.Right  - panelW - PANEL_MARGIN;
                    y = chart.Top    + PANEL_MARGIN;
                    break;
                case PanelPosition.BottomLeft:
                    x = chart.Left   + PANEL_MARGIN;
                    y = chart.Bottom - panelH - PANEL_MARGIN;
                    break;
                case PanelPosition.BottomRight:
                    x = chart.Right  - panelW - PANEL_MARGIN;
                    y = chart.Bottom - panelH - PANEL_MARGIN;
                    break;
                case PanelPosition.MiddleLeft:
                    x = chart.Left + PANEL_MARGIN;
                    y = chart.Top  + (chart.Height - panelH) / 2;
                    break;
                case PanelPosition.MiddleRight:
                    x = chart.Right - panelW - PANEL_MARGIN;
                    y = chart.Top   + (chart.Height - panelH) / 2;
                    break;
            }

            // Clamp inside chart bounds — protects against oversized panels.
            if (x < chart.Left)                  x = chart.Left;
            if (y < chart.Top)                   y = chart.Top;
            if (x + panelW > chart.Right)        x = Math.Max(chart.Left, chart.Right  - panelW);
            if (y + panelH > chart.Bottom)       y = Math.Max(chart.Top,  chart.Bottom - panelH);

            return new Point(x, y);
        }

        private static int ClampPanelWidth(int desired)
        {
            if (desired < MIN_PANEL_WIDTH) return MIN_PANEL_WIDTH;
            if (desired > MAX_PANEL_WIDTH) return MAX_PANEL_WIDTH;
            return desired;
        }

        // ── Misc utility ─────────────────────────────────────────────────────

        /// <summary>Map textSize 1..5 → font point size (Tiny..Huge).</summary>
        private static int MapTextSize(int textSize)
        {
            switch (textSize)
            {
                case 1:  return 8;   // Tiny
                case 2:  return 10;  // Small
                case 3:  return 12;  // Medium
                case 4:  return 14;  // Large
                case 5:  return 16;  // Huge
                default: return textSize < 1 ? 8 : 16;
            }
        }

        /// <summary>Return <paramref name="primary"/> if not NaN, else <paramref name="fallback"/>.</summary>
        private static double FirstValid(double primary, double fallback)
            => double.IsNaN(primary) ? fallback : primary;
    }
}
