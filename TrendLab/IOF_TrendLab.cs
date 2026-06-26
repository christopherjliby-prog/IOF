// IOF_TrendLab.cs
// Multi-timeframe trend dashboard + current-chart structure drawing.
// Mr. Black leg-based methodology: 3-segment structures, body closes only.
// Dual control points: ControllingHigh (bull HH) + ControllingLow (bear LL).
// FLAT when price is between both. No direct Bull→Bear flip.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TradePhantomsIOF.Trend;
using TradingPlatform.BusinessLayer;

namespace IOF_TrendLab
{
    public class IOF_TrendLab : Indicator
    {
        // ── Trend Logic Settings ───────────────────────────────────────────

        [InputParameter("Swing Fractal Lookback", 0, 1, 10, 1, 0)]
        public int SwingLookback = 3;

        [InputParameter("Required Segments (3 = Mr. Black default)", 1, 2, 6, 1, 0)]
        public int RequireSegments = 3;

        [InputParameter("Require Engulfing for Control Point", 2)]
        public bool RequireEngulfing = false;

        [InputParameter("Body-Close Tick Tolerance", 3, 0, 10, 1, 0)]
        public int BodyCloseTolerance = 0;

        [InputParameter("Min Leg Size — 4H (ticks)", 5, 1, 200, 1, 0)]
        public int MinLegTicks_4H = 40;

        [InputParameter("Min Leg Size — 1H (ticks)", 6, 1, 200, 1, 0)]
        public int MinLegTicks_1H = 20;

        [InputParameter("Min Leg Size — 15M (ticks)", 7, 1, 100, 1, 0)]
        public int MinLegTicks_15M = 10;

        [InputParameter("Min Leg Size — 5M (ticks)", 8, 1, 100, 1, 0)]
        public int MinLegTicks_5M = 6;

        [InputParameter("Min Leg Size — 1M (ticks)", 9, 1, 100, 1, 0)]
        public int MinLegTicks_1M = 4;

        // ── History Depth Per Timeframe ────────────────────────────────────

        [InputParameter("History Days (Monthly)", 10, 30, 3650, 30, 0)]
        public int HistDaysMN = 1825;

        [InputParameter("History Days (Weekly)", 11, 7, 1825, 7, 0)]
        public int HistDaysW = 730;

        [InputParameter("History Days (Daily)", 12, 7, 730, 7, 0)]
        public int HistDaysD = 365;

        [InputParameter("History Days (4H)", 13, 1, 365, 1, 0)]
        public int HistDays4H = 90;

        [InputParameter("History Days (1H)", 14, 1, 180, 1, 0)]
        public int HistDays1H = 30;

        [InputParameter("History Days (15M)", 15, 1, 90, 1, 0)]
        public int HistDays15M = 14;

        [InputParameter("History Days (5M)", 16, 1, 30, 1, 0)]
        public int HistDays5M = 7;

        [InputParameter("History Days (1M)", 17, 1, 14, 1, 0)]
        public int HistDays1M = 3;

        // ── Timeframe Visibility Toggles ───────────────────────────────────

        [InputParameter("Show Monthly", 20)]
        public bool ShowMN = true;

        [InputParameter("Show Weekly", 21)]
        public bool ShowW = true;

        [InputParameter("Show Daily", 22)]
        public bool ShowD = true;

        [InputParameter("Show 4H", 23)]
        public bool Show4H = true;

        [InputParameter("Show 1H", 24)]
        public bool Show1H = true;

        [InputParameter("Show 15M", 25)]
        public bool Show15M = true;

        [InputParameter("Show 5M", 26)]
        public bool Show5M = true;

        [InputParameter("Show 1M", 27)]
        public bool Show1M = true;

        // ── Panel Layout ───────────────────────────────────────────────────

        [InputParameter("Panel X Offset (px)", 30, 0, 2000, 1, 0)]
        public int PanelX = 10;

        [InputParameter("Panel Y Offset (px)", 31, 0, 2000, 1, 0)]
        public int PanelY = 10;

        [InputParameter("Row Height (px)", 32, 14, 60, 1, 0)]
        public int RowHeight = 24;

        [InputParameter("Panel Width (px)", 33, 100, 500, 1, 0)]
        public int PanelWidth = 200;

        [InputParameter("Font Size", 34, 6, 18, 1, 0)]
        public int FontSize = 9;

        [InputParameter("Label Column Width (px)", 35, 20, 100, 1, 0)]
        public int LabelColWidth = 44;

        // ── Display Options ────────────────────────────────────────────────

        [InputParameter("Show Control Point Price", 40)]
        public bool ShowControlPrice = true;

        [InputParameter("Show Bar Count", 41)]
        public bool ShowBarCount = false;

        [InputParameter("Show Title Bar", 42)]
        public bool ShowTitle = true;

        [InputParameter("Min Bars Before Showing State (warm-up)", 43, 1, 50, 1, 0)]
        public int WarmupBars = 6;

        // ── Chart Drawing ──────────────────────────────────────────────────

        // Clean-chart default: show ONLY trades (entries / stops / targets / win-loss).
        // Flip these on if you want the structure context back.
        [InputParameter("Draw Structure Labels (HH/HL/LH/LL)", 60)]
        public bool DrawLabels = false;

        [InputParameter("Draw Controlling High Zone", 61)]
        public bool DrawCtrlHigh = false;

        [InputParameter("Draw Controlling Low Zone", 62)]
        public bool DrawCtrlLow = false;

        [InputParameter("Structure Label Font Size", 63, 6, 16, 1, 0)]
        public int LabelFontSize = 8;

        [InputParameter("Zone Line Thickness (px)", 64, 1, 5, 1, 0)]
        public int ZoneLineThick = 2;

        [InputParameter("Zone Fill Alpha (0-255)", 65, 0, 255, 1, 0)]
        public int ZoneFillAlpha = 30;

        // ── Live Data Export (for external real-time watching) ─────────────

        [InputParameter("Export Live Snapshot", 80)]
        public bool ExportEnabled = true;

        [InputParameter("Export Directory", 81)]
        public string ExportDir = @"C:\quantower\Settings\Scripts\IOF_Live";

        [InputParameter("VA Lookback Bars", 82, 10, 5000, 1, 0)]
        public int VALookbackBars = 288;

        [InputParameter("VA Bucket Size (ticks)", 83, 1, 50, 1, 0)]
        public int VABucketTicks = 1;

        [InputParameter("VA Value-Area Percent", 84, 0.50, 0.95, 0.01, 2)]
        public double VAPercent = 0.70;

        [InputParameter("Export Throttle (sec)", 85, 1, 120, 1, 0)]
        public int ExportThrottleSec = 5;

        // ── Entry Signal Drawing ───────────────────────────────────────────

        [InputParameter("Draw Entry Signals", 86)]
        public bool DrawEntries = true;

        [InputParameter("Entry Marker Size (px)", 87, 3, 16, 1, 0)]
        public int EntryMarkerSize = 7;

        [InputParameter("Long Entry Color", 88)]
        public Color LongEntryColor = Color.FromArgb(0, 200, 120);

        [InputParameter("Short Entry Color", 89)]
        public Color ShortEntryColor = Color.FromArgb(235, 70, 70);

        [InputParameter("Hide Counter-Trend Entries (HTF gate)", 90)]
        public bool HideCounterTrendEntries = false;

        [InputParameter("Draw Pending Setups (watchlist)", 92)]
        public bool DrawPending = true;

        [InputParameter("Draw Active Trade (live position + P&L)", 93)]
        public bool DrawActivePosition = true;

        [InputParameter("Manual Signal Mode (show ALL entries to trade by hand)", 94)]
        public bool ManualSignalMode = true;

        // ── IOF Zones (impulse-base-impulse) ───────────────────────────────

        [InputParameter("Draw IOF Zones", 91)]
        public bool DrawZones = false;

        [InputParameter("Zone Base Max Body %", 92, 0.1, 0.9, 0.05, 2)]
        public double ZoneBaseMaxBodyPct = 0.5;

        [InputParameter("Zone Min Impulse Ratio", 93, 1.0, 5.0, 0.1, 1)]
        public double ZoneMinImpulseRatio = 2.0;

        [InputParameter("Zone Max Base Candles", 94, 1, 7, 1, 0)]
        public int ZoneMaxBaseCandles = 5;

        [InputParameter("Zone Lookback Bars", 95, 50, 2000, 50, 0)]
        public int ZoneLookbackBars = 400;

        [InputParameter("Zone Fill Color (Demand)", 96)]
        public Color DemandZoneColor = Color.FromArgb(45, 0, 190, 110);

        [InputParameter("Zone Fill Color (Supply)", 97)]
        public Color SupplyZoneColor = Color.FromArgb(45, 210, 60, 60);

        // ── Account simulator (funded-account risk math) ───────────────────

        [InputParameter("Acct: $ per point (NQ=20, ES=50)", 98, 1, 100, 1, 0)]
        public double AcctPointValue = 20;

        [InputParameter("Acct: Trailing Drawdown $", 99, 500, 20000, 100, 0)]
        public double AcctDrawdown = 2000;

        [InputParameter("Acct: Profit Target $", 100, 500, 50000, 100, 0)]
        public double AcctTarget = 3000;

        [InputParameter("Acct: Max Risk / Trade $", 101, 50, 3000, 25, 0)]
        public double AcctMaxRisk = 300;

        [InputParameter("Take-Profit R:R (lock gains)", 102, 0.5, 10.0, 0.25, 2)]
        public double TakeProfitRR = 2.0;

        [InputParameter("Show Trend Panel (MTF)", 103)]
        public bool ShowTrendPanel = false;

        // The live prop-account dashboard (phase / balance / floor / today / trades).
        // A compact corner panel — not price-chart clutter.
        [InputParameter("Show Account Dashboard", 104)]
        public bool ShowAccountPanel = true;

        [InputParameter("Acct: Cost / Trade $ (comm+slip)", 105, 0, 100, 1, 0)]
        public double AcctCostPerTrade = 15;

        // ── Risk Governor (survival layer) ─────────────────────────────────
        // Hard circuit breakers applied identically in backtest and live. The daily
        // loss cap is the amount you can lose in a session and still make back PLUS
        // some next session — kept well inside the prop trailing drawdown.

        [InputParameter("Gov: Max Losses / Day (0=off)", 106, 0, 10, 1, 0)]
        public int GovMaxLossesPerDay = 2;

        [InputParameter("Gov: Daily Loss Cap $ (0=off)", 107, 0, 5000, 50, 0)]
        public double GovDailyLossCap = 600;

        [InputParameter("Gov: Daily Profit Lock $ (0=off)", 108, 0, 10000, 50, 0)]
        public double GovDailyProfitLock = 0;

        [InputParameter("Gov: Enforce Session Window", 109)]
        public bool GovEnforceSession = false;

        [InputParameter("Gov: Session Start (UTC hour)", 110, 0, 23, 1, 0)]
        public int GovSessionStartUtc = 13;   // ~9:00 ET

        [InputParameter("Gov: Session End (UTC hour)", 111, 0, 24, 1, 0)]
        public int GovSessionEndUtc = 18;      // ~1:00 PM ET (master-ref: after 1pm is risky)

        // ── Prop account model (EOD, no trailing) ──────────────────────────
        [InputParameter("Acct: Start Balance $", 112, 1000, 500000, 1000, 0)]
        public double AcctStartBalance = 50000;

        [InputParameter("Acct: Funded Floor $ (blow level)", 113, 1000, 500000, 500, 0)]
        public double AcctFundedFloor = 52000;

        [InputParameter("Fills: Entry Slippage (ticks)", 114, 0, 20, 1, 0)]
        public double SlipTicksEntry = 1;

        [InputParameter("Fills: Stop Slippage (ticks)", 115, 0, 20, 1, 0)]
        public double SlipTicksStop = 2;

        [InputParameter("Acct: Eval Start (sessions ago, 0=current)", 116, 0, 365, 1, 0)]
        public int AcctEvalStartDaysAgo = 0;

        [InputParameter("Acct: Session Roll (UTC hour, 22=3pm PT)", 117, 0, 23, 1, 0)]
        public int AcctSessionRollUtc = 22;

