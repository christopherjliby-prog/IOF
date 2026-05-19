// =============================================================================
// IOF_ZoneVolumeProfile.cs — Zone Volume Profile Overlay
// =============================================================================
// Version : 1.0.0
// Date    : 2026-05-19
// Author  : Christopher (Nfifty4) + Christopher's Claude (Sonnet 4.6)
//
// Standalone Quantower overlay indicator. Detects IOF demand and supply zones
// using the same IBI scanner as TradePhantoms_IOF_v2, then draws a
// volume-by-price profile inside each of the N closest zones per direction.
//
// REQUIRES: MultiTimeframeZones.cs in the same Scripts/Indicators folder.
//           (Ships in the same zip — drop both files together.)
//
// HOW TO USE:
//   Add to any chart alongside (or without) the main IOF indicator.
//   The volume bars show where volume concentrated during zone formation.
//   The HVN (highest volume node) is highlighted — that is the most
//   contested price level inside the zone, and the most reliable entry
//   reference within the base.
//
// SETTINGS:
//   Max zones per direction  — 1 to 5. Shows closest N demand + N supply.
//   Volume bar max width %   — how wide the profile fills the zone (10–100%).
//   HVN marker               — highlights the highest volume price level.
//   Show labels              — prints zone direction + HVN price on chart.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using TradingPlatform.BusinessLayer;
using TPMTF = TradePhantomsIOF.MultiTF;

namespace TradePhantomsIOF
{
    public class IOF_ZoneVolumeProfile : Indicator, IVolumeAnalysisIndicator
    {
        // ─────────────────────────────────────────────────────────────────────
        // INPUTS — Zone Detection
        // ─────────────────────────────────────────────────────────────────────
        [InputParameter("Min score (0-21)", 1, 0, 21, 1, 0)]
        public int MinScore = 9;

        [InputParameter("Max zones per direction (1-5)", 2, 1, 5, 1, 0)]
        public int MaxZonesPerDir = 2;

        [InputParameter("Lookback bars to scan", 3, 50, 5000, 50, 0)]
        public int LookbackBars = 500;

        [InputParameter("Max base candles", 4, 1, 20, 1, 0)]
        public int MaxBaseCandles = 7;

        [InputParameter("Min impulse / base ratio", 5, 1.0, 10.0, 0.1, 1)]
        public double MinImpulseRatio = 2.0;

        [InputParameter("Base candle max body %", 6, 0.0, 1.0, 0.05, 2)]
        public double BaseCandleMaxBodyPct = 0.5;

        [InputParameter("Show demand zones", 7)]
        public bool ShowDemand = true;

        [InputParameter("Show supply zones", 8)]
        public bool ShowSupply = true;

        // ─────────────────────────────────────────────────────────────────────
        // INPUTS — Volume Profile
        // ─────────────────────────────────────────────────────────────────────
        [InputParameter("Volume bar max width (% of zone width)", 10, 10, 100, 5, 0)]
        public int VolumeBarMaxWidthPct = 60;

        [InputParameter("Show HVN marker", 11)]
        public bool ShowHvn = true;

        [InputParameter("Show zone labels", 12)]
        public bool ShowLabels = true;

        // ─────────────────────────────────────────────────────────────────────
        // INPUTS — Colors
        // ─────────────────────────────────────────────────────────────────────
        [InputParameter("Demand zone fill", 20)]
        public Color DemandZoneColor = Color.FromArgb(40, 0, 200, 80);

        [InputParameter("Demand volume bars", 21)]
        public Color DemandVolColor = Color.FromArgb(180, 0, 220, 100);

        [InputParameter("Supply zone fill", 22)]
        public Color SupplyZoneColor = Color.FromArgb(40, 220, 60, 60);

        [InputParameter("Supply volume bars", 23)]
        public Color SupplyVolColor = Color.FromArgb(180, 220, 80, 80);

        [InputParameter("HVN highlight color", 24)]
        public Color HvnColor = Color.FromArgb(230, 255, 215, 0);

        [InputParameter("Zone border color", 25)]
        public Color BorderColor = Color.FromArgb(120, 180, 180, 180);

        // ─────────────────────────────────────────────────────────────────────
        // STATE
        // ─────────────────────────────────────────────────────────────────────
        private List<TPMTF.TimeframeZone> demandZones = new List<TPMTF.TimeframeZone>();
        private List<TPMTF.TimeframeZone> supplyZones = new List<TPMTF.TimeframeZone>();

