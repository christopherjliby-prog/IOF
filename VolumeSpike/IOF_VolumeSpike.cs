// IOF_VolumeSpike.cs — High Volume Node detector + Volume Heatmap for Quantower
//
// Finds price levels where volume is abnormally high vs surrounding levels.
// Classifies each node: PEAK / BUILDING / BACKSIDE / ISOLATED.
// Flags ★IOF when a node overlaps a live TradePhantoms_IOF_v2 zone (cross-DLL
// via reflection — graceful no-op if IOF_v2 isn't loaded on the same chart).
//
// Draws a full volume-profile heatmap panel on the right side of the chart:
//   cool blue (low) → cyan → yellow → hot red (HVN levels).
//   HVN spike lines are drawn on top of the heatmap.
//
// DATA SOURCE — tries in order, stops at first that returns > 10 levels:
//   1. Quantower native HistoryAggregationVolumeProfile (most accurate)
//   2. Bar-close volume bucketing over LookbackBars (always works)
//
// Install: drop IOF_VolumeSpike/ subfolder into Quantower Scripts\Indicators.
//          Does NOT need to share a folder with TradePhantoms_IOF_v2.
//
// Confirmed SDK: TradingPlatform.BusinessLayer v1.145.17

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using TradingPlatform.BusinessLayer;

namespace TradePhantoms
{
    public class IOF_VolumeSpike : Indicator
    {
        // ── Detection ──────────────────────────────────────────────────────────
        [InputParameter("Spike threshold % above avg", 0, 101, 2000, 10, 0)]
        public int SpikeThresholdPct = 150;

        [InputParameter("Surrounding levels (N each side)", 1, 2, 30, 1, 0)]
        public int SurroundingN = 5;

        // ── Native VP settings ──────────────────────────────────────────────────
        [InputParameter("Profile days (native VP)", 2, 1, 60, 1, 0)]
        public int ProfileDays = 1;

        // ── Bar-based fallback settings ─────────────────────────────────────────
        [InputParameter("Fallback: lookback bars", 3, 1, 20000, 1, 0)]
        public int LookbackBars = 500;

        [InputParameter("Fallback: bucket size (ticks)", 4, 1, 20, 1, 0)]
        public int BucketTicks = 4;

        // ── IOF confluence ──────────────────────────────────────────────────────
        [InputParameter("IOF zone proximity (ticks)", 5, 1, 100, 1, 0)]
        public int IOFZoneProximityTicks = 8;

        // ── HVN line display ────────────────────────────────────────────────────
        [InputParameter("Show HVN labels", 6)]
        public bool ShowLabels = true;

        [InputParameter("HVN line width", 7, 1, 5, 1, 0)]
        public int LineWidth = 2;

        // ── Heatmap display ─────────────────────────────────────────────────────
        [InputParameter("Show heatmap", 8)]
        public bool ShowHeatmap = true;

        [InputParameter("Heatmap width (px)", 9, 20, 300, 10, 0)]
        public int HeatmapWidth = 80;

        [InputParameter("Heatmap opacity (0-255)", 10, 20, 255, 5, 0)]
        public int HeatmapOpacity = 180;

        // ── Colors ──────────────────────────────────────────────────────────────
        [InputParameter("HVN color", 11)]
        public Color HvnColor = Color.FromArgb(220, 0, 200, 255);

        [InputParameter("IOF confluence color", 12)]
        public Color ConfluenceColor = Color.FromArgb(255, 255, 215, 0);

        [InputParameter("Backside color", 13)]
        public Color BacksideColor = Color.FromArgb(220, 255, 100, 60);

        // ── Internal ────────────────────────────────────────────────────────────
        private List<HvnLevel>                    _levels  = new List<HvnLevel>();
        private List<(double Price, double Volume)> _profile = new List<(double Price, double Volume)>();
        private readonly object _lock         = new object();
        private string          _iofKey       = "";
        private bool            _nativeFailed = false;

