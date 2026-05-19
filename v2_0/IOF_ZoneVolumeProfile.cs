// IOF_ZoneVolumeProfile.cs — Standalone Zone Volume Profile
// Drop this ONE file into its OWN subfolder, e.g.:
//   C:\Quantower\Settings\Scripts\Indicators\ZVP\IOF_ZoneVolumeProfile.cs
// Do NOT mix it with other IOF indicator files.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace ZoneVolumeProfile
{
    public class IOF_ZoneVolumeProfile : Indicator
    {
        // ── Detection ─────────────────────────────────────────────────────────
        [InputParameter("Lookback bars", 1, 50, 5000, 50, 0)]
        public int LookbackBars = 500;

        [InputParameter("Max base candles", 2, 1, 20, 1, 0)]
        public int MaxBaseCandles = 7;

        [InputParameter("Base body % max", 3, 0.05, 1.0, 0.05, 2)]
        public double MaxBodyPct = 0.50;

        [InputParameter("Min impulse ratio", 4, 1.0, 10.0, 0.5, 1)]
        public double MinImpulse = 2.0;

        // ── Display ───────────────────────────────────────────────────────────
        [InputParameter("Max zones per direction (1-5)", 5, 1, 5, 1, 0)]
        public int MaxZones = 2;

        [InputParameter("Volume bar width % of zone", 6, 10, 100, 5, 0)]
        public int BarWidthPct = 60;

        [InputParameter("Show HVN line", 7)]
        public bool ShowHvn = true;

        [InputParameter("Show labels", 8)]
        public bool ShowLabels = true;

        [InputParameter("Show demand", 9)]
        public bool ShowDemand = true;

        [InputParameter("Show supply", 10)]
        public bool ShowSupply = true;

        // ── Colors ────────────────────────────────────────────────────────────
        [InputParameter("Demand fill", 20)]
        public Color DemandFill = Color.FromArgb(40, 0, 200, 80);

        [InputParameter("Demand volume", 21)]
        public Color DemandVol = Color.FromArgb(170, 0, 220, 100);

        [InputParameter("Supply fill", 22)]
        public Color SupplyFill = Color.FromArgb(40, 220, 60, 60);

        [InputParameter("Supply volume", 23)]
        public Color SupplyVol = Color.FromArgb(170, 220, 80, 80);

        [InputParameter("HVN color", 24)]
        public Color HvnCol = Color.FromArgb(220, 255, 215, 0);

        // ── Zone record ───────────────────────────────────────────────────────
        private struct Z
        {
            public bool   Demand;
            public double Top, Bottom;   // price bounds (draw box)
            public double WickHi, WickLo;
            public int    S, E;          // bar indices: S=older(higher), E=newer(lower)
        }

        private List<Z> _demand = new List<Z>();
        private List<Z> _supply = new List<Z>();

        public IOF_ZoneVolumeProfile()
        {
            Name           = "IOF Zone Volume Profile";
            ShortName      = "IOF-ZVP";
            Description    = "Volume profile inside IOF demand/supply zones. Single file, no dependencies.";
            IsOverlay      = true;
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            ShortName = "IOF-ZVP";
            _demand   = new List<Z>();
            _supply   = new List<Z>();
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason != UpdateReason.BarClose &&
                args.Reason != UpdateReason.HistoricalBar) return;
            Rescan();
        }

        // ── ZONE DETECTION ────────────────────────────────────────────────────
        private void Rescan()
        {
            var all    = Scan();
            double mid = MidPrice();

            _demand = all.Where(z => z.Demand)
                        .OrderBy(z => Math.Abs(mid - (z.Top + z.Bottom) / 2.0))
                        .Take(MaxZones).ToList();

            _supply = all.Where(z => !z.Demand)
                        .OrderBy(z => Math.Abs(mid - (z.Top + z.Bottom) / 2.0))
                        .Take(MaxZones).ToList();
        }

        private List<Z> Scan()
        {
            var result = new List<Z>();
            int total  = this.HistoricalData?.Count ?? 0;
            if (total < 6) return result;

            int limit = Math.Min(total - 2, LookbackBars);

            for (int i = limit; i >= 2; i--)
            {
                if (!IsBase(i)) continue;

                // find cluster end (newer = lower index)
                int ce = i, cnt = 1;
                while (ce - 1 >= 1 && cnt < MaxBaseCandles && IsBase(ce - 1))
                { ce--; cnt++; }

                int inBar  = i + 1;   // impulse-in  (older)
                int outBar = ce - 1;  // impulse-out (newer)
                if (inBar >= total || outBar < 0) continue;
                if (!IsImpulse(inBar) || !IsImpulse(outBar)) continue;

                // cluster geometry
                double hi = double.MinValue, lo = double.MaxValue;
                double bHi = double.MinValue, bLo = double.MaxValue;
                for (int b = ce; b <= i; b++)
                {
                    var bar = Bar(b); if (bar == null) continue;
                    if (bar.High > hi)  hi  = bar.High;
                    if (bar.Low  < lo)  lo  = bar.Low;
                    double op = Math.Max(bar.Open, bar.Close);
                    double cl = Math.Min(bar.Open, bar.Close);
                    if (op > bHi) bHi = op;
                    if (cl < bLo) bLo = cl;
                }
                if (hi <= lo) continue;
                double ht = hi - lo;

                var ob = Bar(outBar); if (ob == null) continue;
                bool demand  = ob.Close > ob.Open;
                double moveOut = demand ? ob.Close - hi : lo - ob.Close;
                if (moveOut < MinImpulse * ht) continue;

                // active check
                bool active = true;
                for (int j = ce - 1; j >= 0; j--)
                {
                    var bj = Bar(j); if (bj == null) continue;
                    if (demand  && bj.Close < lo) { active = false; break; }
                    if (!demand && bj.Close > hi) { active = false; break; }
                }
                if (!active) continue;

                result.Add(new Z
                {
                    Demand  = demand,
                    Top     = demand ? bHi : hi,
                    Bottom  = demand ? lo  : bLo,
                    WickHi  = hi,
                    WickLo  = lo,
                    S       = i,
                    E       = ce
                });

                i = ce; // skip past this cluster
            }

            return result;
        }

        private bool IsBase(int idx)
        {
            var b = Bar(idx); if (b == null) return false;
            double r = b.High - b.Low;
            return r > 0 && Math.Abs(b.Close - b.Open) / r <= MaxBodyPct;
        }

        private bool IsImpulse(int idx)
        {
            var b = Bar(idx); if (b == null) return false;
            double r = b.High - b.Low;
            return r > 0 && Math.Abs(b.Close - b.Open) / r > 0.55;
        }

        private HistoryItemBar Bar(int idx)
        {
            if (idx < 0 || this.HistoricalData == null ||
                idx >= this.HistoricalData.Count) return null;
            return this.HistoricalData[idx, SeekOriginHistory.Begin] as HistoryItemBar;
        }

        // ── PAINT ─────────────────────────────────────────────────────────────
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (this.Symbol == null) return;
            double tick = this.Symbol.TickSize;
            if (tick <= 0) return;

            var gr  = args.Graphics;
            var win = args.MainWindow;

            try
            {
                if (ShowDemand) foreach (var z in _demand) Paint(gr, win, z, tick);
                if (ShowSupply) foreach (var z in _supply) Paint(gr, win, z, tick);
            }
            catch { }
        }

        private void Paint(Graphics gr, dynamic win, Z z, double tick)
        {
            int yT = PY(win, z.Top);
            int yB = PY(win, z.Bottom);
            if (yB <= yT) return;

            int xL = PX(win, z.S); // zone left = older (higher index)
            int xR = PX(win, 0);   // extend to current bar
            if (xR <= xL) xR = xL + 40;

            int zW = xR - xL;
            int zH = yB - yT;

            // zone background
            using (var bg = new SolidBrush(z.Demand ? DemandFill : SupplyFill))
                gr.FillRectangle(bg, xL, yT, zW, zH);

            // zone border
            using (var bp = new Pen(Color.FromArgb(100, 180, 180, 180), 1f))
                gr.DrawRectangle(bp, xL, yT, zW, zH);

            // volume profile
            var prof = VolumeProfile(z.E, z.S, z.Bottom, z.Top, tick);
            if (prof == null || prof.Count == 0) { Label(gr, xL, yT, z, null); return; }

            double maxV   = prof.Values.Max();
            if (maxV <= 0) { Label(gr, xL, yT, z, null); return; }

            double hvnPx  = prof.OrderByDescending(kv => kv.Value).First().Key;
            int    maxPx  = Math.Max(2, zW * BarWidthPct / 100);
            Color  vc     = z.Demand ? DemandVol : SupplyVol;

            foreach (var kv in prof)
            {
                bool hvn  = ShowHvn && Math.Abs(kv.Key - hvnPx) < tick * 0.5;
                int  bw   = Math.Max(1, (int)(maxPx * kv.Value / maxV));
                int  yBT  = Math.Max(yT, PY(win, kv.Key + tick));
                int  yBB  = Math.Min(yB, PY(win, kv.Key));
                int  bh   = Math.Max(1, yBB - yBT);
                using (var b = new SolidBrush(hvn ? HvnCol : vc))
                    gr.FillRectangle(b, xL, yBT, bw, bh);
            }

            if (ShowHvn)
            {
                int yHvn = PY(win, hvnPx);
                using (var p = new Pen(HvnCol, 1f) { DashStyle = DashStyle.Dash })
                    gr.DrawLine(p, xL, yHvn, xR, yHvn);
            }

            Label(gr, xL, yT, z, hvnPx);
        }

        private void Label(Graphics gr, int xL, int yT, Z z, double? hvn)
        {
            if (!ShowLabels) return;
            string txt = (z.Demand ? "D" : "S") + (hvn.HasValue ? $" HVN {hvn.Value:F2}" : "");
            using (var f  = new Font("Arial", 7.5f, FontStyle.Bold))
            using (var sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
            using (var wh = new SolidBrush(Color.White))
            {
                gr.DrawString(txt, f, sh, xL + 5, yT + 4);
                gr.DrawString(txt, f, wh, xL + 4, yT + 3);
            }
        }

        // ── VOLUME PROFILE ────────────────────────────────────────────────────
        private Dictionary<double, double> VolumeProfile(
            int from, int to, double pLo, double pHi, double tick)
        {
            var d = new Dictionary<double, double>();
            if (this.HistoricalData == null) return d;

            // init price bins
            for (double p = Math.Round(pLo / tick) * tick; p <= pHi; p += tick)
            {
                double k = Math.Round(p / tick) * tick;
                if (!d.ContainsKey(k)) d[k] = 0;
            }

            // from = newer (lower index), to = older (higher index)
            int end = Math.Min(to, this.HistoricalData.Count - 1);
            for (int i = from; i <= end; i++)
            {
                var b = Bar(i);
                if (b == null || b.Volume <= 0) continue;
                double bH = Math.Min(b.High, pHi);
                double bL = Math.Max(b.Low,  pLo);
                if (bH <= bL) continue;
                double rng = bH - bL;
                foreach (var k in d.Keys.ToArray())
                {
                    double ov = Math.Min(k + tick, bH) - Math.Max(k, bL);
                    if (ov > 0) d[k] += b.Volume * ov / rng;
                }
            }

            foreach (var k in d.Keys.Where(k => d[k] <= 0).ToArray()) d.Remove(k);
            return d;
        }

        // ── HELPERS ───────────────────────────────────────────────────────────
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

        private double MidPrice()
        {
            try { return this.Symbol?.Last > 0 ? this.Symbol.Last : Bar(0)?.Close ?? 0; }
            catch { return 0; }
        }

        protected override void OnClear()
        {
            _demand?.Clear();
            _supply?.Clear();
        }
    }
}
