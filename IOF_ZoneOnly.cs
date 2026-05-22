// IOF_ZoneOnly.cs — Standalone IOF Zone Identification
// Drop this ONE file into its OWN subfolder, e.g.:
//   C:\Quantower\Settings\Scripts\Indicators\ZoneOnly\IOF_ZoneOnly.cs
// Do NOT mix with other IOF indicator files.
//
// Detects IBI (Impulse-Base-Impulse) demand/supply zones using the same
// asymmetric geometry as the full TradePhantoms IOF v2 indicator.
// No scoring, no ARM/ACTIVE states, no trade management — zones only.

using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace IOFZoneOnly
{
    public class IOF_ZoneOnly : Indicator
    {
        // ── Detection ─────────────────────────────────────────────────────────
        [InputParameter("Lookback bars", 1, 50, 5000, 50, 0)]
        public int LookbackBars = 500;

        [InputParameter("Max base candles", 2, 1, 7, 1, 0)]
        public int MaxBaseCandles = 7;

        [InputParameter("Base body % max", 3, 0.05, 1.0, 0.05, 2)]
        public double MaxBodyPct = 0.50;

        [InputParameter("Min impulse ratio", 4, 1.0, 10.0, 0.5, 1)]
        public double MinImpulse = 2.0;

        // ── Display ───────────────────────────────────────────────────────────
        [InputParameter("Show demand", 5)]
        public bool ShowDemand = true;

        [InputParameter("Show supply", 6)]
        public bool ShowSupply = true;

        [InputParameter("Show labels", 7)]
        public bool ShowLabels = true;

        [InputParameter("Extend zones to right", 8)]
        public bool ExtendRight = true;

        // ── Colors ────────────────────────────────────────────────────────────
        [InputParameter("Demand fill", 10)]
        public Color DemandFill = Color.FromArgb(50, 0, 200, 80);

        [InputParameter("Demand border", 11)]
        public Color DemandBorder = Color.FromArgb(180, 0, 200, 80);

        [InputParameter("Supply fill", 12)]
        public Color SupplyFill = Color.FromArgb(50, 220, 60, 60);

        [InputParameter("Supply border", 13)]
        public Color SupplyBorder = Color.FromArgb(180, 220, 80, 80);

        // ── Internal zone record ──────────────────────────────────────────────
        private struct Zone
        {
            public bool   IsDemand;
            public string Formation;   // RBR / DBR / RBD / DBD
            public double BodyHi, BodyLo;
            public double WickHi, WickLo;
            public int    StartIdx, EndIdx;  // bar indices (SeekOriginHistory.Begin)
        }

        private List<Zone> _zones = new List<Zone>();

        public IOF_ZoneOnly()
        {
            Name           = "IOF Zone Only";
            ShortName      = "IOF-Z";
            Description    = "IBI demand/supply zones. No scoring, no signals — zones only.";
            IsOverlay      = true;
            SeparateWindow = false;
        }

        protected override void OnInit() => _zones = new List<Zone>();

        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason != UpdateReason.BarClose &&
                args.Reason != UpdateReason.HistoricalBar) return;
            _zones = Scan();
        }

        // ── SCAN ──────────────────────────────────────────────────────────────
        private List<Zone> Scan()
        {
            var result = new List<Zone>();
            if (this.HistoricalData == null) return result;
            int total = this.HistoricalData.Count;
            if (total < 4) return result;

            int firstBar = Math.Max(2, total - LookbackBars);

            for (int endIdx = firstBar; endIdx < total - 2; endIdx++)
            {
                for (int baseLen = 1; baseLen <= MaxBaseCandles; baseLen++)
                {
                    int startIdx = endIdx - baseLen + 1;
                    if (startIdx < 1) continue;
                    if (endIdx + 1 >= total) continue;

                    if (!IsValidBase(startIdx, endIdx)) continue;

                    LegDir legIn  = ClassifyLeg(startIdx - 1);
                    if (legIn  == LegDir.None) continue;
                    LegDir legOut = ClassifyLeg(endIdx + 1);
                    if (legOut == LegDir.None) continue;

                    string formation = ToFormation(legIn, legOut);
                    if (formation == null) continue;
                    bool isDemand = formation == "RBR" || formation == "DBR";

                    double bodyHi = double.MinValue, bodyLo = double.MaxValue;
                    double wickHi = double.MinValue, wickLo = double.MaxValue;
                    for (int i = startIdx; i <= endIdx; i++)
                    {
                        var b = Bar(i); if (b == null) continue;
                        double bh = Math.Max(b.Open, b.Close);
                        double bl = Math.Min(b.Open, b.Close);
                        if (bh > bodyHi) bodyHi = bh;
                        if (bl < bodyLo) bodyLo = bl;
                        if (b.High > wickHi) wickHi = b.High;
                        if (b.Low  < wickLo) wickLo = b.Low;
                    }

                    double zoneTop    = isDemand ? bodyHi : wickHi;
                    double zoneBottom = isDemand ? wickLo  : bodyLo;
                    double baseHeight = zoneTop - zoneBottom;
                    if (baseHeight <= 0) continue;

                    double moveOut = MeasureMoveOut(endIdx, total, isDemand);
                    if (moveOut < MinImpulse * baseHeight) continue;

                    // Invalidation check — close past far wick
                    bool active = true;
                    for (int j = endIdx + 1; j < total; j++)
                    {
                        var bj = Bar(j); if (bj == null) continue;
                        bool broken = isDemand ? bj.Close < wickLo : bj.Close > wickHi;
                        if (broken) { active = false; break; }
                    }
                    if (!active) continue;

                    var z = new Zone
                    {
                        IsDemand  = isDemand,
                        Formation = formation,
                        BodyHi    = bodyHi,
                        BodyLo    = bodyLo,
                        WickHi    = isDemand ? bodyHi : wickHi,
                        WickLo    = isDemand ? wickLo  : bodyLo,
                        StartIdx  = startIdx,
                        EndIdx    = endIdx
                    };

                    if (!IsDuplicate(result, z)) result.Add(z);
                }
            }

            return result;
        }

        // ── PAINT ─────────────────────────────────────────────────────────────
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (this.Symbol == null || _zones == null) return;
            var gr  = args.Graphics;
            var win = args.MainWindow;

            try
            {
                foreach (var z in _zones)
                {
                    if (z.IsDemand && !ShowDemand) continue;
                    if (!z.IsDemand && !ShowSupply) continue;
                    DrawZone(gr, win, z);
                }
            }
            catch { }
        }

        private void DrawZone(System.Drawing.Graphics gr, dynamic win, Zone z)
        {
            double top    = z.IsDemand ? z.BodyHi : z.WickHi;
            double bottom = z.IsDemand ? z.WickLo  : z.BodyLo;

            int yT = PY(win, top);
            int yB = PY(win, bottom);
            if (yB <= yT) return;

            int xL = PX(win, z.StartIdx);
            int xR = ExtendRight ? PX(win, 0) : PX(win, z.EndIdx);
            if (xR <= xL) xR = xL + 40;

            int w = xR - xL;
            int h = yB - yT;

            Color fill   = z.IsDemand ? DemandFill   : SupplyFill;
            Color border = z.IsDemand ? DemandBorder  : SupplyBorder;

            using (var bg = new System.Drawing.SolidBrush(fill))
                gr.FillRectangle(bg, xL, yT, w, h);

            using (var bp = new System.Drawing.Pen(border, 1f))
                gr.DrawRectangle(bp, xL, yT, w, h);

            if (ShowLabels)
            {
                string txt = z.Formation;
                using (var f  = new System.Drawing.Font("Arial", 7.5f, System.Drawing.FontStyle.Bold))
                using (var sh = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(160, 0, 0, 0)))
                using (var wh = new System.Drawing.SolidBrush(System.Drawing.Color.White))
                {
                    gr.DrawString(txt, f, sh, xL + 5, yT + 4);
                    gr.DrawString(txt, f, wh, xL + 4, yT + 3);
                }
            }
        }

        // ── HELPERS ───────────────────────────────────────────────────────────
        private enum LegDir { None, Up, Down }

        private bool IsValidBase(int start, int end)
        {
            for (int i = start; i <= end; i++)
            {
                var b = Bar(i); if (b == null) return false;
                double r = b.High - b.Low;
                if (r <= 0) return false;
                if (Math.Abs(b.Close - b.Open) / r > MaxBodyPct) return false;
            }
            return true;
        }

        private LegDir ClassifyLeg(int idx)
        {
            var b = Bar(idx); if (b == null) return LegDir.None;
            double r = b.High - b.Low;
            if (r <= 0) return LegDir.None;
            if (Math.Abs(b.Close - b.Open) / r < MaxBodyPct + 0.05) return LegDir.None;
            if (b.Close > b.Open) return LegDir.Up;
            if (b.Close < b.Open) return LegDir.Down;
            return LegDir.None;
        }

        private static string ToFormation(LegDir legIn, LegDir legOut)
        {
            if (legIn == LegDir.Up   && legOut == LegDir.Up)   return "RBR";
            if (legIn == LegDir.Down && legOut == LegDir.Up)   return "DBR";
            if (legIn == LegDir.Up   && legOut == LegDir.Down) return "RBD";
            if (legIn == LegDir.Down && legOut == LegDir.Down) return "DBD";
            return null;
        }

        private double MeasureMoveOut(int endOfBase, int total, bool isDemand)
        {
            int scanLimit = Math.Min(total - 1, endOfBase + Math.Max(20, LookbackBars / 4));
            var baseBar = Bar(endOfBase); if (baseBar == null) return 0;
            double baseRange = baseBar.High - baseBar.Low;
            if (baseRange <= 0) baseRange = Math.Abs(baseBar.Close - baseBar.Open);

            double extreme = double.NaN;
            for (int i = endOfBase + 1; i <= scanLimit; i++)
            {
                var b = Bar(i); if (b == null) break;
                if (isDemand)
                {
                    if (double.IsNaN(extreme) || b.High > extreme) extreme = b.High;
                    if (b.Close < baseBar.Close - baseRange) break;
                }
                else
                {
                    if (double.IsNaN(extreme) || b.Low < extreme) extreme = b.Low;
                    if (b.Close > baseBar.Close + baseRange) break;
                }
            }
            if (double.IsNaN(extreme)) return 0;
            return isDemand ? (extreme - baseBar.High) : (baseBar.Low - extreme);
        }

        private static bool IsDuplicate(List<Zone> existing, Zone candidate)
        {
            double cTop = candidate.IsDemand ? candidate.BodyHi : candidate.WickHi;
            double cBot = candidate.IsDemand ? candidate.WickLo  : candidate.BodyLo;
            for (int i = 0; i < existing.Count; i++)
            {
                var z = existing[i];
                if (z.IsDemand != candidate.IsDemand) continue;
                double zTop = z.IsDemand ? z.BodyHi : z.WickHi;
                double zBot = z.IsDemand ? z.WickLo  : z.BodyLo;
                double overlap = Math.Min(cTop, zTop) - Math.Max(cBot, zBot);
                if (overlap <= 0) continue;
                double union = Math.Max(cTop, zTop) - Math.Min(cBot, zBot);
                if (union > 0 && overlap / union >= 0.75) return true;
            }
            return false;
        }

        private HistoryItemBar Bar(int idx)
        {
            if (idx < 0 || this.HistoricalData == null ||
                idx >= this.HistoricalData.Count) return null;
            return this.HistoricalData[idx, SeekOriginHistory.Begin] as HistoryItemBar;
        }

        private int PY(dynamic win, double price)
        {
            try { return (int)Math.Round((double)win.CoordinatesConverter.GetChartY(price)); }
            catch { return 0; }
        }

        private int PX(dynamic win, int idx)
        {
            try
            {
                var b = Bar(idx); if (b == null) return 0;
                return (int)Math.Round((double)win.CoordinatesConverter.GetChartX(b.TimeLeft));
            }
            catch { return 0; }
        }

        protected override void OnClear() => _zones?.Clear();
    }
}