        // ── Types ───────────────────────────────────────────────────────────────
        private enum NodeContext { Peak, Building, Backside, Isolated }

        private class HvnLevel
        {
            public double      Price;
            public double      Volume;
            public double      PctAboveAvg;
            public NodeContext Context;
            public bool        IsIOFConfluence;
        }

        public IOF_VolumeSpike()
        {
            Name           = "IOF Volume Spike";
            ShortName      = "IOF-VS";
            Description    = "Volume heatmap + HVN spike lines. ★IOF = overlaps live IOF zone.";
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            lock (_lock) { _levels.Clear(); _profile.Clear(); }
            _nativeFailed = false;

            try
            {
                string sym    = this.Symbol?.Name ?? "UNK";
                string period = this.HistoricalData?.Aggregation?.ToString() ?? "UNK";
                _iofKey = $"{sym}_{period}";
            }
            catch { _iofKey = ""; }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason == UpdateReason.NewBar || args.Reason == UpdateReason.HistoricalBar)
                RebuildLevels();
        }

        // ── Core rebuild ────────────────────────────────────────────────────────

        private void RebuildLevels()
        {
            if (this.Symbol == null) return;

            List<(double Price, double Volume)> profile = null;

            if (!_nativeFailed)
            {
                profile = FetchNativeProfile();
                if (profile == null || profile.Count < SurroundingN * 2 + 1)
                {
                    _nativeFailed = true;
                    profile = null;
                }
            }

            if (profile == null)
                profile = FetchBarBasedProfile();

            if (profile == null || profile.Count < SurroundingN * 2 + 1) return;

            profile.Sort((a, b) => a.Price.CompareTo(b.Price));

            double tick      = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            double tolerance = IOFZoneProximityTicks * tick;

            var newLevels = new List<HvnLevel>();

            for (int i = SurroundingN; i < profile.Count - SurroundingN; i++)
            {
                double centerVol = profile[i].Volume;
                double sumVol    = 0;
                for (int j = i - SurroundingN; j <= i + SurroundingN; j++)
                    if (j != i) sumVol += profile[j].Volume;

                double avgVol = sumVol / (SurroundingN * 2);
                if (avgVol <= 0) continue;

                double pctAbove = centerVol / avgVol * 100.0;
                if (pctAbove < SpikeThresholdPct) continue;

                bool isIOF = CheckIOFConfluence(profile[i].Price, tolerance);

                newLevels.Add(new HvnLevel
                {
                    Price           = profile[i].Price,
                    Volume          = centerVol,
                    PctAboveAvg     = pctAbove,
                    Context         = ClassifyContext(profile, i),
                    IsIOFConfluence = isIOF
                });
            }

            lock (_lock)
            {
                _profile.Clear();
                _profile.AddRange(profile);
                _levels.Clear();
                _levels.AddRange(newLevels);
            }
        }

        // ── Volume profile sources ───────────────────────────────────────────────

        private List<(double Price, double Volume)> FetchNativeProfile()
        {
            try
            {
                var agg      = new HistoryAggregationVolumeProfile(Period.DAY1);
                var fromTime = DateTime.UtcNow.Date.AddDays(-ProfileDays);
                var hd       = this.Symbol.GetHistory(agg, fromTime, DateTime.UtcNow);
                if (hd == null || hd.Count == 0) return null;

                var result = new List<(double Price, double Volume)>();

                for (int i = 0; i < hd.Count; i++)
                {
                    var item = hd[i, SeekOriginHistory.Begin] as HistoryItemVolumeProfile;
                    if (item?.PriceLevels == null) continue;

                    foreach (var kvp in item.PriceLevels)
                    {
                        if (kvp.Value == null || kvp.Value.Volume <= 0) continue;
                        result.Add((kvp.Key, kvp.Value.Volume));
                    }
                }

                return result.Count > 10 ? result : null;
            }
            catch { return null; }
        }