        [InputParameter("Fills: Breakeven at % to target (0=off)", 118, 0.0, 1.0, 0.05, 2)]
        public double BreakevenAtPct = 0.5;

        // Eval start PINNED (UTC). Default 2026-06-24 22:00 UTC = 3pm PT Jun 24. Fixed so the
        // account accumulates across days instead of resetting each session.
        [InputParameter("Acct: Eval Start Year (UTC)", 119, 2020, 2035, 1, 0)]
        public int AcctEvalYear = 2026;

        [InputParameter("Acct: Eval Start Month (UTC)", 120, 1, 12, 1, 0)]
        public int AcctEvalMonth = 6;

        [InputParameter("Acct: Eval Start Day (UTC)", 121, 1, 31, 1, 0)]
        public int AcctEvalDay = 25;

        [InputParameter("Acct: Eval Start Hour (UTC)", 122, 0, 23, 1, 0)]
        public int AcctEvalHour = 15;   // FRESH RESET pin: 2026-06-25 15:00 UTC

        [InputParameter("Acct: Max Contracts (fallback if symbol unknown)", 123, 1, 50, 1, 0)]
        public int AcctMaxContracts = 1;

        [InputParameter("Zone Entry: Enable Retrace Entries", 124)]
        public bool UseZoneEntries = true;

        [InputParameter("Zone Entry: Require Rejection Close", 125)]
        public bool ZoneRequireRejection = true;

        [InputParameter("Zone Entry: Min R:R (zone-to-zone)", 126, 0.5, 10.0, 0.5, 1)]
        public double ZoneMinRR = 2.0;

        // ── Colors ─────────────────────────────────────────────────────────

        [InputParameter("Bull Color", 50)]
        public Color BullColor = Color.FromArgb(0, 160, 60);

        [InputParameter("Bear Color", 51)]
        public Color BearColor = Color.FromArgb(200, 30, 30);

        [InputParameter("Flat Color", 52)]
        public Color FlatColor = Color.FromArgb(60, 60, 70);

        [InputParameter("Loading Color", 53)]
        public Color LoadingColor = Color.FromArgb(35, 35, 35);

        [InputParameter("Panel Background Color", 54)]
        public Color PanelBgColor = Color.FromArgb(200, 15, 15, 15);

        [InputParameter("Panel Border Color", 55)]
        public Color BorderColor = Color.FromArgb(80, 80, 80);

        [InputParameter("Label Text Color", 56)]
        public Color LabelTextColor = Color.FromArgb(180, 180, 180);

        [InputParameter("State Text Color", 57)]
        public Color StateTextColor = Color.White;

        [InputParameter("Title Text Color", 58)]
        public Color TitleTextColor = Color.FromArgb(220, 220, 220);

        [InputParameter("Controlling High Color", 66)]
        public Color CtrlHighColor = Color.FromArgb(200, 30, 30);

        [InputParameter("Controlling Low Color", 67)]
        public Color CtrlLowColor = Color.FromArgb(0, 160, 60);

        [InputParameter("HH Label Color", 68)]
        public Color HHColor = Color.FromArgb(0, 200, 80);

        [InputParameter("LL Label Color", 69)]
        public Color LLColor = Color.FromArgb(220, 40, 40);

        [InputParameter("HL Label Color", 70)]
        public Color HLColor = Color.FromArgb(0, 160, 60);

        [InputParameter("LH Label Color", 71)]
        public Color LHColor = Color.FromArgb(180, 30, 30);

        // ── Internal ───────────────────────────────────────────────────────

        private static readonly string[] TF_LABELS  = { "MN", "W", "D", "4H", "1H", "15M", "5M", "1M" };
        private static readonly Period[] TF_PERIODS = {
            Period.MONTH1, Period.WEEK1, Period.DAY1, Period.HOUR4,
            Period.HOUR1, Period.MIN15, Period.MIN5, Period.MIN1
        };
        private const int TF_COUNT = 8;

        private bool[] TF_VISIBLE => new bool[] {
            ShowMN, ShowW, ShowD, Show4H, Show1H, Show15M, Show5M, Show1M
        };

        private int[] HistDays => new int[] {
            HistDaysMN, HistDaysW, HistDaysD, HistDays4H,
            HistDays1H, HistDays15M, HistDays5M, HistDays1M
        };

        private HistoricalData[]    _feeds;
        private TrendStateMachine[] _machines;
        private DateTime[]          _feedLastTime;        // last processed bar TIME per feed
        private int[]               _feedProcessedCount;  // bars processed per feed (for warm-up)
        private double              _tickSize;
        private TrendSnapshot[]     _snapshots;

        // Chart-TF machine (for drawing on current chart)
        private TrendStateMachine   _chartMachine;
        private DateTime            _chartLastTime = DateTime.MinValue;
        private int                 _chartProcessedCount;
        private TrendSnapshot       _chartSnapshot;
        private List<IofZone>       _chartZones = new List<IofZone>();
        private DateTime            _zonesLastScanBar = DateTime.MinValue;

        // Cached account analytics (recomputed on a new bar / TpRR change, not per repaint)
        private AccountSimResult    _acctSim;
        private int                 _acctPasses, _acctBlows, _acctNeither;
        private List<(double rr, int passes, int blows, double winRate, double expR)> _rrSweep
            = new List<(double, int, int, double, double)>();
        private double              _acctBestRR = 2.0;
        private double              _stressWorstDD;
        private int                 _stressStreak, _stressTrades;
        private double              _stressFinal;
        private int                 _lastAnalyticsCount = -1;
        private double              _lastAnalyticsTpRR = -1;
        private GovernedResult      _governed;   // honest, circuit-breaker-gated account run
        private PoolResult          _pool;       // 30-eval pool — full-history projection
        private PoolResult          _poolLive;   // 30-eval pool — LIVE, from the pinned eval start
        private int                 _passesHighWater = 0;  // monotonic banked-pass count — never drops on re-derivation
        private bool                _hwLoaded = false;     // read the persisted high-water once
        private int                 _hwWritten = -1;       // last value written to disk (avoid rewriting)

        // Pending setups gathered from ALL feeds (every chart's exported snapshot) so each chart
        // can draw the whole watchlist. Read-only — does not affect trades.
        private List<(string sym, int tf, int side, double entry, double stop, double target)> _crossSetups
            = new List<(string, int, int, double, double, double)>();
        private DateTime _crossLastRead = DateTime.MinValue;

        // Live export state
        private DateTime            _lastExportUtc        = DateTime.MinValue;
        private int                 _lastExportedClosedBar = -1;
        private double              _chartTfMinutes        = double.NaN;

        // ── Helpers ────────────────────────────────────────────────────────

        private int GetMinLegTicksByIndex(int i)
        {
            if (i <= 3) return MinLegTicks_4H;
            if (i == 4) return MinLegTicks_1H;
            if (i == 5) return MinLegTicks_15M;
            if (i == 6) return MinLegTicks_5M;
            return MinLegTicks_1M;
        }

        // Infer chart period (minutes) from bar timestamp gap — SDK has no Period property.
        private double InferChartTfMinutes()
        {
            try
            {
                var hd = this.HistoricalData;
                if (hd == null || hd.Count < 2) return 240;
                var b0 = hd[0] as HistoryItemBar;
                var b1 = hd[1] as HistoryItemBar;
                if (b0 == null || b1 == null) return 240;
                return Math.Abs((b1.TimeLeft - b0.TimeLeft).TotalMinutes);
            }
            catch { return 240; }
        }