        // IVolumeAnalysisIndicator — allows Quantower to feed volume-by-price
        // data into this indicator when available.
        public VolumeAnalysisData VolumeAnalysisData { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        // INIT
        // ─────────────────────────────────────────────────────────────────────
        public IOF_ZoneVolumeProfile()
        {
            Name        = "IOF Zone Volume Profile";
            ShortName   = "IOF-ZVP";
            Description = "Volume-by-price profile inside the closest IOF demand and supply zones.";
            IsOverlay   = true;
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            ShortName = $"IOF-ZVP (top {MaxZonesPerDir})";
            this.demandZones = new List<TPMTF.TimeframeZone>();
            this.supplyZones = new List<TPMTF.TimeframeZone>();
        }

        // ─────────────────────────────────────────────────────────────────────
        // UPDATE — rescan and rank on each bar close
        // ─────────────────────────────────────────────────────────────────────
        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason != UpdateReason.BarClose &&
                args.Reason != UpdateReason.HistoricalBar) return;

            if (this.HistoricalData == null || this.HistoricalData.Count < 10) return;

            var all = TPMTF.MultiTFZoneScanner.ScanTimeframe(
                this.HistoricalData,
                TPMTF.ZoneTimeframe.LTF,
                this.LookbackBars,
                this.BaseCandleMaxBodyPct,
                this.MinImpulseRatio,
                this.MaxBaseCandles);

            if (all == null) { all = new List<TPMTF.TimeframeZone>(); }

            double price = GetCurrentPrice();

            this.demandZones = all
                .Where(z => z.IsLong && z.Active)
                .OrderBy(z => Math.Abs(price - (z.BodyHi + z.WickLo) / 2.0))
                .Take(MaxZonesPerDir)
                .ToList();

            this.supplyZones = all
                .Where(z => !z.IsLong && z.Active)
                .OrderBy(z => Math.Abs(price - (z.WickHi + z.BodyLo) / 2.0))
                .Take(MaxZonesPerDir)
                .ToList();
        }

        // ─────────────────────────────────────────────────────────────────────
        // PAINT
        // ─────────────────────────────────────────────────────────────────────
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (this.Symbol == null) return;
            double tickSize = this.Symbol.TickSize;
            if (tickSize <= 0) return;

            var gr  = args.Graphics;
            var win = args.MainWindow;

            if (ShowDemand)
                foreach (var z in this.demandZones)
                    DrawZoneProfile(gr, win, z, isDemand: true, tickSize);

