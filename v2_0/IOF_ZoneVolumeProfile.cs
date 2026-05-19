// =============================================================================
// IOF_ZoneVolumeProfile.cs — Zone Volume Profile Overlay
// =============================================================================
// Version : 1.1.0 (standalone — zero external dependencies)
// Date    : 2026-05-19
// Author  : Christopher (Nfifty4) + Christopher's Claude (Sonnet 4.6)
//
// STANDALONE: this is ONE file. Drop it into your Quantower
//   Scripts\Indicators folder and restart. No other files required.
//
// Detects IOF demand and supply zones using the same IBI (Impulse→Base→Impulse)
// pattern as the main TradePhantoms IOF indicator, then draws a volume-by-price
// profile inside the N closest zones per direction.
//
// The HVN (highest volume node) inside each zone is highlighted in gold — that
// is the most contested price level in the base and the best limit-order
// reference within the zone.
//
// KEY SETTINGS:
//   Max zones per direction  1-5 (shows closest N demand + N supply to price)
//   Volume bar max width %   how wide the profile fills inside the zone
//   HVN marker               gold bar + dashed price line at HVN
//   Show labels              direction + HVN price printed on zone
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace IOFZoneVolumeProfile
{
    public class IOF_ZoneVolumeProfile : Indicator, IVolumeAnalysisIndicator
    {
        // ─────────────────────────────────────────────────────────────────────
        // INPUTS — Zone Detection
        // ─────────────────────────────────────────────────────────────────────
        [InputParameter("Min impulse / base ratio", 1, 1.0, 10.0, 0.1, 1)]
        public double MinImpulseRatio = 2.0;

        [InputParameter("Max base candles", 2, 1, 20, 1, 0)]
        public int MaxBaseCandles = 7;

        [InputParameter("Base candle max body %", 3, 0.0, 1.0, 0.05, 2)]
        public double BaseCandleMaxBodyPct = 0.5;

        [InputParameter("Lookback bars to scan", 4, 50, 5000, 50, 0)]
        public int LookbackBars = 500;

        [InputParameter("Show demand zones", 5)]
        public bool ShowDemand = true;

        [InputParameter("Show supply zones", 6)]
        public bool ShowSupply = true;

        // ─────────────────────────────────────────────────────────────────────
        // INPUTS — Profile Display
        // ─────────────────────────────────────────────────────────────────────
        [InputParameter("Max zones per direction (1-5)", 10, 1, 5, 1, 0)]
        public int MaxZonesPerDir = 2;

        [InputParameter("Volume bar max width (% of zone width)", 11, 10, 100, 5, 0)]
        public int VolumeBarMaxWidthPct = 60;

        [InputParameter("Show HVN marker", 12)]
        public bool ShowHvn = true;

        [InputParameter("Show zone labels", 13)]
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

        [InputParameter("HVN color", 24)]
        public Color HvnColor = Color.FromArgb(230, 255, 215, 0);

        [InputParameter("Zone border", 25)]
        public Color BorderColor = Color.FromArgb(100, 180, 180, 180);

        // ─────────────────────────────────────────────────────────────────────
        // INTERNAL ZONE RECORD — fully self-contained, no external deps
        // ─────────────────────────────────────────────────────────────────────
        private struct Zone
        {
            public bool   IsDemand;
            public double WickHi, WickLo;   // for demand: top=BodyHi, stop=WickLo
            public double BodyHi, BodyLo;   //             bottom=WickLo, stop=WickHi
            public int    StartIndex;        // first base candle
            public int    EndIndex;          // last base candle
            public bool   Active;            // false if close went past far wick
        }

        // ─────────────────────────────────────────────────────────────────────
        // STATE
        // ─────────────────────────────────────────────────────────────────────
        private List<Zone> demandZones = new List<Zone>();
        private List<Zone> supplyZones = new List<Zone>();

        // IVolumeAnalysisIndicator
        public VolumeAnalysisData VolumeAnalysisData { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        // CONSTRUCTOR
        // ─────────────────────────────────────────────────────────────────────
        public IOF_ZoneVolumeProfile()
        {
            Name           = "IOF Zone Volume Profile";
            ShortName      = "IOF-ZVP";
            Description    = "Volume-by-price profile inside the closest IOF demand/supply zones. Standalone — no other files required.";
            IsOverlay      = true;
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            ShortName      = $"IOF-ZVP (top {MaxZonesPerDir})";
            this.demandZones = new List<Zone>();
            this.supplyZones = new List<Zone>();
        }

        // ─────────────────────────────────────────────────────────────────────
        // UPDATE — rescan on each closed bar
        // ─────────────────────────────────────────────────────────────────────
        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason != UpdateReason.BarClose &&
                args.Reason != UpdateReason.HistoricalBar) return;
            if (this.HistoricalData == null || this.HistoricalData.Count < 10) return;

            RebuildZoneLists();
        }

        private void RebuildZoneLists()
        {
            var all     = DetectZones();
            double price = CurrentPrice();

            this.demandZones = all
                .Where(z => z.IsDemand && z.Active)
                .OrderBy(z => Math.Abs(price - (z.BodyHi + z.WickLo) / 2.0))
                .Take(MaxZonesPerDir)
                .ToList();

            this.supplyZones = all
                .Where(z => !z.IsDemand && z.Active)
                .OrderBy(z => Math.Abs(price - (z.WickHi + z.BodyLo) / 2.0))
                .Take(MaxZonesPerDir)
                .ToList();
        }

        // ─────────────────────────────────────────────────────────────────────
        // IBI ZONE DETECTION — self-contained, no external dependencies
        //
        // Pattern: Impulse candle → 1-N base candles → Impulse candle out
        //   Base candle : body ≤ BaseCandleMaxBodyPct × range
        //   Impulse     : body > 55% of range
        //   Move-out    : ≥ MinImpulseRatio × base cluster height
        //   Demand (RBR/DBR): impulse-out is bullish (close > open)
        //   Supply (RBD/DBD): impulse-out is bearish (close < open)
        // ─────────────────────────────────────────────────────────────────────
        private List<Zone> DetectZones()
        {
            var zones   = new List<Zone>();
            int total   = this.HistoricalData.Count;
            int scanEnd = Math.Min(total - 1, LookbackBars);

            // bars are stored newest-first: index 0 = most recent bar
            // scan from scanEnd (oldest) toward 0 (newest)
            for (int i = scanEnd; i >= 2; i--)
            {
                // candidate base-cluster start
                if (!IsBase(i)) continue;

                // Walk forward to find end of base cluster
                int clusterEnd = i;
                int baseCount  = 1;
                while (clusterEnd - 1 >= 1 && baseCount < MaxBaseCandles && IsBase(clusterEnd - 1))
                {
                    clusterEnd--;
                    baseCount++;
                }

                // Need at least 1 bar before (impulse-in) and 1 after (impulse-out)
                int impulseInIdx  = i + 1;          // older bar (higher index)
                int impulseOutIdx = clusterEnd - 1; // newer bar (lower index)
                if (impulseInIdx >= total || impulseOutIdx < 0) continue;

                // Impulse-in must be a strong bar
                if (!IsImpulse(impulseInIdx)) continue;

                // Impulse-out must be a strong bar
                if (!IsImpulse(impulseOutIdx)) continue;

                // Base cluster geometry
                double baseHi = double.MinValue, baseLo = double.MaxValue;
                double bodyHi = double.MinValue, bodyLo = double.MaxValue;
                double wickHi = double.MinValue, wickLo = double.MaxValue;

                for (int b = clusterEnd; b <= i; b++)
                {
                    var bar = GetBar(b);
                    if (bar == null) continue;
                    if (bar.High > baseHi) baseHi = bar.High;
                    if (bar.Low  < baseLo) baseLo = bar.Low;
                    double bHi = Math.Max(bar.Open, bar.Close);
                    double bLo = Math.Min(bar.Open, bar.Close);
                    if (bHi > bodyHi) bodyHi = bHi;
                    if (bLo < bodyLo) bodyLo = bLo;
                    if (bar.High > wickHi) wickHi = bar.High;
                    if (bar.Low  < wickLo) wickLo = bar.Low;
                }

                double baseHeight = baseHi - baseLo;
                if (baseHeight <= 0) continue;

                // Move-out: measure distance the impulse-out bar travels from cluster
                var impulseOut = GetBar(impulseOutIdx);
                if (impulseOut == null) continue;
                bool isDemand = impulseOut.Close > impulseOut.Open; // bullish = demand zone
                double moveOut = isDemand
                    ? impulseOut.Close - baseHi
                    : baseLo - impulseOut.Close;
                if (moveOut < MinImpulseRatio * baseHeight) continue;

                // Check zone is still active (no close through distal wick)
                bool active = IsZoneActive(clusterEnd, isDemand, wickHi, wickLo);

                // Avoid duplicates — skip if we already have a zone overlapping this range
                bool duplicate = zones.Any(z =>
                    z.IsDemand == isDemand &&
                    z.StartIndex == i && z.EndIndex == clusterEnd);
                if (duplicate) continue;

                zones.Add(new Zone
                {
                    IsDemand   = isDemand,
                    WickHi     = wickHi,
                    WickLo     = wickLo,
                    BodyHi     = bodyHi,
                    BodyLo     = bodyLo,
                    StartIndex = i,
                    EndIndex   = clusterEnd,
                    Active     = active
                });

                // Skip past this cluster to avoid double-counting base candles
                i = clusterEnd;
            }

            return zones;
        }

        private bool IsZoneActive(int clusterEnd, bool isDemand, double wickHi, double wickLo)
        {
            // Zone is invalidated when price closes beyond the distal wick
            for (int j = clusterEnd - 1; j >= 0; j--)
            {
                var b = GetBar(j);
                if (b == null) continue;
                if (isDemand  && b.Close < wickLo) return false;
                if (!isDemand && b.Close > wickHi) return false;
            }
            return true;
        }

        private bool IsBase(int idx)
        {
            var bar = GetBar(idx);
            if (bar == null) return false;
            double range = bar.High - bar.Low;
            if (range <= 0) return false;
            double body = Math.Abs(bar.Close - bar.Open);
            return body / range <= BaseCandleMaxBodyPct;
        }

        private bool IsImpulse(int idx)
        {
            var bar = GetBar(idx);
            if (bar == null) return false;
            double range = bar.High - bar.Low;
            if (range <= 0) return false;
            double body = Math.Abs(bar.Close - bar.Open);
            return body / range > 0.55;
        }

        private HistoryItemBar GetBar(int idx)
        {
            if (idx < 0 || idx >= this.HistoricalData.Count) return null;
            return this.HistoricalData[idx, SeekOriginHistory.Begin] as HistoryItemBar;
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

            try
            {
                if (ShowDemand)
                    foreach (var z in this.demandZones)
                        DrawZoneProfile(gr, win, z, tickSize);

                if (ShowSupply)
                    foreach (var z in this.supplyZones)
                        DrawZoneProfile(gr, win, z, tickSize);
            }
            catch { /* painting must never throw */ }
        }

        private void DrawZoneProfile(Graphics gr, dynamic win, Zone z, double tickSize)
        {
            // Price bounds (asymmetric per IOF doctrine)
            double priceTop    = z.IsDemand ? z.BodyHi : z.WickHi;
            double priceBottom = z.IsDemand ? z.WickLo : z.BodyLo;
            if (priceTop <= priceBottom) return;

            // Screen coordinates
            int yTop    = ToY(win, priceTop);
            int yBottom = ToY(win, priceBottom);
            if (yBottom <= yTop) return;
            int zoneH = yBottom - yTop;

            // Zone left = formation start bar, right = most recent bar (extends to now)
            int xLeft  = ToX(win, z.StartIndex);
            int xRight = ToX(win, 0);
            if (xRight <= xLeft) xRight = xLeft + 40;
            int zoneW = xRight - xLeft;

            // Background fill
            using (var bg = new SolidBrush(z.IsDemand ? DemandZoneColor : SupplyZoneColor))
                gr.FillRectangle(bg, xLeft, yTop, zoneW, zoneH);

            // Border
            using (var pen = new Pen(BorderColor, 1f))
                gr.DrawRectangle(pen, xLeft, yTop, zoneW, zoneH);

            // Volume profile
            var profile = BuildVolumeProfile(z.StartIndex, z.EndIndex, priceBottom, priceTop, tickSize);
            if (profile == null || profile.Count == 0) { DrawLabel(gr, xLeft, yTop, z, null, null); return; }

            double maxVol    = profile.Values.Max();
            if (maxVol <= 0) { DrawLabel(gr, xLeft, yTop, z, null, null); return; }

            double hvnPrice  = profile.OrderByDescending(kv => kv.Value).First().Key;
            int    maxBarPx  = Math.Max(2, (int)(zoneW * VolumeBarMaxWidthPct / 100.0));
            Color  volColor  = z.IsDemand ? DemandVolColor : SupplyVolColor;

            foreach (var kv in profile)
            {
                bool   isHvn  = ShowHvn && Math.Abs(kv.Key - hvnPrice) < tickSize * 0.5;
                int    barW   = Math.Max(1, (int)Math.Round(maxBarPx * (kv.Value / maxVol)));
                int    yBarT  = Math.Max(yTop,    ToY(win, kv.Key + tickSize));
                int    yBarB  = Math.Min(yBottom, ToY(win, kv.Key));
                int    barH   = Math.Max(1, yBarB - yBarT);

                using (var b = new SolidBrush(isHvn ? HvnColor : volColor))
                    gr.FillRectangle(b, xLeft, yBarT, barW, barH);
            }

            // HVN dashed line across full zone width
            if (ShowHvn)
            {
                int yHvn = ToY(win, hvnPrice);
                using (var p = new Pen(HvnColor, 1f) { DashStyle = DashStyle.Dash })
                    gr.DrawLine(p, xLeft, yHvn, xRight, yHvn);
            }

            DrawLabel(gr, xLeft, yTop, z, profile, (double?)hvnPrice);
        }

        private void DrawLabel(Graphics gr, int xLeft, int yTop,
            Zone z, Dictionary<double, double> profile, double? hvnPrice)
        {
            if (!ShowLabels) return;
            string lbl = z.IsDemand ? "D" : "S";
            if (hvnPrice.HasValue) lbl += $" HVN {hvnPrice.Value:F2}";

            using (var font   = new Font("Arial", 7.5f, FontStyle.Bold))
            using (var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
            using (var brush  = new SolidBrush(Color.White))
            {
                gr.DrawString(lbl, font, shadow, xLeft + 5, yTop + 4);
                gr.DrawString(lbl, font, brush,  xLeft + 4, yTop + 3);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // VOLUME-BY-PRICE — distributes bar volume proportionally across tick
        // levels based on each bar's High/Low overlap with each price level.
        // Standard approximation when tick data is unavailable.
        // ─────────────────────────────────────────────────────────────────────
        private Dictionary<double, double> BuildVolumeProfile(
            int barStart, int barEnd,
            double priceBottom, double priceTop, double tickSize)
        {
            if (this.HistoricalData == null) return null;

            var profile = new Dictionary<double, double>();

            // Initialise all tick-level price bins
            double lvl = Math.Round(priceBottom / tickSize) * tickSize;
            while (lvl <= priceTop + tickSize * 0.1)
            {
                double key = Math.Round(lvl / tickSize) * tickSize;
                if (!profile.ContainsKey(key)) profile[key] = 0.0;
                lvl = Math.Round((lvl + tickSize) / tickSize) * tickSize;
            }

            int end = Math.Min(barStart, this.HistoricalData.Count - 1); // StartIndex > EndIndex (newest first)
            for (int i = barEnd; i <= end; i++)
            {
                var bar = GetBar(i);
                if (bar == null || bar.Volume <= 0) continue;

                double barHi    = Math.Min(bar.High, priceTop);
                double barLo    = Math.Max(bar.Low,  priceBottom);
                if (barHi <= barLo) continue;
                double barRange = barHi - barLo;

                double[] keys = profile.Keys.ToArray();
                foreach (double price in keys)
                {
                    double overlap = Math.Min(price + tickSize, barHi) - Math.Max(price, barLo);
                    if (overlap <= 0) continue;
                    profile[price] += bar.Volume * (overlap / barRange);
                }
            }

            // Remove empty bins
            foreach (var k in profile.Keys.Where(k => profile[k] <= 0).ToArray())
                profile.Remove(k);

            return profile;
        }

        // ─────────────────────────────────────────────────────────────────────
        // COORDINATE HELPERS
        // ─────────────────────────────────────────────────────────────────────
        private int ToY(dynamic win, double price)
        {
            try { return (int)Math.Round((double)win.CoordinatesConverter.GetChartY(price)); }
            catch { return 0; }
        }

        private int ToX(dynamic win, int barIndex)
        {
            try
            {
                var bar = GetBar(barIndex);
                if (bar == null) return 0;
                return (int)Math.Round((double)win.CoordinatesConverter.GetChartX(bar.TimeLeft));
            }
            catch { return 0; }
        }

        private double CurrentPrice()
        {
            try
            {
                if (this.Symbol?.Last > 0) return this.Symbol.Last;
                var bar = GetBar(0);
                return bar?.Close ?? 0.0;
            }
            catch { return 0.0; }
        }

        protected override void OnClear()
        {
            this.demandZones?.Clear();
            this.supplyZones?.Clear();
        }
    }
}