        private int InferChartMinLegTicks()
        {
            double minutes = InferChartTfMinutes();
            if (minutes < 2)   return MinLegTicks_1M;
            if (minutes < 10)  return MinLegTicks_5M;
            if (minutes < 45)  return MinLegTicks_15M;
            if (minutes < 180) return MinLegTicks_1H;
            return MinLegTicks_4H;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────

        public IOF_TrendLab() : base()
        {
            Name           = "IOF TrendLab Full";
            Description    = "Multi-timeframe IOF trend dashboard with chart structure drawing.";
            SeparateWindow = false;
            AddLineSeries("Dummy", Color.Transparent, 1, LineStyle.Solid);
        }

        protected override void OnInit()
        {

            _feeds              = new HistoricalData[TF_COUNT];
            _machines           = new TrendStateMachine[TF_COUNT];
            _feedLastTime       = new DateTime[TF_COUNT];
            _feedProcessedCount = new int[TF_COUNT];
            _snapshots          = new TrendSnapshot[TF_COUNT];
            _tickSize           = this.Symbol?.TickSize ?? 0.25;

            _chartMachine = new TrendStateMachine
            {
                SwingFractalLookback            = SwingLookback,
                RequireEngulfingForControlPoint  = RequireEngulfing,
                RequireSegments                  = RequireSegments,
                MinLegTicks                     = MinLegTicks_4H  // refined on first bar in OnUpdate
            };
            _chartLastTime       = DateTime.MinValue;
            _chartProcessedCount = 0;

            int[] perTFMinLeg = {
                MinLegTicks_4H,  // 0 MN  — use 4H as floor for slow TFs
                MinLegTicks_4H,  // 1 W
                MinLegTicks_4H,  // 2 D
                MinLegTicks_4H,  // 3 4H
                MinLegTicks_1H,  // 4 1H
                MinLegTicks_15M, // 5 15M
                MinLegTicks_5M,  // 6 5M
                MinLegTicks_1M,  // 7 1M
            };

            for (int i = 0; i < TF_COUNT; i++)
            {
                _feedLastTime[i]       = DateTime.MinValue;
                _feedProcessedCount[i] = 0;
                _machines[i] = new TrendStateMachine
                {
                    SwingFractalLookback            = SwingLookback,
                    RequireEngulfingForControlPoint  = RequireEngulfing,
                    RequireSegments                  = RequireSegments,
                    MinLegTicks                     = perTFMinLeg[i],
                };

                try
                {
                    DateTime from = DateTime.UtcNow.AddDays(-HistDays[i]);
                    _feeds[i] = this.Symbol.GetHistory(TF_PERIODS[i], from, DateTime.UtcNow);
                }
                catch
                {
                    _feeds[i] = null;
                }
            }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            // Process chart-TF bars for the drawing machine (oldest -> newest)
            var mainFeed = this.HistoricalData;
            if (mainFeed != null && mainFeed.Count >= 2)
            {
                // Detect chart period on first bar and set the correct MinLegTicks
                if (_chartProcessedCount == 0)
                    _chartMachine.MinLegTicks = InferChartMinLegTicks();

                try { ScanZonesIfNewBar(mainFeed); } catch { }   // scan FIRST so zones exist for the replay
                _chartMachine.UseZoneEntries       = UseZoneEntries;
                _chartMachine.ZoneRequireRejection = ZoneRequireRejection;
                _chartMachine.ZoneMinRR            = ZoneMinRR;
                _chartMachine.TpRR                 = TakeProfitRR;     // entries resolve at the account TP
                _chartMachine.BreakevenAtPct       = BreakevenAtPct;
                _chartMachine.SetActiveZones(_chartZones);        // zone-wick stops + zone-retrace entries
                _chartProcessedCount += ProcessFeedBars(mainFeed, _chartMachine, ref _chartLastTime);
            }
            _chartMachine.TpRR = TakeProfitRR;
            _chartSnapshot = _chartMachine.GetSnapshot();

            try { ComputeAccountAnalytics(); } catch { }
            try { ScanZonesIfNewBar(mainFeed); } catch { }

            // Process MTF feeds
            for (int i = 0; i < TF_COUNT; i++)
                ProcessFeed(i);

            for (int i = 0; i < TF_COUNT; i++)
            {
                if (_machines[i] != null)
                    _snapshots[i] = _machines[i].GetSnapshot();
            }

            TryExport();
            try { ReadCrossSetupsThrottled(); } catch { }   // gather every feed's pending setups for the overlay

            SetValue(double.NaN, 0);
        }

        protected override void OnClear()
        {
            if (_feeds == null) return;
            for (int i = 0; i < TF_COUNT; i++)
            {
                try { _feeds[i]?.Dispose(); } catch { }
                _feeds[i] = null;
            }
        }

        // ── MTF Bar Processing ─────────────────────────────────────────────

        private void ProcessFeed(int idx)
        {
            var feed = _feeds[idx];
            if (feed == null) return;
            _feedProcessedCount[idx] += ProcessFeedBars(feed, _machines[idx], ref _feedLastTime[idx]);
        }

        // Feed all CLOSED bars newer than `lastTime` into `machine`, in chronological
        // (oldest -> newest) order. Auto-detects feed ordering: Quantower's live
        // HistoricalData and GetHistory feeds are newest-first (index 0 = forming bar),
        // but this also handles oldest-first safely. Returns # of bars processed.
        private int ProcessFeedBars(HistoricalData feed, TrendStateMachine machine, ref DateTime lastTime)
        {
            int count = feed.Count;
            if (count < 2) return 0;

            var b0 = feed[0]          as HistoryItemBar;
            var bN = feed[count - 1]  as HistoryItemBar;
            if (b0 == null || bN == null) return 0;
            bool newestFirst = b0.TimeLeft > bN.TimeLeft;

            int processed = 0;
            if (newestFirst)
            {
                // index 0 = forming bar; closed bars are count-1 (oldest) .. 1 (newest closed)
                for (int j = count - 1; j >= 1; j--)
                {
                    var bar = feed[j] as HistoryItemBar;
                    if (bar == null || bar.TimeLeft <= lastTime) continue;
                    machine.OnBarClose(j, bar.TimeLeft, bar.Open, bar.High, bar.Low, bar.Close, _tickSize);
                    lastTime = bar.TimeLeft;
                    processed++;
                }
            }
            else
            {
                // index count-1 = forming bar; closed bars are 0 (oldest) .. count-2 (newest closed)
                for (int j = 0; j <= count - 2; j++)
                {
                    var bar = feed[j] as HistoryItemBar;
                    if (bar == null || bar.TimeLeft <= lastTime) continue;
                    machine.OnBarClose(j, bar.TimeLeft, bar.Open, bar.High, bar.Low, bar.Close, _tickSize);
                    lastTime = bar.TimeLeft;
                    processed++;
                }
            }
            return processed;
        }

        // ── Account analytics (cached) ─────────────────────────────────────

        // PINNED eval start (UTC). Fixed point the account runs from — does NOT roll forward
        // each session, so the eval accumulates across days until it passes or blows.
        private DateTime PinnedEvalStartUtc()
        {
            try { return new DateTime(AcctEvalYear, AcctEvalMonth, AcctEvalDay, AcctEvalHour, 0, 0, DateTimeKind.Utc); }
            catch { return DateTime.MinValue; }   // bad date → count all history
        }

        // Auto-detect contract specs from the symbol so MNQ/NQ/ES/MES "just work" and survive
        // restarts (Quantower resets inputs to code defaults). Returns (pointValue, maxContracts,
        // commission $/contract). Micros size up to 5; minis trade 1.
        private (double pv, int maxC, double comm) DetectContractSpec(string sym)
        {
            string s = (sym ?? "").ToUpperInvariant();
            // Micros first (M-prefix), each contains its mini's ticker. $ per POINT (not per tick).
            if (s.Contains("MNQ")) return (2.0,    10, 1.5);  // Micro Nasdaq  (10 micros = 1 NQ)
            if (s.Contains("MES")) return (5.0,    10, 1.5);  // Micro S&P     (10 micros = 1 ES)
            if (s.Contains("MYM")) return (0.5,    10, 1.5);  // Micro Dow     (10 micros = 1 YM)
            if (s.Contains("M2K")) return (5.0,    10, 1.5);  // Micro Russell (10 micros = 1 RTY)
            if (s.Contains("MGC")) return (10.0,   10, 1.5);  // Micro Gold    (10 micros = 1 GC)
            if (s.Contains("MCL")) return (100.0,  10, 1.5);  // Micro Crude   (10 micros = 1 CL)
            if (s.Contains("NQ"))  return (20.0,   1, 4.5);   // E-mini Nasdaq
            if (s.Contains("ES"))  return (50.0,   1, 4.5);   // E-mini S&P
            if (s.Contains("YM"))  return (5.0,    1, 4.5);   // E-mini Dow
            if (s.Contains("RTY")) return (50.0,   1, 4.5);   // E-mini Russell
            if (s.Contains("GC"))  return (100.0,  1, 4.5);   // Gold
            if (s.Contains("CL"))  return (1000.0, 1, 4.5);   // Crude
            return (AcctPointValue, Math.Max(1, AcctMaxContracts), AcctCostPerTrade);
        }

        // True if the symbol matches a ticker in the contract table (else it's on the fallback
        // $/pt and the pricing is probably wrong — surfaced in the export as contract.known=false).
        private static bool IsKnownSymbol(string sym)
        {
            string s = (sym ?? "").ToUpperInvariant();
            foreach (var k in new[] { "MNQ", "MES", "MYM", "M2K", "MGC", "MCL", "NQ", "ES", "YM", "RTY", "GC", "CL" })
                if (s.Contains(k)) return true;
            return false;
        }

        private void ComputeAccountAnalytics()
        {
            if (_chartMachine == null) return;
            if (_chartProcessedCount == _lastAnalyticsCount && TakeProfitRR == _lastAnalyticsTpRR) return;
            _lastAnalyticsCount = _chartProcessedCount;
            _lastAnalyticsTpRR  = TakeProfitRR;

            _chartMachine.TpRR = TakeProfitRR;
            _acctSim = _chartMachine.RunAccountSim(AcctPointValue, AcctDrawdown, AcctTarget, AcctMaxRisk, AcctCostPerTrade);
            (_acctPasses, _acctBlows, _acctNeither) =
                _chartMachine.RunAccountPassRate(AcctPointValue, AcctDrawdown, AcctTarget, AcctMaxRisk, AcctCostPerTrade);
            _rrSweep = _chartMachine.RunRRSweep(new double[] { 1.0, 1.5, 2.0, 2.5, 3.0, 4.0 },
                                                AcctPointValue, AcctDrawdown, AcctTarget, AcctMaxRisk, AcctCostPerTrade);
            (_stressWorstDD, _stressStreak, _stressFinal, _stressTrades) =
                _chartMachine.RunStress(AcctPointValue, AcctMaxRisk, AcctCostPerTrade);
            double bestPR = -1; _acctBestRR = TakeProfitRR;
            foreach (var s in _rrSweep)
            {
                int sd = s.passes + s.blows;
                double pr = sd > 0 ? (double)s.passes / sd : 0;
                if (pr > bestPR) { bestPR = pr; _acctBestRR = s.rr; }
            }

            // Honest, circuit-breaker-gated run — the number that actually matters.
            var spec = DetectContractSpec(this.Symbol?.Name);
            // Higher timeframes carry wider structural stops — a fixed $300 cap rejects every
            // 1h/4h signal (153pt × $2 = $306 > $300 → sizes to 0). Scale the cap by TF so the
            // big timeframes can hold at least 1 micro; faster TFs stay tight. Daily cap scales too.
            double riskMult  = _chartTfMinutes >= 240 ? 1.67 : _chartTfMinutes >= 60 ? 1.5 : 1.0;
            double tfMaxRisk = AcctMaxRisk     * riskMult;
            double tfDayCap  = GovDailyLossCap * riskMult;
            var rules = new RiskRules
            {
                PointValue           = spec.pv,
                MaxContracts         = spec.maxC,
                MaxRiskPerTradeDollars = tfMaxRisk,
                MaxLossesPerDay      = GovMaxLossesPerDay,
                DailyLossCapDollars  = tfDayCap,
                DailyProfitLockDollars = GovDailyProfitLock,
                AccountTrailingDD    = AcctDrawdown,
                ProfitTarget         = AcctTarget,
                CostPerTrade         = spec.comm,
                TpRR                 = TakeProfitRR,
                EnforceSession       = GovEnforceSession,
                SessionStartMinUtc   = GovSessionStartUtc * 60,
                SessionEndMinUtc     = GovSessionEndUtc >= 24 ? 1440 : GovSessionEndUtc * 60,
                StartBalance         = AcctStartBalance,
                FundedFloor          = AcctFundedFloor,
                TickSize             = _tickSize,
                SlipTicksEntry       = SlipTicksEntry,
                SlipTicksStop        = SlipTicksStop,
                SessionRolloverHourUtc = AcctSessionRollUtc,
                BreakevenAtPct       = BreakevenAtPct,
                EvalStartUtc         = PinnedEvalStartUtc()
            };
            var simBook = _chartMachine.GetSimTrades();
            _governed = RiskGovernor.RunGoverned(simBook, rules, DateTime.UtcNow);
            // A pass, once achieved, is banked for good — the live re-derivation can flicker the
            // count down as bars drift, but the high-water never drops. (Persisted in TryExport,
            // where the timeframe is resolved — here _chartTfMinutes can still be NaN.)
            if (_governed != null && _governed.EvalPasses > _passesHighWater)
                _passesHighWater = _governed.EvalPasses;
            // NOTE: passing is decided by RunGoverned on CLOSED trades only — its pass-securing
            // locks the pass at $53k whenever a closed trade's recorded PEAK (MFE) reached the
            // target, even if it then reversed. While a trade is still OPEN the account stays
            // ARMED and the live P&L shows on the active-trade overlay; it flips FUNDED on close.
            // (No live override here — that caused "FUNDED + still in an active trade".)
            _pool     = RiskGovernor.RunEvalPool(simBook, rules, 30, DateTime.MinValue);     // full-history projection
            _poolLive = RiskGovernor.RunEvalPool(simBook, rules, 30, PinnedEvalStartUtc());  // LIVE spin-up from the pin
        }

        // ── IOF Zone scanning ──────────────────────────────────────────────

        private void ScanZonesIfNewBar(HistoricalData hd)
        {
            // Always scan (cheap, feeds export + zone targets); DRAWING is gated separately.
            if (hd == null || hd.Count < 4) return;
            int count = hd.Count;
            var b0 = hd[0]         as HistoryItemBar;
            var bN = hd[count - 1] as HistoryItemBar;
            if (b0 == null || bN == null) return;
            bool newestFirst = b0.TimeLeft > bN.TimeLeft;

            var newestClosed = newestFirst ? hd[1] as HistoryItemBar
                                           : hd[count - 2] as HistoryItemBar;
            if (newestClosed == null) return;
            if (newestClosed.TimeLeft == _zonesLastScanBar) return;   // already scanned this bar
            _zonesLastScanBar = newestClosed.TimeLeft;

            var chrono = BuildChronoBars(hd, newestFirst, ZoneLookbackBars);
            if (chrono.Count < 4) return;
            try
            {
                _chartZones = IofZoneScanner.Scan(chrono, ZoneBaseMaxBodyPct, ZoneMinImpulseRatio, ZoneMaxBaseCandles);
            }
            catch { }
        }

        private List<HistoryItemBar> BuildChronoBars(HistoricalData hd, bool newestFirst, int maxBars)
        {
            var list = new List<HistoryItemBar>(maxBars);
            int count = hd.Count;
            if (newestFirst)
            {
                int oldest = Math.Min(count - 1, maxBars);   // closed bars are 1..count-1 (0=forming)
                for (int j = oldest; j >= 1; j--)
                {
                    var b = hd[j] as HistoryItemBar;
                    if (b != null) list.Add(b);
                }
            }
            else
            {
                int lastClosed = count - 2;
                int from = Math.Max(0, lastClosed - maxBars + 1);
                for (int j = from; j <= lastClosed; j++)
                {
                    var b = hd[j] as HistoryItemBar;
                    if (b != null) list.Add(b);
                }
            }
            return list;
        }

        // ── Live Snapshot Export ───────────────────────────────────────────

        private void TryExport()
        {
            if (!ExportEnabled) return;
            try
            {
                var hd = this.HistoricalData;
                if (hd == null || hd.Count < 2) return;

                int count      = hd.Count;
                int lastClosed = count - 2;

                bool   newBar   = lastClosed != _lastExportedClosedBar;
                double sinceSec = (DateTime.UtcNow - _lastExportUtc).TotalSeconds;
                if (!newBar && sinceSec < ExportThrottleSec) return;

                if (double.IsNaN(_chartTfMinutes))
                    _chartTfMinutes = InferChartTfMinutes();

                var va = VolumeProfile.ComputeProfile(
                    hd, VALookbackBars, _tickSize, VABucketTicks, VAPercent);

                string sym = SanitizeSymbol(this.Symbol?.Name ?? "UNKNOWN");
                bool tfValid = !double.IsNaN(_chartTfMinutes) && _chartTfMinutes > 0;
                int  tfm = tfValid ? (int)Math.Round(_chartTfMinutes) : 1;

                // Durable banked-pass high-water: restore once + persist on increase BEFORE building
                // the snapshot, so the count reflects disk. (Timeframe is resolved here, unlike in
                // ComputeAccountAnalytics — that was writing garbage "_-2147483648m" filenames.)
                if (tfValid)
                {
                    try
                    {
                        string hwp = Path.Combine(ExportDir, $"passhw_{sym}_{tfm}m.txt");
                        if (!_hwLoaded)
                        {
                            _hwLoaded = true;
                            if (File.Exists(hwp) && int.TryParse(File.ReadAllText(hwp).Trim(), out int saved) && saved > _passesHighWater)
                                _passesHighWater = saved;
                        }
                        if (_passesHighWater > _hwWritten) { File.WriteAllText(hwp, _passesHighWater.ToString()); _hwWritten = _passesHighWater; }
                    }
                    catch { }
                }

                string json = BuildSnapshotJson(hd, count, lastClosed, va);

                Directory.CreateDirectory(ExportDir);
                string path = Path.Combine(ExportDir, $"snapshot_{sym}_{tfm}m.json");
                string tmp  = path + ".tmp";

                File.WriteAllText(tmp, json);
                File.Move(tmp, path, true);   // atomic-ish replace; reader never sees a partial file

                _lastExportUtc         = DateTime.UtcNow;
                _lastExportedClosedBar = lastClosed;
            }
            catch { /* export must never throw into the indicator */ }
        }

        private string BuildSnapshotJson(HistoricalData hd, int count, int lastClosed, VaResult va)
        {
            var sb = new StringBuilder(2048);

            // Detect feed ordering — live HistoricalData is newest-first in Quantower.
            var hdFirst = hd[0]         as HistoryItemBar;
            var hdLast  = hd[count - 1] as HistoryItemBar;
            bool newestFirst = hdFirst != null && hdLast != null
                               && hdFirst.TimeLeft > hdLast.TimeLeft;

            HistoryItemBar formingBar = newestFirst ? hdFirst : hdLast;
            HistoryItemBar lastBar    = newestFirst
                ? (count >= 2 ? hd[1]          as HistoryItemBar : null)
                : (count >= 2 ? hd[count - 2]  as HistoryItemBar : null);
            double last = formingBar?.Close ?? lastBar?.Close ?? double.NaN;

            sb.Append('{');
            sb.Append("\"schemaVersion\":1,");
            sb.Append("\"writtenUtc\":").Append(T(DateTime.UtcNow)).Append(',');
            sb.Append("\"symbol\":").Append(S(this.Symbol?.Name)).Append(',');
            sb.Append("\"chartTfMinutes\":").Append(D(_chartTfMinutes)).Append(',');
            sb.Append("\"tickSize\":").Append(D(_tickSize)).Append(',');

            // Detected contract spec — so pricing is VERIFIABLE per chart. "known" = matched the
            // table (false = fell to the $/pt fallback and is probably wrong for this symbol).
            var cspec = DetectContractSpec(this.Symbol?.Name);
            bool specKnown = IsKnownSymbol(this.Symbol?.Name);
            sb.Append("\"contract\":{")
              .Append("\"pointValue\":").Append(D(cspec.pv)).Append(',')
              .Append("\"tickValue\":").Append(D(_tickSize * cspec.pv)).Append(',')
              .Append("\"maxContracts\":").Append(cspec.maxC).Append(',')
              .Append("\"commission\":").Append(D(cspec.comm)).Append(',')
              .Append("\"known\":").Append(specKnown ? "true" : "false")
              .Append("},");

            sb.Append("\"price\":{\"last\":").Append(D(last)).Append("},");

            sb.Append("\"lastClosedBar\":");
            if (lastBar != null)
            {
                sb.Append('{')
                  .Append("\"timeUtc\":").Append(T(lastBar.TimeLeft)).Append(',')
                  .Append("\"o\":").Append(D(lastBar.Open)).Append(',')
                  .Append("\"h\":").Append(D(lastBar.High)).Append(',')
                  .Append("\"l\":").Append(D(lastBar.Low)).Append(',')
                  .Append("\"c\":").Append(D(lastBar.Close)).Append(',')
                  .Append("\"v\":").Append(D(lastBar.Volume))
                  .Append('}');
            }
            else sb.Append("null");
            sb.Append(',');

            sb.Append("\"valueArea\":{")
              .Append("\"valid\":").Append(va.Valid ? "true" : "false").Append(',')
              .Append("\"poc\":").Append(va.Valid ? D(va.Poc) : "null").Append(',')
              .Append("\"vah\":").Append(va.Valid ? D(va.Vah) : "null").Append(',')
              .Append("\"val\":").Append(va.Valid ? D(va.Val) : "null").Append(',')
              .Append("\"totalVolume\":").Append(D(va.TotalVolume)).Append(',')
              .Append("\"barsUsed\":").Append(va.BarsUsed)
              .Append("},");

            var cs = _chartSnapshot;
            sb.Append("\"chartTrend\":{")
              .Append("\"state\":").Append(S(cs != null ? cs.State.ToString() : "Flat")).Append(',')
              .Append("\"lean\":").Append(S(cs != null ? cs.Lean.ToString() : "Flat")).Append(',')
              .Append("\"controllingHigh\":").Append(D(cs?.ControllingHigh ?? double.NaN)).Append(',')
              .Append("\"controllingLow\":").Append(D(cs?.ControllingLow  ?? double.NaN)).Append(',')
              .Append("\"controlPrice\":").Append(D(cs?.ControllingPivotPrice ?? double.NaN))
              .Append("},");

            // Entry / re-entry signals (continuation) — last 10, most recent last
            sb.Append("\"entries\":[");
            if (cs?.Entries != null)
            {
                int en = cs.Entries.Count;
                int estart = Math.Max(0, en - 10);
                bool efirst = true;
                for (int i = estart; i < en; i++)
                {
                    var e = cs.Entries[i];
                    if (!efirst) sb.Append(',');
                    efirst = false;
                    sb.Append('{')
                      .Append("\"timeUtc\":").Append(T(e.Time)).Append(',')
                      .Append("\"side\":").Append(S(e.Side > 0 ? "long" : "short")).Append(',')
                      .Append("\"kind\":").Append(S(e.Kind)).Append(',')
                      .Append("\"price\":").Append(D(e.Price)).Append(',')
                      .Append("\"stop\":").Append(D(e.Stop)).Append(',')
                      .Append("\"target\":").Append(D(e.Target)).Append(',')
                      .Append("\"rr\":").Append(D(e.RR)).Append(',')
                      .Append("\"aligned\":").Append(EntryAligned(e) ? "true" : "false").Append(',')
                      .Append("\"status\":").Append(S(e.Status)).Append(',')
                      .Append("\"resultR\":").Append(D(e.ResultR)).Append(',')
                      .Append("\"trend\":").Append(S(e.Trend.ToString()))
                      .Append('}');
                }
            }
            sb.Append("],");

            // Pending setups — the ARMED zones the bot is waiting for price to retrace into,
            // BEFORE it enters. Nearest to price first. Read-only (doesn't affect taken trades).
            var pend = _chartMachine != null ? _chartMachine.GetPendingSetups() : new List<PendingSetup>();
            pend.Sort((a, b) => Math.Abs(a.Entry - last).CompareTo(Math.Abs(b.Entry - last)));
            sb.Append("\"pendingSetups\":[");
            int pn = Math.Min(8, pend.Count);
            for (int i = 0; i < pn; i++)
            {
                var p = pend[i];
                if (i > 0) sb.Append(',');
                sb.Append('{')
                  .Append("\"side\":").Append(S(p.Side > 0 ? "long" : "short")).Append(',')
                  .Append("\"entry\":").Append(D(p.Entry)).Append(',')
                  .Append("\"stop\":").Append(D(p.Stop)).Append(',')
                  .Append("\"target\":").Append(D(p.Target)).Append(',')
                  .Append("\"rr\":").Append(D(p.RR)).Append(',')
                  .Append("\"formation\":").Append(S(p.Formation)).Append(',')
                  .Append("\"distance\":").Append(D(Math.Abs(p.Entry - last)))
                  .Append('}');
            }
            sb.Append("],");

            // Trade log — win/loss tally of resolved entry outcomes (this chart's TF)
            int wlWins   = cs?.Wins   ?? 0;
            int wlLosses = cs?.Losses ?? 0;
            int wlTotal  = wlWins + wlLosses;
            double wlR   = cs?.TotalR ?? 0;
            sb.Append("\"tradeLog\":{")
              .Append("\"wins\":").Append(wlWins).Append(',')
              .Append("\"losses\":").Append(wlLosses).Append(',')
              .Append("\"breakevens\":").Append(cs?.Scratches ?? 0).Append(',')
              .Append("\"total\":").Append(wlTotal).Append(',')
              .Append("\"winRate\":").Append(wlTotal > 0 ? D((double)wlWins / wlTotal) : "null").Append(',')
              .Append("\"totalR\":").Append(D(wlR)).Append(',')
              .Append("\"avgR\":").Append(wlTotal > 0 ? D(wlR / wlTotal) : "null")
              .Append("},");

            // Full resolved-trade R-series (chronological) for the analytics journal + Monte Carlo.
            var trs = _chartMachine != null ? _chartMachine.GetTradeRs() : (IReadOnlyList<double>)new List<double>();
            sb.Append("\"tradeRs\":[");
            for (int i = 0; i < trs.Count; i++) { if (i > 0) sb.Append(','); sb.Append(D(trs[i])); }
            sb.Append("],");

            // Zone-entry-specific tally — measures the precision of the IOF retrace entries
            // separately from the trend-break entries (so we can see if the filters help).
            int zW = 0, zL = 0, zP = 0;
            if (cs?.Entries != null)
                foreach (var e in cs.Entries)
                    if (e.Kind == "zone") { if (e.Status == "win") zW++; else if (e.Status == "loss") zL++; else zP++; }
            sb.Append("\"zoneLog\":{")
              .Append("\"wins\":").Append(zW).Append(',')
              .Append("\"losses\":").Append(zL).Append(',')
              .Append("\"pending\":").Append(zP).Append(',')
              .Append("\"winRate\":").Append((zW + zL) > 0 ? D((double)zW / (zW + zL)) : "null")
              .Append("},");

            // Funded-account simulation (cached): 1 contract, trailing DD vs target, risk-capped
            var acct = _acctSim;
            sb.Append("\"accountSim\":{")
              .Append("\"passed\":").Append(acct != null && acct.Passed ? "true" : "false").Append(',')
              .Append("\"blown\":").Append(acct != null && acct.Blown ? "true" : "false").Append(',')
              .Append("\"finalDollars\":").Append(D(acct?.FinalDollars ?? 0)).Append(',')
              .Append("\"peakDollars\":").Append(D(acct?.PeakDollars ?? 0)).Append(',')
              .Append("\"maxDrawdown\":").Append(D(acct?.MaxDrawdown ?? 0)).Append(',')
              .Append("\"tradesTaken\":").Append(acct?.TradesTaken ?? 0).Append(',')
              .Append("\"skipped\":").Append(acct?.Skipped ?? 0).Append(',')
              .Append("\"tradesToResult\":").Append(acct?.TradesToResult ?? 0).Append(',')
              .Append("\"ddLimit\":").Append(D(AcctDrawdown)).Append(',')
              .Append("\"target\":").Append(D(AcctTarget)).Append(',')
              .Append("\"maxRiskPerTrade\":").Append(D(AcctMaxRisk)).Append(',')
              .Append("\"pointValue\":").Append(D(AcctPointValue)).Append(',')
              .Append("\"tpRR\":").Append(D(TakeProfitRR))
              .Append("},");

            // ── Risk Governor: the HONEST run (hard daily caps + lockouts + session) ──
            // This is the number that matters: what the account does WITH circuit breakers.
            var g = _governed;
            var gl = g?.Live;
            sb.Append("\"governed\":{")
              .Append("\"status\":").Append(S(gl != null ? gl.Status : "ARMED")).Append(',')
              .Append("\"canTrade\":").Append(gl != null && gl.CanTrade ? "true" : "false").Append(',')
              .Append("\"phase\":").Append(S(gl != null ? gl.Phase : "EVAL")).Append(',')
              .Append("\"balance\":").Append(D(gl?.Balance ?? AcctStartBalance)).Append(',')
              .Append("\"peak\":").Append(D(gl?.Peak ?? AcctStartBalance)).Append(',')
              .Append("\"floor\":").Append(D(gl?.Floor ?? (AcctStartBalance - AcctDrawdown))).Append(',')
              .Append("\"roomToFloor\":").Append(D(gl?.RoomToFloor ?? 0)).Append(',')
              .Append("\"toTarget\":").Append(D(gl?.ToTarget ?? AcctTarget)).Append(',')
              .Append("\"passedEval\":").Append(gl != null && gl.Passed ? "true" : "false").Append(',')
              .Append("\"daysToPass\":").Append(g?.DaysToPass ?? 0).Append(',')
              .Append("\"tradesToPass\":").Append(g?.TradesToPass ?? 0).Append(',')
              .Append("\"tradingDays\":").Append(g?.TradingDays ?? 0).Append(',')
              .Append("\"avgPerDay\":").Append(D(g?.AvgPerDay ?? 0)).Append(',')
              .Append("\"inSession\":").Append(gl == null || gl.InSession ? "true" : "false").Append(',')
              .Append("\"lockedToday\":").Append(gl != null && gl.LockedToday ? "true" : "false").Append(',')
              .Append("\"lossesToday\":").Append(gl?.LossesToday ?? 0).Append(',')
              .Append("\"pnlToday\":").Append(D(gl?.PnlToday ?? 0)).Append(',')
              .Append("\"equity\":").Append(D(g?.FinalDollars ?? 0)).Append(',')
              .Append("\"passed\":").Append(g != null && g.Passed ? "true" : "false").Append(',')
              .Append("\"blown\":").Append(g != null && g.Blown ? "true" : "false").Append(',')
              .Append("\"trades\":").Append(g?.Trades ?? 0).Append(',')
              .Append("\"wins\":").Append(g?.Wins ?? 0).Append(',')
              .Append("\"losses\":").Append(g?.Losses ?? 0).Append(',')
              .Append("\"breakevens\":").Append(g?.Scratches ?? 0).Append(',')
              .Append("\"winRate\":").Append(g != null && (g.Wins + g.Losses) > 0 ? D(g.WinRate) : "null").Append(',')
              .Append("\"avgR\":").Append(D(g?.AvgR ?? 0)).Append(',')
              .Append("\"skipped\":").Append(g?.Skipped ?? 0).Append(',')
              .Append("\"blockedByGuard\":").Append(g?.BlockedByGuard ?? 0).Append(',')
              .Append("\"lockoutDays\":").Append(g?.LockoutDays ?? 0).Append(',')
              .Append("\"evalsRun\":").Append(g?.EvalsRun ?? 1).Append(',')
              .Append("\"passesBanked\":").Append(_passesHighWater).Append(',')
              .Append("\"evalPasses\":").Append(g?.EvalPasses ?? 0).Append(',')
              .Append("\"livePayouts\":").Append(g?.LivePayouts ?? 0).Append(',')
              .Append("\"liveWithdrawn\":").Append(D(g?.LiveWithdrawn ?? 0)).Append(',')
              .Append("\"liveGraduated\":").Append(g?.LiveGraduated ?? 0).Append(',')
              .Append("\"worstEodDD\":").Append(D(g?.WorstEodDD ?? 0)).Append(',')
              .Append("\"longestLossStreak\":").Append(g?.LongestLossStreak ?? 0).Append(',')
              .Append("\"dailyLossCap\":").Append(D(GovDailyLossCap)).Append(',')
              .Append("\"maxLossesPerDay\":").Append(GovMaxLossesPerDay).Append(',');
            // Equity curve — running balance after each trade, downsampled to ~120 points.
            sb.Append("\"equityCurve\":[");
            var ec = g?.EquityCurve;
            if (ec != null && ec.Count > 0)
            {
                int n = ec.Count, step = Math.Max(1, n / 120); bool ef = true;
                for (int i = 0; i < n; i += step) { if (!ef) sb.Append(','); sb.Append(D(ec[i])); ef = false; }
                if ((n - 1) % step != 0) sb.Append(',').Append(D(ec[n - 1]));
            }
            sb.Append("]");
            sb.Append("},");

            // ── 30-eval account-pool projection (spin up fresh on lockout; any blow fails) ──
            var pool = _pool;
            sb.Append("\"pool\":{")
              .Append("\"passes\":").Append(pool?.Passes ?? 0).Append(',')
              .Append("\"blows\":").Append(pool?.Blows ?? 0).Append(',')
              .Append("\"goalTarget\":").Append(pool?.GoalTarget ?? 30).Append(',')
              .Append("\"goalMet\":").Append(pool != null && pool.GoalMet ? "true" : "false").Append(',')
              .Append("\"accountsUsed\":").Append(pool?.AccountsUsed ?? 0).Append(',')
              .Append("\"inProgress\":").Append(pool?.InProgress ?? 0).Append(',')
              .Append("\"tradingDays\":").Append(pool?.TradingDays ?? 0).Append(',')
              .Append("\"payouts\":").Append(pool?.Payouts ?? 0).Append(',')
              .Append("\"withdrawn\":").Append(D(pool?.Withdrawn ?? 0)).Append(',')
              .Append("\"liveAccounts\":").Append(pool?.LiveAccounts ?? 0)
              .Append("},");

            // LIVE account pool (spin-up-on-lockout from the pinned eval start = today forward)
            var poolL = _poolLive;
            sb.Append("\"poolLive\":{")
              .Append("\"passes\":").Append(poolL?.Passes ?? 0).Append(',')
              .Append("\"blows\":").Append(poolL?.Blows ?? 0).Append(',')
              .Append("\"accountsUsed\":").Append(poolL?.AccountsUsed ?? 0).Append(',')
              .Append("\"inProgress\":").Append(poolL?.InProgress ?? 0).Append(',')
              .Append("\"tradingDays\":").Append(poolL?.TradingDays ?? 0).Append(',')
              .Append("\"payouts\":").Append(poolL?.Payouts ?? 0).Append(',')
              .Append("\"withdrawn\":").Append(D(poolL?.Withdrawn ?? 0)).Append(',')
              .Append("\"liveAccounts\":").Append(poolL?.LiveAccounts ?? 0)
              .Append("},");

            // Robust pass-rate + R:R sweep (cached) — to pick the best take-profit
            {
                int pp = _acctPasses, bb = _acctBlows, nn = _acctNeither;
                int dec = pp + bb;
                sb.Append("\"passRate\":{")
                  .Append("\"passes\":").Append(pp).Append(',')
                  .Append("\"blows\":").Append(bb).Append(',')
                  .Append("\"neither\":").Append(nn).Append(',')
                  .Append("\"rate\":").Append(dec > 0 ? D((double)pp / dec) : "null").Append(',')
                  .Append("\"atRR\":").Append(D(TakeProfitRR)).Append(',')
                  .Append("\"bestRR\":").Append(D(_acctBestRR))
                  .Append("},");

                sb.Append("\"stress\":{")
                  .Append("\"worstEodDD\":").Append(D(_stressWorstDD)).Append(',')
                  .Append("\"longestLossStreak\":").Append(_stressStreak).Append(',')
                  .Append("\"fullRunFinal\":").Append(D(_stressFinal)).Append(',')
                  .Append("\"trades\":").Append(_stressTrades)
                  .Append("},");

                sb.Append("\"rrSweep\":[");
                for (int i = 0; i < _rrSweep.Count; i++)
                {
                    var s = _rrSweep[i];
                    if (i > 0) sb.Append(',');
                    int sdec = s.passes + s.blows;
                    sb.Append('{')
                      .Append("\"rr\":").Append(D(s.rr)).Append(',')
                      .Append("\"passes\":").Append(s.passes).Append(',')
                      .Append("\"blows\":").Append(s.blows).Append(',')
                      .Append("\"passRate\":").Append(sdec > 0 ? D((double)s.passes / sdec) : "null").Append(',')
                      .Append("\"winRate\":").Append(D(s.winRate)).Append(',')
                      .Append("\"expR\":").Append(D(s.expR))
                      .Append('}');
                }
                sb.Append("],");
            }

            // Active (unsealed) leg diagnostics for the chart machine
            sb.Append("\"chartLeg\":{")
              .Append("\"dir\":").Append(cs?.ActiveLegDir ?? 0).Append(',')
              .Append("\"extreme\":").Append(D(cs?.ActiveLegExtreme ?? double.NaN)).Append(',')
              .Append("\"start\":").Append(D(cs?.ActiveLegStart ?? double.NaN)).Append(',')
              .Append("\"lastClose\":").Append(D(cs?.LastBarClose ?? double.NaN)).Append(',')
              .Append("\"legsSealed\":").Append(cs?.LegsSealed ?? 0).Append(',')
              .Append("\"pivotsCount\":").Append(cs?.PivotsCount ?? 0).Append(',')
              .Append("\"minLegTicks\":").Append(_chartMachine != null ? _chartMachine.MinLegTicks : 0)
              .Append("},");

            // IOF zones (impulse-base-impulse) — active zones, nearest to price first
            sb.Append("\"zones\":[");
            if (_chartZones != null && _chartZones.Count > 0)
            {
                var zlist = new List<IofZone>();
                foreach (var z in _chartZones) if (z.Active) zlist.Add(z);
                zlist.Sort((a, b) => Math.Abs(a.EntryEdge - last).CompareTo(Math.Abs(b.EntryEdge - last)));
                int zn = Math.Min(16, zlist.Count);
                for (int i = 0; i < zn; i++)
                {
                    var z = zlist[i];
                    if (i > 0) sb.Append(',');
                    sb.Append('{')
                      .Append("\"formation\":").Append(S(z.Formation)).Append(',')
                      .Append("\"side\":").Append(S(z.IsLong ? "demand" : "supply")).Append(',')
                      .Append("\"bodyHi\":").Append(D(z.BodyHi)).Append(',')
                      .Append("\"bodyLo\":").Append(D(z.BodyLo)).Append(',')
                      .Append("\"wickHi\":").Append(D(z.WickHi)).Append(',')
                      .Append("\"wickLo\":").Append(D(z.WickLo)).Append(',')
                      .Append("\"entryEdge\":").Append(D(z.EntryEdge)).Append(',')
                      .Append("\"farWick\":").Append(D(z.FarWick)).Append(',')
                      .Append("\"baseEndUtc\":").Append(T(z.BaseEndTime))
                      .Append('}');
                }
            }
            sb.Append("],");

            sb.Append("\"mtf\":[");
            for (int i = 0; i < TF_COUNT; i++)
            {
                if (i > 0) sb.Append(',');
                var s       = _snapshots[i];
                bool loaded = _feedProcessedCount[i] >= WarmupBars;
                sb.Append('{')
                  .Append("\"tf\":").Append(S(TF_LABELS[i])).Append(',')
                  .Append("\"loaded\":").Append(loaded ? "true" : "false").Append(',')
                  .Append("\"state\":").Append(S(s != null ? s.State.ToString() : "Flat")).Append(',')
                  .Append("\"lean\":").Append(S(s != null ? s.Lean.ToString() : "Flat")).Append(',')
                  .Append("\"controlPrice\":").Append(D(s?.ControllingPivotPrice ?? double.NaN))
                  .Append('}');
            }
            sb.Append("],");

            sb.Append("\"legPivots\":[");
            if (cs?.LegPivots != null)
            {
                int n     = cs.LegPivots.Count;
                int start = Math.Max(0, n - 12);
                bool first = true;
                for (int i = start; i < n; i++)
                {
                    var p = cs.LegPivots[i];
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('{')
                      .Append("\"label\":").Append(S(p.Label)).Append(',')
                      .Append("\"price\":").Append(D(p.Price)).Append(',')
                      .Append("\"timeUtc\":").Append(T(p.Time)).Append(',')
                      .Append("\"isBull\":").Append(p.IsBull ? "true" : "false")
                      .Append('}');
                }
            }
            sb.Append("],");

            // Diagnostics — lets the reader confirm feed ordering for both the chart feed
            // and an MTF (GetHistory) feed, so we can validate the trend engine's bar order.
            sb.Append("\"debug\":{")
              .Append("\"chartBarCount\":").Append(count).Append(',')
              .Append("\"chartNewestFirst\":").Append(newestFirst ? "true" : "false").Append(',')
              .Append("\"chartIdx0Utc\":").Append(hdFirst != null ? T(hdFirst.TimeLeft) : "null").Append(',')
              .Append("\"chartIdxLastUtc\":").Append(hdLast != null ? T(hdLast.TimeLeft) : "null");
            var dFeed = (_feeds != null && _feeds.Length > 2) ? _feeds[2] : null;  // Daily feed
            if (dFeed != null && dFeed.Count >= 2)
            {
                var f0 = dFeed[0]              as HistoryItemBar;
                var fL = dFeed[dFeed.Count - 1] as HistoryItemBar;
                sb.Append(',')
                  .Append("\"dailyFeedCount\":").Append(dFeed.Count).Append(',')
                  .Append("\"dailyIdx0Utc\":").Append(f0 != null ? T(f0.TimeLeft) : "null").Append(',')
                  .Append("\"dailyIdxLastUtc\":").Append(fL != null ? T(fL.TimeLeft) : "null");
            }
            sb.Append('}');

            sb.Append('}');
            return sb.ToString();
        }

        private static string SanitizeSymbol(string s)
        {
            if (string.IsNullOrEmpty(s)) return "UNKNOWN";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "UNKNOWN";
        }

        // JSON formatting helpers
        private static string D(double v) =>
            (double.IsNaN(v) || double.IsInfinity(v))
                ? "null"
                : v.ToString("0.##########", CultureInfo.InvariantCulture);

        private static string S(string v) =>
            v == null ? "null"
                      : "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string T(DateTime t) =>
            "\"" + t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "\"";

        // ── Painting ───────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);
            var g = args.Graphics;
            if (g == null) return;

            // Draw IOF zones (behind structure)
            try { DrawZoneBoxes(g, args.Rectangle); } catch { }

            // Draw chart structure (labels + zones) on current TF
            DrawChartStructure(g, args.Rectangle);

            // Draw entry / re-entry signals
            try { DrawEntrySignals(g, args.Rectangle); } catch { }

            // Draw pending setups (the armed watchlist) the bot is waiting on
            try { DrawPendingSetups(g, args.Rectangle); } catch { }

            // Draw the live ACTIVE trade (broker-style position line + real-time P&L)
            try { DrawActiveTrade(g, args.Rectangle); } catch { }

            // Dashboards
            if (ShowTrendPanel)   DrawPanel(g, args.Rectangle);
            if (ShowAccountPanel) { try { DrawAccountPanel(g, args.Rectangle); } catch { } }
        }