            if (ShowSupply)
                foreach (var z in this.supplyZones)
                    DrawZoneProfile(gr, win, z, isDemand: false, tickSize);
        }

        // ─────────────────────────────────────────────────────────────────────
        // DRAW ONE ZONE PROFILE
        // ─────────────────────────────────────────────────────────────────────
        private void DrawZoneProfile(Graphics gr, dynamic win,
            TPMTF.TimeframeZone z, bool isDemand, double tickSize)
        {
            // Price boundaries (asymmetric per IOF doctrine)
            double priceTop    = isDemand ? z.BodyHi : z.WickHi;
            double priceBottom = isDemand ? z.WickLo : z.BodyLo;
            if (priceTop <= priceBottom) return;

            // Screen Y
            int yTop    = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(priceTop));
            int yBottom = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(priceBottom));
            if (yBottom <= yTop) return;
            int zoneH = yBottom - yTop;

            // Screen X — zone left is the formation start bar, right extends to now
            int xLeft  = GetBarX(win, z.BaseStartIndex);
            int xRight = GetBarX(win, 0); // bar index 0 = most recent
            if (xRight <= xLeft) xRight = xLeft + 60;
            int zoneW = xRight - xLeft;

            // Zone background fill
            using (var bg = new SolidBrush(isDemand ? DemandZoneColor : SupplyZoneColor))
                gr.FillRectangle(bg, xLeft, yTop, zoneW, zoneH);

            // Zone border
            using (var border = new Pen(BorderColor, 1f))
                gr.DrawRectangle(border, xLeft, yTop, zoneW, zoneH);

            // Build volume profile from base candle bars
            var profile = BuildVolumeProfile(
                z.BaseStartIndex, z.BaseEndIndex,
                priceBottom, priceTop, tickSize);

            if (profile == null || profile.Count == 0) goto DrawLabel;

            double maxVol = profile.Values.Max();
            if (maxVol <= 0) goto DrawLabel;

            // Find HVN price level
            double hvnPrice = profile.OrderByDescending(kv => kv.Value).First().Key;

            int maxBarPx = (int)(zoneW * VolumeBarMaxWidthPct / 100.0);
            if (maxBarPx < 2) maxBarPx = 2;

            Color volColor = isDemand ? DemandVolColor : SupplyVolColor;

            foreach (var kv in profile)
            {
                double levelPrice = kv.Key;
                double vol        = kv.Value;
                bool   isHvn      = ShowHvn && Math.Abs(levelPrice - hvnPrice) < tickSize * 0.5;

                int barW = (int)Math.Round(maxBarPx * (vol / maxVol));
                if (barW < 1) barW = 1;

                // Y bounds for this tick-level price band
                int yBarTop = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(levelPrice + tickSize));
                int yBarBot = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(levelPrice));
                int barH    = yBarBot - yBarTop;
                if (barH < 1) barH = 1;

                // Clip to zone bounds
                yBarTop = Math.Max(yBarTop, yTop);
                barH    = Math.Min(barH, yBottom - yBarTop);
                if (barH <= 0) continue;

                using (var brush = new SolidBrush(isHvn ? HvnColor : volColor))
                    gr.FillRectangle(brush, xLeft, yBarTop, barW, barH);
            }

            // HVN price line across full zone width
            if (ShowHvn)
            {
                int yHvn = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(hvnPrice));
                using (var hvnPen = new Pen(HvnColor, 1f) { DashStyle = DashStyle.Dash })
                    gr.DrawLine(hvnPen, xLeft, yHvn, xRight, yHvn);
            }

            DrawLabel:
            if (ShowLabels)
            {
                string lbl = isDemand ? "D" : "S";
                if (profile != null && profile.Count > 0)
                {
                    double hvn = profile.OrderByDescending(kv => kv.Value).First().Key;
                    lbl += $" HVN {hvn:F2}";
                }
                using (var font  = new Font("Arial", 7.5f, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.White))
                using (var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                {
                    gr.DrawString(lbl, font, shadow, xLeft + 5, yTop + 4);
                    gr.DrawString(lbl, font, brush,  xLeft + 4, yTop + 3);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // BUILD VOLUME-BY-PRICE PROFILE
        // Aggregates bar volume across price levels within the zone's base bars.
        // Volume is distributed proportionally to each tick level's overlap
        // with the bar's High–Low range — a standard approximation when
        // tick-by-tick data is unavailable.
        // ─────────────────────────────────────────────────────────────────────
        private Dictionary<double, double> BuildVolumeProfile(
            int barStart, int barEnd,
            double priceBottom, double priceTop,
            double tickSize)
        {
            if (this.HistoricalData == null) return null;

            var profile = new Dictionary<double, double>();

            // Initialise all tick levels in the zone's price range
            double lvl = Math.Round(priceBottom / tickSize) * tickSize;
            while (lvl <= priceTop + tickSize * 0.1)
            {
                profile[Math.Round(lvl / tickSize) * tickSize] = 0.0;
                lvl += tickSize;
            }

            int end = Math.Min(barEnd, this.HistoricalData.Count - 1);
            for (int i = barStart; i <= end; i++)
            {
                var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null || bar.Volume <= 0) continue;

                double barHi  = Math.Min(bar.High, priceTop);
                double barLo  = Math.Max(bar.Low,  priceBottom);
                if (barHi <= barLo) continue;

                double barRange = barHi - barLo;

                // Distribute this bar's volume across overlapping tick levels
                var keys = profile.Keys.ToArray();
                foreach (double price in keys)
                {
                    double levelTop = price + tickSize;
                    double overlap  = Math.Min(levelTop, barHi) - Math.Max(price, barLo);
                    if (overlap <= 0) continue;
                    profile[price] += bar.Volume * (overlap / barRange);
                }
            }

            // Remove empty levels
            foreach (var k in profile.Keys.Where(k => profile[k] <= 0).ToArray())
                profile.Remove(k);

            return profile;
        }

        // ─────────────────────────────────────────────────────────────────────
        // HELPERS
        // ─────────────────────────────────────────────────────────────────────
        private int GetBarX(dynamic win, int barIndex)
        {
            try
            {
                if (this.HistoricalData == null || barIndex < 0 ||
                    barIndex >= this.HistoricalData.Count) return 0;
                var bar = this.HistoricalData[barIndex, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null) return 0;
                return (int)Math.Round((double)win.CoordinatesConverter.GetChartX(bar.TimeLeft));
            }
            catch { return 0; }
        }

        private double GetCurrentPrice()
        {
            try
            {
                if (this.Symbol != null && this.Symbol.Last > 0) return this.Symbol.Last;
                if (this.HistoricalData != null && this.HistoricalData.Count > 0)
                {
                    var bar = this.HistoricalData[0, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (bar != null) return bar.Close;
                }
            }
            catch { }
            return 0.0;
        }

        protected override void OnClear()
        {
            this.demandZones?.Clear();
            this.supplyZones?.Clear();
        }
    }
}
