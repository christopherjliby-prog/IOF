// IOF_GlobexTrap.cs — Globex Trap Confluence Indicator
// Drop this ONE file into its OWN subfolder, e.g.:
//   C:\Quantower\Settings\Scripts\Indicators\GlobexTrap\IOF_GlobexTrap.cs
// Do NOT mix with other IOF indicator files.
//
// Tracks Asia and London session highs/lows. Identifies 15m/1h/4h IOF zones
// just above/below those levels. Highlights yellow (MTFC) when 2+ timeframes
// agree. The trap fires when NY open pushes through the Asia/London level
// into the highlighted zone — fade the push.
//
// Add to a 5m chart. Sessions default to UTC (MNQ summer, adjust for winter).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace GlobexTrap
{
    public class IOF_GlobexTrap : Indicator
    {
        // ── IBI Detection ──────────────────────────────────────────────────────
        [InputParameter("Lookback bars", 1, 50, 2000, 50, 0)]
        public int LookbackBars = 300;

        [InputParameter("Max base candles", 2, 1, 7, 1, 0)]
        public int MaxBaseCandles = 7;

        [InputParameter("Base body % max", 3, 0.05, 1.0, 0.05, 2)]
        public double MaxBodyPct = 0.50;

        [InputParameter("Min impulse ratio", 4, 1.0, 10.0, 0.5, 1)]
        public double MinImpulse = 2.0;

        [InputParameter("Zone proximity to session level (ticks)", 5, 1, 200, 5, 0)]
        public int ProximityTicks = 30;

        // ── Session hours (UTC) ────────────────────────────────────────────────
        // MNQ summer defaults. Winter: add 1hr to each.
        [InputParameter("Asia session start (UTC hour)", 10, 0, 23, 1, 0)]
        public int AsiaStartHour = 0;   // midnight UTC

        [InputParameter("Asia session end (UTC hour)", 11, 0, 23, 1, 0)]
        public int AsiaEndHour = 7;     // 7 AM UTC

        [InputParameter("London session start (UTC hour)", 12, 0, 23, 1, 0)]
        public int LondonStartHour = 7; // 7 AM UTC

        [InputParameter("London session end (UTC hour)", 13, 0, 23, 1, 0)]
        public int LondonEndHour = 14;  // 2 PM UTC (just before NY 14:30)

        [InputParameter("NY open hour (UTC)", 14, 0, 23, 1, 0)]
        public int NyOpenHour = 14;

        [InputParameter("NY open minute (UTC)", 15, 0, 59, 1, 0)]
        public int NyOpenMinute = 30;

        // ── Display ────────────────────────────────────────────────────────────
        [InputParameter("Show Asia levels", 20)]
        public bool ShowAsia = true;

        [InputParameter("Show London levels", 21)]
        public bool ShowLondon = true;

        [InputParameter("Show 15m zones", 22)]
        public bool Show15m = true;

        [InputParameter("Show 1h zones", 23)]
        public bool Show1h = true;

        [InputParameter("Show 4h zones", 24)]
        public bool Show4h = true;

        [InputParameter("Show labels", 25)]
        public bool ShowLabels = true;

        // ── Colors ─────────────────────────────────────────────────────────────
        [InputParameter("Asia level color", 30)]
        public Color AsiaColor = Color.FromArgb(200, 180, 220, 255);

        [InputParameter("London level color", 31)]
        public Color LondonColor = Color.FromArgb(200, 255, 200, 100);

        [InputParameter("15m fill", 32)]
        public Color Fill15m = Color.FromArgb(35, 100, 149, 237);

        [InputParameter("15m border", 33)]
        public Color Border15m = Color.FromArgb(130, 100, 149, 237);

        [InputParameter("1h fill", 34)]
        public Color Fill1h = Color.FromArgb(50, 255, 165, 0);

        [InputParameter("1h border", 35)]
        public Color Border1h = Color.FromArgb(160, 255, 165, 0);

        [InputParameter("4h fill", 36)]
        public Color Fill4h = Color.FromArgb(65, 148, 0, 211);

        [InputParameter("4h border", 37)]
        public Color Border4h = Color.FromArgb(180, 148, 0, 211);

        [InputParameter("MTFC highlight fill", 38)]
        public Color MtfcFill = Color.FromArgb(110, 255, 215, 0);

        [InputParameter("MTFC border", 39)]
        public Color MtfcBorder = Color.FromArgb(230, 255, 215, 0);

        // ── Internal types ──────────────────────────────────────────────────────
        private struct Zone
        {
            public bool     IsDemand;
            public string   Formation;
            public string   Tier;
            public double   Top, Bottom;
            public double   BodyHi, BodyLo, WickHi, WickLo;
            public DateTime StartTime;
            public int      StartIdx, EndIdx;
        }

        private struct SessionLevel
        {
            public double   High, Low;
            public string   Name;   // "Asia" or "London"
            public DateTime Date;
        }

        private struct MtfcCluster
        {
            public double  Top, Bottom;
            public bool    IsDemand;
            public int     TfCount;
            public string  Label;
        }

        // ── State ──────────────────────────────────────────────────────────────
        private HistoricalData      _h15m, _h1h, _h4h;
        private List<Zone>          _zones15m = new List<Zone>();
        private List<Zone>          _zones1h  = new List<Zone>();
        private List<Zone>          _zones4h  = new List<Zone>();
        private List<SessionLevel>  _levels   = new List<SessionLevel>();
        private List<MtfcCluster>   _clusters = new List<MtfcCluster>();

        public IOF_GlobexTrap()
        {
            Name           = "IOF Globex Trap";
            ShortName      = "IOF-GT";
            Description    = "Asia/London session levels + 15m/1h/4h IOF zones. MTFC = yellow.";
            IsOverlay      = true;
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            _zones15m = new List<Zone>();
            _zones1h  = new List<Zone>();
            _zones4h  = new List<Zone>();
            _levels   = new List<SessionLevel>();
            _clusters = new List<MtfcCluster>();

            if (this.Symbol == null) return;
            _h15m = this.Symbol.GetHistory(Period.MIN15, DateTime.UtcNow.AddDays(-45));
            _h1h  = this.Symbol.GetHistory(Period.HOUR1, DateTime.UtcNow.AddDays(-90));
            _h4h  = this.Symbol.GetHistory(Period.HOUR4, DateTime.UtcNow.AddDays(-180));
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason != UpdateReason.BarClose &&
                args.Reason != UpdateReason.HistoricalBar) return;
            Rescan();
        }

        // ── RESCAN ─────────────────────────────────────────────────────────────
        private void Rescan()
        {
            _levels = ComputeSessionLevels();

            double tick = (this.Symbol?.TickSize > 0) ? this.Symbol.TickSize : 0.25;
            double prox = ProximityTicks * tick;

            _zones15m = ScanTF(_h15m, "15m");
            _zones1h  = ScanTF(_h1h,  "1h");
            _zones4h  = ScanTF(_h4h,  "4h");

            _clusters = BuildClusters(prox);
        }

        // ── SESSION LEVELS ──────────────────────────────────────────────────────
        private List<SessionLevel> ComputeSessionLevels()
        {
            var result = new List<SessionLevel>();
            if (this.HistoricalData == null) return result;

            int total = this.HistoricalData.Count;

            // Accumulate Asia and London H/L per calendar date (UTC)
            var asia   = new Dictionary<DateTime, (double hi, double lo)>();
            var london = new Dictionary<DateTime, (double hi, double lo)>();

            for (int i = 0; i < total; i++)
            {
                var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null) continue;

                DateTime utc  = bar.TimeLeft.ToUniversalTime();
                DateTime date = utc.Date;
                int      hr   = utc.Hour;

                bool isAsia   = hr >= AsiaStartHour   && hr < AsiaEndHour;
                bool isLondon = hr >= LondonStartHour  && hr < LondonEndHour;

                if (isAsia)
                {
                    if (!asia.TryGetValue(date, out var cur))
                        asia[date] = (bar.High, bar.Low);
                    else
                        asia[date] = (Math.Max(cur.hi, bar.High), Math.Min(cur.lo, bar.Low));
                }
                else if (isLondon)
                {
                    if (!london.TryGetValue(date, out var cur))
                        london[date] = (bar.High, bar.Low);
                    else
                        london[date] = (Math.Max(cur.hi, bar.High), Math.Min(cur.lo, bar.Low));
                }
            }

            // Last 5 trading days
            foreach (var kv in asia.OrderByDescending(x => x.Key).Take(5))
                result.Add(new SessionLevel
                    { Name = "Asia", Date = kv.Key, High = kv.Value.hi, Low = kv.Value.lo });

            foreach (var kv in london.OrderByDescending(x => x.Key).Take(5))
                result.Add(new SessionLevel
                    { Name = "London", Date = kv.Key, High = kv.Value.hi, Low = kv.Value.lo });

            return result;
        }

        // ── IBI ZONE SCAN ───────────────────────────────────────────────────────
        private List<Zone> ScanTF(HistoricalData data, string tier)
        {
            var result = new List<Zone>();
            if (data == null || data.Count < 4) return result;

            int total    = data.Count;
            int firstBar = Math.Max(2, total - LookbackBars);

            for (int endIdx = firstBar; endIdx < total - 2; endIdx++)
            {
                for (int baseLen = 1; baseLen <= MaxBaseCandles; baseLen++)
                {
                    int startIdx = endIdx - baseLen + 1;
                    if (startIdx < 1 || endIdx + 1 >= total) continue;
                    if (!IsValidBase(data, startIdx, endIdx)) continue;

                    LegDir legIn  = ClassifyLeg(data, startIdx - 1);
                    if (legIn == LegDir.None) continue;
                    LegDir legOut = ClassifyLeg(data, endIdx + 1);
                    if (legOut == LegDir.None) continue;

                    string fm = ToFormation(legIn, legOut);
                    if (fm == null) continue;
                    bool isDemand = fm == "RBR" || fm == "DBR";

                    double bodyHi = double.MinValue, bodyLo = double.MaxValue;
                    double wickHi = double.MinValue, wickLo = double.MaxValue;
                    DateTime startTime = DateTime.MinValue;

                    for (int i = startIdx; i <= endIdx; i++)
                    {
                        var b = GetBar(data, i); if (b == null) continue;
                        double bh = Math.Max(b.Open, b.Close);
                        double bl = Math.Min(b.Open, b.Close);
                        if (bh > bodyHi) bodyHi = bh;
                        if (bl < bodyLo) bodyLo = bl;
                        if (b.High > wickHi) wickHi = b.High;
                        if (b.Low  < wickLo) wickLo  = b.Low;
                        if (i == startIdx) startTime = b.TimeLeft;
                    }

                    double zTop    = isDemand ? bodyHi : wickHi;
                    double zBottom = isDemand ? wickLo  : bodyLo;
                    if (zTop - zBottom <= 0) continue;

                    double moveOut = MeasureMoveOut(data, endIdx, total, isDemand);
                    if (moveOut < MinImpulse * (zTop - zBottom)) continue;

                    bool active = true;
                    for (int j = endIdx + 1; j < total; j++)
                    {
                        var bj = GetBar(data, j); if (bj == null) continue;
                        bool broken = isDemand ? bj.Close < wickLo : bj.Close > wickHi;
                        if (broken) { active = false; break; }
                    }
                    if (!active) continue;

                    var z = new Zone
                    {
                        IsDemand  = isDemand, Formation = fm, Tier = tier,
                        BodyHi    = bodyHi, BodyLo = bodyLo,
                        WickHi    = isDemand ? bodyHi : wickHi,
                        WickLo    = isDemand ? wickLo  : bodyLo,
                        Top       = zTop, Bottom = zBottom,
                        StartTime = startTime, StartIdx = startIdx, EndIdx = endIdx
                    };

                    if (!IsDuplicate(result, z)) result.Add(z);
                }
            }
            return result;
        }

        // ── MTFC CLUSTERS ───────────────────────────────────────────────────────
        private List<MtfcCluster> BuildClusters(double prox)
        {
            var clusters = new List<MtfcCluster>();
            if (_levels == null || _levels.Count == 0) return clusters;

            var all = new List<(Zone z, int bit)>();
            foreach (var z in _zones15m) all.Add((z, 1));
            foreach (var z in _zones1h)  all.Add((z, 2));
            foreach (var z in _zones4h)  all.Add((z, 4));

            // Use latest level per session name to avoid duplicates
            var latestAsia   = _levels.Where(l => l.Name == "Asia")
                                      .OrderByDescending(l => l.Date).FirstOrDefault();
            var latestLondon = _levels.Where(l => l.Name == "London")
                                      .OrderByDescending(l => l.Date).FirstOrDefault();

            var checkLevels = new List<(double price, bool isHigh, string sessionName)>();

            if (latestAsia.Date != default)
            {
                checkLevels.Add((latestAsia.High, true,  "Asia"));
                checkLevels.Add((latestAsia.Low,  false, "Asia"));
            }
            if (latestLondon.Date != default)
            {
                checkLevels.Add((latestLondon.High, true,  "London"));
                checkLevels.Add((latestLondon.Low,  false, "London"));
            }

            foreach (var (levelPrice, isHigh, sessionName) in checkLevels)
            {
                // isHigh → expect supply zone just above → trap short
                // !isHigh → expect demand zone just below → trap long
                bool isDemand = !isHigh;

                var near = all.Where(x =>
                {
                    if (x.z.IsDemand != isDemand) return false;
                    if (isDemand)
                        // demand zone top near the session low
                        return x.z.Top >= levelPrice - prox * 4 &&
                               x.z.Top <= levelPrice + prox;
                    else
                        // supply zone bottom near the session high
                        return x.z.Bottom >= levelPrice - prox &&
                               x.z.Bottom <= levelPrice + prox * 4;
                }).ToList();

                if (near.Count == 0) continue;

                int tfMask = near.Aggregate(0, (acc, x) => acc | x.bit);
                int tfCount = CountBits(tfMask);
                if (tfCount < 2) continue;

                double top    = near.Min(x => x.z.Top);
                double bottom = near.Max(x => x.z.Bottom);
                if (top <= bottom) continue;

                string tfs = "";
                if ((tfMask & 1) > 0) tfs += "15m";
                if ((tfMask & 2) > 0) tfs += (tfs.Length > 0 ? "+" : "") + "1H";
                if ((tfMask & 4) > 0) tfs += (tfs.Length > 0 ? "+" : "") + "4H";

                clusters.Add(new MtfcCluster
                {
                    Top      = top,
                    Bottom   = bottom,
                    IsDemand = isDemand,
                    TfCount  = tfCount,
                    Label    = $"{sessionName} {(isDemand ? "TRAP LONG" : "TRAP SHORT")} [{tfs}]"
                });
            }

            return clusters;
        }

        private static int CountBits(int v)
        { int c = 0; while (v > 0) { c += v & 1; v >>= 1; } return c; }

        // ── PAINT ───────────────────────────────────────────────────────────────
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (this.Symbol == null) return;
            var gr  = args.Graphics;
            var win = args.MainWindow;
            try
            {
                if (Show4h)  DrawZones(gr, win, _zones4h,  Fill4h,  Border4h,  "4H");
                if (Show1h)  DrawZones(gr, win, _zones1h,  Fill1h,  Border1h,  "1H");
                if (Show15m) DrawZones(gr, win, _zones15m, Fill15m, Border15m, "15");
                DrawSessionLines(gr, win);
                DrawClusters(gr, win);
            }
            catch { }
        }

        private void DrawSessionLines(Graphics gr, dynamic win)
        {
            if (_levels == null) return;
            var rect   = (System.Drawing.Rectangle)win.ClientRectangle;
            int xLeft  = rect.Left;
            int xRight = rect.Right;

            foreach (var lvl in _levels)
            {
                if (lvl.Name == "Asia"   && !ShowAsia)   continue;
                if (lvl.Name == "London" && !ShowLondon) continue;

                Color col = lvl.Name == "Asia" ? AsiaColor : LondonColor;

                int yH = PY(win, lvl.High);
                int yL = PY(win, lvl.Low);

                var dash = lvl.Name == "Asia" ? DashStyle.Dash : DashStyle.DashDot;

                using (var p = new Pen(col, 1f) { DashStyle = dash })
                {
                    gr.DrawLine(p, xLeft, yH, xRight, yH);
                    gr.DrawLine(p, xLeft, yL, xRight, yL);
                }

                if (ShowLabels)
                {
                    using (var f  = new Font("Arial", 7f, FontStyle.Bold))
                    using (var br = new SolidBrush(col))
                    {
                        gr.DrawString($"{lvl.Name} H {lvl.High:F2}", f, br, xLeft + 4, yH - 13);
                        gr.DrawString($"{lvl.Name} L {lvl.Low:F2}",  f, br, xLeft + 4, yL + 2);
                    }
                }
            }
        }

        private void DrawZones(Graphics gr, dynamic win,
            List<Zone> zones, Color fill, Color border, string tierLabel)
        {
            if (zones == null) return;
            foreach (var z in zones)
            {
                int yT = PY(win, z.Top);
                int yB = PY(win, z.Bottom);
                if (yB <= yT) continue;

                int xL = PXTime(win, z.StartTime);
                int xR = PX(win, 0);
                if (xR <= xL) xR = xL + 40;

                using (var bg = new SolidBrush(fill))
                    gr.FillRectangle(bg, xL, yT, xR - xL, yB - yT);
                using (var bp = new Pen(border, 1f))
                    gr.DrawRectangle(bp, xL, yT, xR - xL, yB - yT);

                if (ShowLabels)
                {
                    string txt = $"{tierLabel} {z.Formation}";
                    using (var f  = new Font("Arial", 7f, FontStyle.Bold))
                    using (var sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                    using (var wh = new SolidBrush(Color.White))
                    {
                        gr.DrawString(txt, f, sh, xL + 5, yT + 4);
                        gr.DrawString(txt, f, wh, xL + 4, yT + 3);
                    }
                }
            }
        }

        private void DrawClusters(Graphics gr, dynamic win)
        {
            if (_clusters == null) return;
            var rect = (System.Drawing.Rectangle)win.ClientRectangle;

            foreach (var c in _clusters)
            {
                int yT = PY(win, c.Top);
                int yB = PY(win, c.Bottom);
                if (yB <= yT) continue;

                int xL = rect.Left;
                int xR = rect.Right;

                using (var bg = new SolidBrush(MtfcFill))
                    gr.FillRectangle(bg, xL, yT, xR - xL, yB - yT);
                using (var bp = new Pen(MtfcBorder, 2f))
                    gr.DrawRectangle(bp, xL, yT, xR - xL, yB - yT);

                if (ShowLabels)
                {
                    string stars = new string('★', c.TfCount);
                    string txt   = $"⚡ {c.Label} {stars}";
                    using (var f  = new Font("Arial", 8f, FontStyle.Bold))
                    using (var sh = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                    using (var yl = new SolidBrush(Color.FromArgb(255, 255, 215, 0)))
                    {
                        gr.DrawString(txt, f, sh, xL + 9, yT + 5);
                        gr.DrawString(txt, f, yl, xL + 8, yT + 4);
                    }
                }
            }
        }

        // ── IBI HELPERS ─────────────────────────────────────────────────────────
        private enum LegDir { None, Up, Down }

        private bool IsValidBase(HistoricalData data, int start, int end)
        {
            for (int i = start; i <= end; i++)
            {
                var b = GetBar(data, i); if (b == null) return false;
                double r = b.High - b.Low;
                if (r <= 0 || Math.Abs(b.Close - b.Open) / r > MaxBodyPct) return false;
            }
            return true;
        }

        private LegDir ClassifyLeg(HistoricalData data, int idx)
        {
            var b = GetBar(data, idx); if (b == null) return LegDir.None;
            double r = b.High - b.Low;
            if (r <= 0 || Math.Abs(b.Close - b.Open) / r < MaxBodyPct + 0.05) return LegDir.None;
            return b.Close > b.Open ? LegDir.Up : b.Close < b.Open ? LegDir.Down : LegDir.None;
        }

        private static string ToFormation(LegDir i, LegDir o)
        {
            if (i == LegDir.Up   && o == LegDir.Up)   return "RBR";
            if (i == LegDir.Down && o == LegDir.Up)   return "DBR";
            if (i == LegDir.Up   && o == LegDir.Down) return "RBD";
            if (i == LegDir.Down && o == LegDir.Down) return "DBD";
            return null;
        }

        private double MeasureMoveOut(HistoricalData data, int endOfBase,
            int total, bool isDemand)
        {
            int limit = Math.Min(total - 1, endOfBase + Math.Max(20, LookbackBars / 4));
            var bb    = GetBar(data, endOfBase); if (bb == null) return 0;
            double rng = bb.High - bb.Low;
            if (rng <= 0) rng = Math.Abs(bb.Close - bb.Open);
            double ext = double.NaN;
            for (int i = endOfBase + 1; i <= limit; i++)
            {
                var b = GetBar(data, i); if (b == null) break;
                if (isDemand)
                {
                    if (double.IsNaN(ext) || b.High > ext) ext = b.High;
                    if (b.Close < bb.Close - rng) break;
                }
                else
                {
                    if (double.IsNaN(ext) || b.Low < ext) ext = b.Low;
                    if (b.Close > bb.Close + rng) break;
                }
            }
            if (double.IsNaN(ext)) return 0;
            return isDemand ? (ext - bb.High) : (bb.Low - ext);
        }

        private static bool IsDuplicate(List<Zone> existing, Zone candidate)
        {
            foreach (var z in existing)
            {
                if (z.IsDemand != candidate.IsDemand) continue;
                double overlap = Math.Min(candidate.Top, z.Top) - Math.Max(candidate.Bottom, z.Bottom);
                if (overlap <= 0) continue;
                double union = Math.Max(candidate.Top, z.Top) - Math.Min(candidate.Bottom, z.Bottom);
                if (union > 0 && overlap / union >= 0.75) return true;
            }
            return false;
        }

        // ── COORD HELPERS ────────────────────────────────────────────────────────
        private static HistoryItemBar GetBar(HistoricalData data, int idx)
        {
            if (data == null || idx < 0 || idx >= data.Count) return null;
            return data[idx, SeekOriginHistory.Begin] as HistoryItemBar;
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
                var b = this.HistoricalData?[idx, SeekOriginHistory.Begin] as HistoryItemBar;
                if (b == null) return 0;
                return (int)Math.Round((double)win.CoordinatesConverter.GetChartX(b.TimeLeft));
            }
            catch { return 0; }
        }

        private int PXTime(dynamic win, DateTime time)
        {
            try { return (int)Math.Round((double)win.CoordinatesConverter.GetChartX(time)); }
            catch { return 0; }
        }

        protected override void OnClear()
        {
            _zones15m?.Clear();
            _zones1h?.Clear();
            _zones4h?.Clear();
            _levels?.Clear();
            _clusters?.Clear();
        }
    }
}