        // ── Chart Structure Drawing ────────────────────────────────────────

        private void DrawChartStructure(Graphics g, Rectangle chartRect)
        {
            if (_chartSnapshot == null) return;

            var win = this.CurrentChart?.MainWindow;
            if (win == null) return;

            // ── Controlling High zone ──────────────────────────────────────
            if (DrawCtrlHigh && !double.IsNaN(_chartSnapshot.ControllingHigh))
            {
                float y = (float)win.CoordinatesConverter.GetChartY(_chartSnapshot.ControllingHigh);
                if (y >= chartRect.Top && y <= chartRect.Bottom)
                {
                    // Fill zone (thin band)
                    int bandH = Math.Max(2, ZoneLineThick * 2);
                    using (var fill = new SolidBrush(Color.FromArgb(ZoneFillAlpha, CtrlHighColor)))
                        g.FillRectangle(fill, chartRect.Left, (int)y - bandH, chartRect.Width, bandH * 2);
                    // Line
                    using (var pen = new Pen(CtrlHighColor, ZoneLineThick))
                        g.DrawLine(pen, chartRect.Left, (int)y, chartRect.Right, (int)y);
                    // Label
                    using (var f = new Font("Consolas", LabelFontSize, FontStyle.Bold))
                    using (var b = new SolidBrush(CtrlHighColor))
                        g.DrawString("Controlling High", f, b, chartRect.Right - 140, y - LabelFontSize - 4);
                }
            }

            // ── Controlling Low zone ───────────────────────────────────────
            if (DrawCtrlLow && !double.IsNaN(_chartSnapshot.ControllingLow))
            {
                float y = (float)win.CoordinatesConverter.GetChartY(_chartSnapshot.ControllingLow);
                if (y >= chartRect.Top && y <= chartRect.Bottom)
                {
                    int bandH = Math.Max(2, ZoneLineThick * 2);
                    using (var fill = new SolidBrush(Color.FromArgb(ZoneFillAlpha, CtrlLowColor)))
                        g.FillRectangle(fill, chartRect.Left, (int)y - bandH, chartRect.Width, bandH * 2);
                    using (var pen = new Pen(CtrlLowColor, ZoneLineThick))
                        g.DrawLine(pen, chartRect.Left, (int)y, chartRect.Right, (int)y);
                    using (var f = new Font("Consolas", LabelFontSize, FontStyle.Bold))
                    using (var b = new SolidBrush(CtrlLowColor))
                        g.DrawString("Controlling Low", f, b, chartRect.Right - 130, y + 4);
                }
            }

            // ── HH / HL / LH / LL labels ──────────────────────────────────
            if (DrawLabels && _chartSnapshot.LegPivots != null)
            {
                using (var f = new Font("Consolas", LabelFontSize, FontStyle.Bold))
                {
                    foreach (var p in _chartSnapshot.LegPivots)
                    {
                        float y, x;
                        try
                        {
                            y = (float)win.CoordinatesConverter.GetChartY(p.Price);
                            x = (float)win.CoordinatesConverter.GetChartX(p.Time);
                        }
                        catch { continue; }

                        if (x < chartRect.Left || x > chartRect.Right) continue;
                        if (y < chartRect.Top  || y > chartRect.Bottom) continue;

                        Color c = p.Label == "HH" ? HHColor :
                                  p.Label == "LL" ? LLColor :
                                  p.Label == "HL" ? HLColor : LHColor;

                        using (var b = new SolidBrush(c))
                        {
                            // Diamond marker
                            float dm = 4f;
                            var diamond = new PointF[]
                            {
                                new PointF(x,      y - dm),
                                new PointF(x + dm, y),
                                new PointF(x,      y + dm),
                                new PointF(x - dm, y)
                            };
                            g.FillPolygon(b, diamond);

                            // Label above (bull) or below (bear) diamond
                            float ly = p.IsBull ? y - dm - LabelFontSize - 2 : y + dm + 2;
                            g.DrawString(p.Label, f, b, x - 8, ly);
                        }
                    }
                }
            }
        }