        private List<(double Price, double Volume)> FetchBarBasedProfile()
        {
            try
            {
                if (this.HistoricalData == null) return null;

                double tick       = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
                double bucketSize = BucketTicks * tick;
                if (bucketSize <= 0) return null;

                var buckets = new Dictionary<int, double>();
                int total   = this.HistoricalData.Count;
                int start   = Math.Max(0, total - LookbackBars);

                for (int i = start; i < total; i++)
                {
                    var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (bar == null || bar.Volume <= 0) continue;

                    double volPer = bar.Volume / 4.0;
                    foreach (double px in new[] { bar.Open, bar.High, bar.Low, bar.Close })
                    {
                        int bucket = (int)Math.Round(px / bucketSize);
                        if (!buckets.ContainsKey(bucket)) buckets[bucket] = 0;
                        buckets[bucket] += volPer;
                    }
                }

                return buckets.Count > 10
                    ? buckets.Select(kv => (kv.Key * bucketSize, kv.Value)).ToList()
                    : null;
            }
            catch { return null; }
        }

        // ── Context classification ───────────────────────────────────────────────

        private static NodeContext ClassifyContext(List<(double Price, double Volume)> profile, int idx)
        {
            int n = Math.Min(3, idx);

            bool leftRising   = true;
            bool rightFalling = true;

            for (int j = idx - n; j < idx - 1; j++)
                if (profile[j].Volume > profile[j + 1].Volume) { leftRising = false; break; }

            for (int j = idx + 1; j < idx + n && j + 1 < profile.Count; j++)
                if (profile[j].Volume < profile[j + 1].Volume) { rightFalling = false; break; }

            if  (leftRising  && rightFalling)  return NodeContext.Peak;
            if  (leftRising  && !rightFalling) return NodeContext.Building;
            if  (!leftRising && rightFalling)  return NodeContext.Backside;
            return NodeContext.Isolated;
        }

        // ── IOF confluence via cross-DLL reflection ──────────────────────────────