        // ── IOF Zone Drawing ───────────────────────────────────────────────

        private void DrawZoneBoxes(Graphics g, Rectangle chartRect)
        {
            if (!DrawZones || _chartZones == null || _chartZones.Count == 0) return;
            var win = this.CurrentChart?.MainWindow;
            if (win == null) return;

            using (var lf = new Font("Consolas", Math.Max(6, LabelFontSize - 1), FontStyle.Bold))
            foreach (var z in _chartZones)
            {
                if (!z.Active) continue;
                double top = z.IsLong ? z.BodyHi : z.WickHi;
                double bot = z.IsLong ? z.WickLo : z.BodyLo;
                float yTop, yBot, xStart;
                try
                {
                    yTop   = (float)win.CoordinatesConverter.GetChartY(top);
                    yBot   = (float)win.CoordinatesConverter.GetChartY(bot);
                    xStart = (float)win.CoordinatesConverter.GetChartX(z.BaseStartTime);
                }
                catch { continue; }

                float y = Math.Min(yTop, yBot);
                float h = Math.Abs(yBot - yTop);
                if (y + h < chartRect.Top || y > chartRect.Bottom) continue;
                float x = Math.Max(chartRect.Left, xStart);
                float w = chartRect.Right - x;
                if (w <= 0 || h <= 0) continue;

                Color fill = z.IsLong ? DemandZoneColor : SupplyZoneColor;
                using (var b = new SolidBrush(fill))
                    g.FillRectangle(b, x, y, w, h);
                using (var p = new Pen(Color.FromArgb(160, fill.R, fill.G, fill.B)))
                    g.DrawRectangle(p, x, y, w, h);
                using (var lb = new SolidBrush(Color.FromArgb(230, fill.R, fill.G, fill.B)))
                    g.DrawString(z.Formation, lf, lb, x + 2, y + 1);
            }
        }

        // ── Account Dashboard ──────────────────────────────────────────────

        private void DrawAccountPanel(Graphics g, Rectangle chartRect)
        {
            var gv = _governed;
            var gl = gv?.Live;
            if (gl == null) return;

            var snap = _chartSnapshot;
            int tradeRows = (snap?.Entries != null) ? Math.Min(6, snap.Entries.Count) : 0;

            int rowH = Math.Max(14, RowHeight);
            int rows = 12 + 1 + tradeRows;         // title+11 stat rows, divider, trade rows
            int w = 250;
            int h = rows * rowH + 8;
            int x = chartRect.Left + PanelX;
            int y = chartRect.Bottom - h - PanelY;   // lower-left corner

            Color green = Color.FromArgb(40, 220, 130);
            Color red   = Color.FromArgb(235, 70, 70);
            Color amber = Color.FromArgb(240, 200, 70);
            Color blue  = Color.FromArgb(120, 200, 255);

            using (var bg = new SolidBrush(PanelBgColor)) g.FillRectangle(bg, x, y, w, h);
            using (var bpen = new Pen(BorderColor)) g.DrawRectangle(bpen, x, y, w - 1, h - 1);

            float fs = Math.Max(7f, FontSize);
            using (var tf = new Font("Consolas", fs, FontStyle.Bold))
            using (var rf = new Font("Consolas", fs))
            {
                int cy = y + 3;

                Color phaseC = gl.Phase == "FUNDED" ? green : gl.Phase == "BLOWN" ? red : blue;
                using (var tb = new SolidBrush(phaseC))
                    g.DrawString("IOF  " + gl.Phase + "  -  " + gl.Status, tf, tb, x + 5, cy);
                cy += rowH;

                void Row(string label, string val, Color c)
                {
                    using (var lb = new SolidBrush(LabelTextColor)) g.DrawString(label, rf, lb, x + 5, cy);
                    var sz = g.MeasureString(val, tf);
                    using (var vb = new SolidBrush(c)) g.DrawString(val, tf, vb, x + w - 5 - sz.Width, cy);
                    cy += rowH;
                }

                Row("balance",  "$" + gl.Balance.ToString("N0"), gl.Balance >= AcctStartBalance ? green : red);
                Row("floor",    "$" + gl.Floor.ToString("N0"), red);
                Row("room",     "$" + gl.RoomToFloor.ToString("N0"), gl.RoomToFloor > GovDailyLossCap ? green : amber);
                Row(gl.Passed ? "phase" : "to pass",
                    gl.Passed ? "FUNDED" : "$" + gl.ToTarget.ToString("N0"), gl.Passed ? green : blue);
                Row("today",    (gl.PnlToday >= 0 ? "+$" : "-$") + Math.Abs(gl.PnlToday).ToString("N0"),
                    gl.PnlToday >= 0 ? green : red);
                Row("can trade", gl.CanTrade ? "YES" : gl.Status,
                    gl.CanTrade ? green : gl.Status == "BLOWN" ? red : amber);
                Row("trades",   gv.Trades + "  W" + gv.Wins + " L" + gv.Losses + " BE" + gv.Scratches, Color.White);
                Row("win/avgR", (gv.Wins + gv.Losses) > 0
                    ? (gv.WinRate * 100).ToString("F0") + "%  " + gv.AvgR.ToString("F2") + "R" : "-", Color.White);
                Row("pass in", gv.DaysToPass > 0 ? gv.DaysToPass + "d / " + gv.TradesToPass + "tr" : "not yet", blue);
                Row("$/day", ((gv.AvgPerDay >= 0 ? "+$" : "-$") + Math.Abs(gv.AvgPerDay).ToString("N0"))
                    + "  /" + gv.TradingDays + "d", gv.AvgPerDay >= 0 ? green : red);
                Row("eval #", "#" + gv.EvalsRun + "   " + gv.EvalPasses + " passed", gv.EvalPasses > 0 ? green : blue);

                using (var db = new SolidBrush(Color.FromArgb(150, 150, 150)))
                    g.DrawString("- recent trades -", rf, db, x + 5, cy);
                cy += rowH;

                if (snap?.Entries != null)
                {
                    int en = snap.Entries.Count;
                    for (int i = en - 1; i >= 0 && i >= en - tradeRows; i--)
                    {
                        var e = snap.Entries[i];
                        bool win = e.Status == "win", loss = e.Status == "loss";
                        Color c = win ? green : loss ? red : amber;
                        string lbl = (e.Side > 0 ? "L " : "S ") + ((int)e.Price);
                        string res = win ? "WIN +" + e.ResultR.ToString("F1") + "R"
                                   : loss ? "LOSS " + e.ResultR.ToString("F1") + "R"
                                   :        "OPEN " + e.RR.ToString("F1") + "R";
                        using (var lb = new SolidBrush(c)) g.DrawString(lbl, rf, lb, x + 5, cy);
                        var sz = g.MeasureString(res, rf);
                        using (var vb = new SolidBrush(c)) g.DrawString(res, rf, vb, x + w - 5 - sz.Width, cy);
                        cy += rowH;
                    }
                }
            }
        }