        private bool CheckIOFConfluence(double price, double tolerance)
        {
            if (string.IsNullOrEmpty(_iofKey) || tolerance <= 0) return false;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "TradePhantoms_IOF_v2") continue;
                    var type   = asm.GetType("TradePhantoms.IOFZoneRegistry");
                    if (type == null) continue;
                    var method = type.GetMethod("IsNearZone",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method == null) continue;
                    return (bool)method.Invoke(null, new object[] { _iofKey, price, tolerance });
                }
            }
            catch { }
            return false;
        }

        // ── Rendering ────────────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (this.Symbol == null) return;

            var gr  = args.Graphics;
            var win = this.CurrentChart.MainWindow;

            List<HvnLevel> levelsSnap;
            List<(double Price, double Volume)> profileSnap;
            lock (_lock)
            {
                levelsSnap  = new List<HvnLevel>(_levels);
                profileSnap = new List<(double Price, double Volume)>(_profile);
            }

            var rect = (System.Drawing.Rectangle)win.ClientRectangle;

            // Draw heatmap panel first so HVN lines render on top
            if (ShowHeatmap && profileSnap.Count >= 2)
                DrawHeatmap(gr, win, rect, profileSnap);

            if (levelsSnap.Count == 0) return;

            using var labelFont = new Font("Consolas", 8f, FontStyle.Regular);

            int lineRight = ShowHeatmap ? rect.Right - HeatmapWidth : rect.Right;

            foreach (var lvl in levelsSnap)
            {
                int y;
                try { y = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(lvl.Price)); }
                catch { continue; }

                if (y < rect.Top || y > rect.Bottom) continue;

                Color lineColor = lvl.IsIOFConfluence ? ConfluenceColor
                                : lvl.Context == NodeContext.Backside ? BacksideColor
                                : HvnColor;

                DashStyle dash = lvl.Context switch
                {
                    NodeContext.Backside  => DashStyle.Dash,
                    NodeContext.Building  => DashStyle.DashDot,
                    NodeContext.Isolated  => DashStyle.Dot,
                    _                    => DashStyle.Solid
                };

                using var pen = new Pen(lineColor, LineWidth) { DashStyle = dash };
                gr.DrawLine(pen, rect.Left, y, lineRight, y);

                if (ShowLabels)
                {
                    string ctx   = lvl.Context.ToString().ToUpper();
                    string pct   = $"+{(lvl.PctAboveAvg - 100):F0}%";
                    string iof   = lvl.IsIOFConfluence ? " ★IOF" : "";
                    string label = $"HVN {ctx} {pct}{iof}";

                    using var brush = new SolidBrush(Color.FromArgb(220, lineColor));
                    gr.DrawString(label, labelFont, brush, rect.Left + 4, y - 14);
                }
            }
        }

        private void DrawHeatmap(Graphics gr, dynamic win,
                                  System.Drawing.Rectangle rect,
                                  List<(double Price, double Volume)> profile)
        {
            double maxVol = 0;
            foreach (var p in profile) if (p.Volume > maxVol) maxVol = p.Volume;
            if (maxVol <= 0) return;

            int panelLeft = rect.Right - HeatmapWidth;

            // Subtle dark background for the panel
            using var bgBrush = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
            gr.FillRectangle(bgBrush, panelLeft, rect.Top, HeatmapWidth, rect.Height);

            for (int i = 0; i < profile.Count; i++)
            {
                int y;
                try { y = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(profile[i].Price)); }
                catch { continue; }

                if (y < rect.Top || y > rect.Bottom) continue;

                // Row bounds: midpoint to each neighbor
                int yTop, yBot;

                if (i < profile.Count - 1)
                {
                    int yNext;
                    try { yNext = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(profile[i + 1].Price)); }
                    catch { yNext = y - 2; }
                    yTop = (y + yNext) / 2;
                }
                else
                {
                    yTop = y - 1;
                }

                if (i > 0)
                {
                    int yPrev;
                    try { yPrev = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(profile[i - 1].Price)); }
                    catch { yPrev = y + 2; }
                    yBot = (y + yPrev) / 2;
                }
                else
                {
                    yBot = y + 1;
                }

                if (yTop > yBot) { int tmp = yTop; yTop = yBot; yBot = tmp; }
                int rowH = Math.Max(1, yBot - yTop);

                double t    = profile[i].Volume / maxVol;
                int    barW = Math.Max(1, (int)Math.Round(t * HeatmapWidth));

                using var brush = new SolidBrush(HeatColor(t, HeatmapOpacity));
                gr.FillRectangle(brush, rect.Right - barW, yTop, barW, rowH);
            }

            // Thin separator line between heatmap and chart
            using var sepPen = new Pen(Color.FromArgb(60, 255, 255, 255), 1);
            gr.DrawLine(sepPen, panelLeft, rect.Top, panelLeft, rect.Bottom);
        }

        // Interpolates cool blue → cyan → yellow → hot red
        private static Color HeatColor(double t, int alpha)
        {
            t = Math.Max(0, Math.Min(1, t));
            int r, g, b;

            if (t < 0.25)
            {
                double s = t / 0.25;
                r = 0;
                g = (int)(s * 80);
                b = (int)(160 + s * 95);
            }
            else if (t < 0.5)
            {
                double s = (t - 0.25) / 0.25;
                r = 0;
                g = (int)(80 + s * 175);
                b = (int)(255 - s * 255);
            }
            else if (t < 0.75)
            {
                double s = (t - 0.5) / 0.25;
                r = (int)(s * 255);
                g = 255;
                b = 0;
            }
            else
            {
                double s = (t - 0.75) / 0.25;
                r = 255;
                g = (int)(255 - s * 255);
                b = 0;
            }

            return Color.FromArgb(alpha, r, g, b);
        }

        protected override void OnClear()
        {
            lock (_lock) { _levels.Clear(); _profile.Clear(); }
        }
    }
}