        // ── HTF bias gate ──────────────────────────────────────────────────
        // HTF bias = the 4h lean (the user's anchor TF). An entry is "aligned" when its
        // side matches: Bear→short, Bull→long. Flat HTF = stand aside (nothing aligned).
        private TrendState HtfBias()
        {
            var s = (_snapshots != null && _snapshots.Length > 3) ? _snapshots[3] : null; // 4H
            return s != null ? s.Lean : TrendState.Flat;
        }

        private bool EntryAligned(TrendEntry e)
        {
            var bias = HtfBias();
            if (bias == TrendState.Bull) return e.Side > 0;
            if (bias == TrendState.Bear) return e.Side < 0;
            return false;
        }

        // ── Entry Signal Drawing ───────────────────────────────────────────

        private void DrawEntrySignals(Graphics g, Rectangle chartRect)
        {
            if (!DrawEntries) return;
            var snap = _chartSnapshot;
            if (snap?.Entries == null || snap.Entries.Count == 0) return;
            var win = this.CurrentChart?.MainWindow;
            if (win == null) return;

            int n = snap.Entries.Count;
            float s = EntryMarkerSize;
            var spec = DetectContractSpec(this.Symbol?.Name);
            double riskMult = _chartTfMinutes >= 240 ? 1.67 : _chartTfMinutes >= 60 ? 1.5 : 1.0;

            // Outcome palette — winners and losers must be unmistakable at a glance.
            Color winCol  = Color.FromArgb(40, 220, 130);   // bright green
            Color lossCol = Color.FromArgb(235, 70, 70);    // red
            Color openCol = Color.FromArgb(240, 200, 70);   // amber = live/pending

            using (var f = new Font("Consolas", Math.Max(6, LabelFontSize), FontStyle.Bold))
            {
                for (int i = 0; i < n; i++)
                {
                    var e = snap.Entries[i];

                    // Only the CURRENT eval's trades — anything before the pinned eval start belongs
                    // to a prior account or stale history, so don't draw it on this fresh eval.
                    if (e.Time < PinnedEvalStartUtc()) continue;

                    bool aligned = EntryAligned(e);
                    if (HideCounterTrendEntries && !aligned) continue;  // optional HTF gate

                    // Only mark trades the ACCOUNT actually takes: if the stop is too wide for the
                    // risk cap (sizes to 0 contracts) the account skips it, so don't draw it as a
                    // position. Chart = what the account does, not raw strategy signals.
                    double rPer1 = Math.Abs(e.Price - e.Stop) * spec.pv;
                    int eContracts = rPer1 > 0 ? Math.Min((int)Math.Floor(AcctMaxRisk * riskMult / rPer1), spec.maxC) : 0;
                    if (!ManualSignalMode && eContracts < 1) continue;   // account mode hides unsized; manual mode shows ALL signals

                    bool isWin     = e.Status == "win";
                    bool isLoss    = e.Status == "loss";
                    bool isScratch = e.Status == "scratch";
                    bool isOpen    = !isWin && !isLoss && !isScratch;

                    float x, y;
                    try
                    {
                        x = (float)win.CoordinatesConverter.GetChartX(e.Time);
                        y = (float)win.CoordinatesConverter.GetChartY(e.Price);
                    }
                    catch { continue; }
                    if (x < chartRect.Left - 200 || x > chartRect.Right) continue;
                    if (y < chartRect.Top  || y > chartRect.Bottom) continue;

                    // Direction tint for the marker; outcome drives the text + bracket.
                    Color dir = e.Side > 0 ? LongEntryColor : ShortEntryColor;
                    Color scratchCol = Color.FromArgb(150, 160, 170);   // grey = breakeven
                    Color oc  = isWin ? winCol : isLoss ? lossCol : isScratch ? scratchCol : openCol;

                    // Triangle marker (up = long, down = short), white outline for contrast.
                    PointF[] tri = e.Side > 0
                        ? new[] { new PointF(x, y - s), new PointF(x - s, y + s), new PointF(x + s, y + s) }
                        : new[] { new PointF(x, y + s), new PointF(x - s, y - s), new PointF(x + s, y - s) };
                    using (var b = new SolidBrush(dir))
                        g.FillPolygon(b, tri);
                    using (var op = new Pen(Color.White, 1.2f))
                        g.DrawPolygon(op, tri);

                    // Outcome text — the WIN/LOSS difference you asked for, color-coded.
                    string side = e.Side > 0 ? "LONG"  : "SHORT";
                    string txt  = isWin     ? "WIN +"  + e.ResultR.ToString("F1") + "R"
                                : isLoss    ? "LOSS "  + e.ResultR.ToString("F1") + "R"
                                : isScratch ? "BE  0R"
                                :             "OPEN "  + e.RR.ToString("F1") + "R";
                    float ly = e.Side > 0 ? y - s - LabelFontSize - 3 : y + s + 3;
                    using (var tb = new SolidBrush(oc))
                        g.DrawString(side + "  " + txt, f, tb, x + s + 2, ly);

                    // Stop/target bracket. Full live ray for the latest + any still-open trade;
                    // a short stub for resolved history so the chart stays clean.
                    bool liveBracket = isOpen || i >= n - 1;
                    try
                    {
                        float ys = (float)win.CoordinatesConverter.GetChartY(e.Stop);
                        float yt = (float)win.CoordinatesConverter.GetChartY(e.Target);
                        float x2 = liveBracket ? Math.Min(chartRect.Right, x + 170) : x + 34;

                        using (var ep = new Pen(dir, liveBracket ? 2f : 1f))   // entry level
                            g.DrawLine(ep, x, y, x2, y);
                        using (var sp = new Pen(Color.FromArgb(liveBracket ? 220 : 130, lossCol),
                               liveBracket ? 1.6f : 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                            g.DrawLine(sp, x, ys, x2, ys);
                        using (var tp = new Pen(Color.FromArgb(liveBracket ? 220 : 130, winCol),
                               liveBracket ? 1.6f : 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                            g.DrawLine(tp, x, yt, x2, yt);

                        if (liveBracket)
                        {
                            using (var lb  = new SolidBrush(lossCol))
                                g.DrawString("SL", f, lb, x2 + 2, ys - LabelFontSize / 2f);
                            using (var lb2 = new SolidBrush(winCol))
                                g.DrawString("TP", f, lb2, x2 + 2, yt - LabelFontSize / 2f);
                        }
                    }
                    catch { }
                }
            }
        }

        // Current live price = the forming bar's close (ticks in real-time between bar closes).
        private double CurrentLivePrice()
        {
            var hd = this.HistoricalData;
            if (hd == null || hd.Count == 0) return double.NaN;
            var b0 = hd[0] as HistoryItemBar;
            var bl = hd[hd.Count - 1] as HistoryItemBar;
            bool newestFirst = b0 != null && bl != null && b0.TimeLeft > bl.TimeLeft;
            var forming = newestFirst ? b0 : bl;
            return forming?.Close ?? double.NaN;
        }

        // ── Active trade overlay (broker-style live position + P&L) ─────────
        private void DrawActiveTrade(Graphics g, Rectangle chartRect)
        {
            if (!DrawActivePosition || _chartMachine == null) return;
            var win = this.CurrentChart?.MainWindow;
            if (win == null) return;
            var open = _chartMachine.GetLatestOpenTrade();
            if (open == null || open.RiskPts <= 0) return;
            if (open.EntryTime < PinnedEvalStartUtc()) return;   // pre-pin trade — the fresh eval was never in it
            double live = CurrentLivePrice();
            if (double.IsNaN(live)) return;

            int side = open.Side;
            double entry = open.Entry, stop = open.Stop, riskPts = open.RiskPts;
            double target = entry + side * TakeProfitRR * riskPts;
            double curR   = (side > 0 ? (live - entry) : (entry - live)) / riskPts;

            // Stop is a HARD exit: the moment price touches it you're out at ~-1R. Never render the
            // position running past the stop — that's just where price went AFTER stopping you out.
            // (The bar-close logic records the stop at -1R regardless of the overshoot.)
            bool breached = side > 0 ? live <= stop : live >= stop;
            if (breached) { live = stop; curR = -1.0; }
            // Also cap a winner at the take-profit — you're out there too.
            if (curR >= TakeProfitRR) { curR = TakeProfitRR; live = target; }

            var spec = DetectContractSpec(this.Symbol?.Name);
            double riskMult = _chartTfMinutes >= 240 ? 1.67 : _chartTfMinutes >= 60 ? 1.5 : 1.0;
            double riskPer1 = riskPts * spec.pv;
            int contracts = riskPer1 > 0 ? Math.Min((int)Math.Floor(AcctMaxRisk * riskMult / riskPer1), spec.maxC) : 0;
            if (!ManualSignalMode && contracts < 1) return;   // account mode hides unsized trades
            if (contracts < 1) contracts = 1;                 // manual mode: show it, P&L at 1 contract
            double curDollars = curR * riskPer1 * contracts;

            float yE, yS, yT, yL;
            try
            {
                yE = (float)win.CoordinatesConverter.GetChartY(entry);
                yS = (float)win.CoordinatesConverter.GetChartY(stop);
                yT = (float)win.CoordinatesConverter.GetChartY(target);
                yL = (float)win.CoordinatesConverter.GetChartY(live);
            }
            catch { return; }

            bool profit = curR >= 0;
            Color pc = profit ? Color.FromArgb(40, 220, 130) : Color.FromArgb(235, 70, 70);
            int L = chartRect.Left, R = chartRect.Right;

            // Shaded live P&L region between entry and current price.
            using (var fill = new SolidBrush(Color.FromArgb(26, pc)))
                g.FillRectangle(fill, L, Math.Min(yE, yL), R - L, Math.Max(1f, Math.Abs(yL - yE)));

            using (var ep = new Pen(Color.FromArgb(235, 255, 205, 80), 1.7f))                                   // entry (amber, solid)
                g.DrawLine(ep, L, yE, R, yE);
            using (var sp = new Pen(Color.FromArgb(170, 235, 70, 70), 1.2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                g.DrawLine(sp, L, yS, R, yS);                                                                   // stop
            using (var tp = new Pen(Color.FromArgb(170, 40, 200, 120), 1.2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                g.DrawLine(tp, L, yT, R, yT);                                                                   // target
            using (var lp = new Pen(pc, 1.5f))                                                                  // live price (bright)
                g.DrawLine(lp, L, yL, R, yL);

            using (var f = new Font("Consolas", Math.Max(8, LabelFontSize + 1), FontStyle.Bold))
            {
                using (var b = new SolidBrush(Color.FromArgb(255, 255, 205, 80)))
                    g.DrawString((side > 0 ? "▲ ACTIVE LONG  @ " : "▼ ACTIVE SHORT  @ ") + ((int)entry), f, b, L + 8, yE - LabelFontSize - 5);
                string pnl = (curR >= 0 ? "+" : "") + curR.ToString("F2") + "R    "
                           + (curDollars >= 0 ? "+$" : "-$") + Math.Abs(curDollars).ToString("N0")
                           + "    " + live.ToString("F0");
                using (var b = new SolidBrush(pc))
                    g.DrawString(pnl, f, b, L + 8, yL + 3);
                using (var b = new SolidBrush(Color.FromArgb(190, 235, 70, 70)))
                    g.DrawString("SL " + ((int)stop), f, b, R - 95, yS - LabelFontSize - 3);
                using (var b = new SolidBrush(Color.FromArgb(190, 40, 200, 120)))
                    g.DrawString("TP " + ((int)target), f, b, R - 95, yT - LabelFontSize - 3);
            }
        }

        // ── Pending setups (armed watchlist, ALL feeds on every chart) ──────
        private class PendLevel { public int Side; public double Entry, Stop, Target; public List<string> Tfs = new List<string>(); }

        private static string Match1(string s, string pat, string dflt)
        { var m = Regex.Match(s, pat); return m.Success ? m.Groups[1].Value : dflt; }
        private static double ParseD(string s)
        => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        private static string TfLabel(int m) => m <= 0 ? "?" : m >= 1440 ? (m / 1440) + "d" : m >= 60 ? (m / 60) + "h" : m + "m";

        // Gather pending setups from EVERY exported snapshot so each chart can draw them all.
        private void ReadCrossSetupsThrottled()
        {
            if ((DateTime.UtcNow - _crossLastRead).TotalSeconds < 3) return;
            _crossLastRead = DateTime.UtcNow;
            var list = new List<(string, int, int, double, double, double)>();
            try
            {
                if (!Directory.Exists(ExportDir)) { _crossSetups = list; return; }
                foreach (var file in Directory.GetFiles(ExportDir, "snapshot_*.json"))
                {
                    string txt;
                    try { txt = File.ReadAllText(file); } catch { continue; }
                    string sym = Match1(txt, "\"symbol\":\"([^\"]+)\"", "");
                    int tf = (int)ParseD(Match1(txt, "\"chartTfMinutes\":([0-9]+)", "0"));
                    var ps = Regex.Match(txt, "\"pendingSetups\":\\[(.*?)\\]");
                    if (!ps.Success) continue;
                    foreach (Match m in Regex.Matches(ps.Groups[1].Value, "\\{[^}]*\\}"))
                    {
                        string o = m.Value;
                        double en = ParseD(Match1(o, "\"entry\":([0-9.]+)", "NaN"));
                        if (double.IsNaN(en)) continue;
                        int side = Match1(o, "\"side\":\"(\\w+)\"", "long") == "long" ? 1 : -1;
                        list.Add((sym, tf, side, en,
                            ParseD(Match1(o, "\"stop\":([0-9.]+)", "NaN")),
                            ParseD(Match1(o, "\"target\":([0-9.]+)", "NaN"))));
                    }
                }
            }
            catch { }
            _crossSetups = list;
        }

        private void DrawPendingSetups(Graphics g, Rectangle chartRect)
        {
            if (!DrawPending) return;
            var win = this.CurrentChart?.MainWindow;
            if (win == null) return;
            var src = _crossSetups;
            if (src == null || src.Count == 0) return;

            // Aggregate by side + price level so the same level across TFs draws ONCE (= confluence).
            double tick = Math.Max(_tickSize, 0.01);
            var agg = new Dictionary<string, PendLevel>();
            foreach (var s in src)
            {
                string key = s.side + ":" + Math.Round(s.entry / tick);
                if (!agg.TryGetValue(key, out var pl))
                { pl = new PendLevel { Side = s.side, Entry = s.entry, Stop = s.stop, Target = s.target }; agg[key] = pl; }
                string tag = (string.IsNullOrEmpty(s.sym) ? "" : s.sym + " ") + TfLabel(s.tf);
                if (!pl.Tfs.Contains(tag)) pl.Tfs.Add(tag);
            }

            using (var f = new Font("Consolas", Math.Max(6, LabelFontSize), FontStyle.Bold))
            foreach (var pl in agg.Values)
            {
                float yE;
                try { yE = (float)win.CoordinatesConverter.GetChartY(pl.Entry); } catch { continue; }
                if (yE < chartRect.Top - 40 || yE > chartRect.Bottom + 40) continue;

                // Which timeframes share this level? (each tag is "SYM tflabel")
                var tfSet = new HashSet<string>();
                foreach (var t in pl.Tfs) { int sp = t.LastIndexOf(' '); tfSet.Add(sp >= 0 ? t.Substring(sp + 1) : t); }
                // GOLDILOCKS: a level nested across 5m + 15m + 1h → draw YELLOW (the A+ confluence).
                bool goldilocks = tfSet.Contains("5m") && tfSet.Contains("15m") && tfSet.Contains("1h");
                bool conf = pl.Tfs.Count > 1;

                Color dir  = pl.Side > 0 ? Color.FromArgb(40, 220, 130) : Color.FromArgb(235, 70, 70);
                Color line = goldilocks ? Color.FromArgb(255, 215, 60) : dir;
                float wid  = goldilocks ? 2.4f : conf ? 1.9f : 1.1f;
                int   alpha = goldilocks ? 255 : conf ? 220 : 130;
                using (var ep = new Pen(Color.FromArgb(alpha, line), wid)
                       { DashStyle = goldilocks ? System.Drawing.Drawing2D.DashStyle.Solid : System.Drawing.Drawing2D.DashStyle.Dash })
                    g.DrawLine(ep, chartRect.Left, yE, chartRect.Right, yE);

                string head = goldilocks ? "★ GOLDILOCKS " : (pl.Side > 0 ? "WATCH LONG " : "WATCH SHORT ");
                string lbl  = head + ((int)pl.Entry) + "  [" + string.Join(",", pl.Tfs) + "]";
                using (var b = new SolidBrush(goldilocks ? Color.FromArgb(255, 215, 60) : dir))
                    g.DrawString(lbl, f, b, chartRect.Left + 6, yE - LabelFontSize - 3);
            }
        }

        // ── Panel Drawing ──────────────────────────────────────────────────

        private void DrawPanel(Graphics g, Rectangle chartRect)
        {
            bool[] vis = TF_VISIBLE;
            int visibleRows = 0;
            for (int i = 0; i < TF_COUNT; i++)
                if (vis[i]) visibleRows++;

            if (visibleRows == 0) return;

            int titleH      = ShowTitle ? RowHeight : 0;
            int panelHeight = visibleRows * RowHeight + titleH + 4;
            int x           = chartRect.Left + PanelX;
            int y           = chartRect.Top  + PanelY;

            using (var bgBrush  = new SolidBrush(PanelBgColor))
                g.FillRectangle(bgBrush, x, y, PanelWidth, panelHeight);
            using (var borderPen = new Pen(BorderColor))
                g.DrawRectangle(borderPen, x, y, PanelWidth - 1, panelHeight - 1);

            float fSize  = Math.Max(6f, FontSize);
            int   stateW = PanelWidth - LabelColWidth - 8;
            int   rowX   = x + 4;
            int   stateX = rowX + LabelColWidth;
            int   curY   = y + 2;

            using (var labelFont = new Font("Consolas", fSize, FontStyle.Bold))
            using (var stateFont = new Font("Consolas", fSize, FontStyle.Bold))
            using (var titleFont = new Font("Consolas", fSize - 1f > 6f ? fSize - 1f : 6f, FontStyle.Bold))
            {
                if (ShowTitle)
                {
                    using (var titleBrush = new SolidBrush(TitleTextColor))
                    {
                        string title = "IOF TREND";
                        var sz = g.MeasureString(title, titleFont);
                        g.DrawString(title, titleFont, titleBrush,
                            x + (PanelWidth - sz.Width) / 2f,
                            curY + (RowHeight - sz.Height) / 2f);
                    }
                    using (var sepPen = new Pen(BorderColor))
                        g.DrawLine(sepPen, x, curY + RowHeight, x + PanelWidth - 1, curY + RowHeight);
                    curY += RowHeight;
                }

                for (int i = 0; i < TF_COUNT; i++)
                {
                    if (!vis[i]) continue;

                    int rowY = curY;
                    curY += RowHeight;

                    using (var lb = new SolidBrush(LabelTextColor))
                        g.DrawString(TF_LABELS[i], labelFont, lb, rowX, rowY + (RowHeight - FontSize) / 2f - 1);

                    var snap    = _snapshots[i];
                    bool loaded = _feedProcessedCount[i] >= WarmupBars;
                    TrendState state = snap != null ? snap.State : TrendState.Flat;

                    Color  bgColor;
                    string stateLabel;

                    if (!loaded)
                    {
                        bgColor    = LoadingColor;
                        stateLabel = "LOADING";
                    }
                    else
                    {
                        switch (state)
                        {
                            case TrendState.Bull: bgColor = BullColor; stateLabel = "BULL"; break;
                            case TrendState.Bear: bgColor = BearColor; stateLabel = "BEAR"; break;
                            default:              bgColor = FlatColor;  stateLabel = "FLAT"; break;
                        }
                    }

                    using (var stateBg = new SolidBrush(bgColor))
                        g.FillRectangle(stateBg, stateX, rowY + 1, stateW, RowHeight - 3);

                    using (var stBrush = new SolidBrush(StateTextColor))
                    {
                        if (ShowControlPrice && loaded && state != TrendState.Flat && snap != null
                            && !double.IsNaN(snap.ControllingPivotPrice))
                        {
                            string priceStr = snap.ControllingPivotPrice.ToString("F2");
                            var labSz  = g.MeasureString(stateLabel, stateFont);
                            var priceSz = g.MeasureString(priceStr, stateFont);
                            float ly = rowY + (RowHeight - labSz.Height)  / 2f;
                            float py = rowY + (RowHeight - priceSz.Height) / 2f;
                            g.DrawString(stateLabel, stateFont, stBrush, stateX + 3, ly);
                            float px = stateX + stateW - priceSz.Width - 3;
                            if (px > stateX + labSz.Width + 4)
                                g.DrawString(priceStr, stateFont, stBrush, px, py);
                        }
                        else
                        {
                            var sz = g.MeasureString(stateLabel, stateFont);
                            g.DrawString(stateLabel, stateFont, stBrush,
                                stateX + (stateW - sz.Width)   / 2f,
                                rowY   + (RowHeight - sz.Height) / 2f);
                        }
                    }

                    if (ShowBarCount && loaded)
                    {
                        string cnt = _feedProcessedCount[i].ToString();
                        using (var cntBrush = new SolidBrush(Color.FromArgb(120, 120, 120)))
                        using (var cntFont  = new Font("Consolas", Math.Max(6f, fSize - 2f)))
                            g.DrawString(cnt, cntFont, cntBrush, rowX, rowY + RowHeight - cntFont.Height - 1);
                    }

                    using (var sepPen = new Pen(Color.FromArgb(40, 80, 80, 80)))
                        g.DrawLine(sepPen, x + 1, rowY + RowHeight - 1, x + PanelWidth - 2, rowY + RowHeight - 1);
                }
            }
        }
    }
}
