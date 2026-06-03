// =============================================================================
// TradePhantoms_IOF_v2.cs — Master indicator integration (Path A full rebuild)
// =============================================================================
// Quantower indicator integrating five v2/ component files:
//
//   * TradeLifecycle.cs       (TradePhantomsIOF.Lifecycle)
//   * TrailStrategies.cs      (TradePhantomsIOF.Trail)
//   * StatsStripRenderer.cs   (TradePhantomsIOF.UI)
//   * DashboardRenderer.cs    (TradePhantomsIOF.UI)
//   * EntryAndTPHelpers.cs    (TradePhantomsIOF / EntryTPMath)
//
// Ports zone detection + 20-point IOF rubric from the v1 file
// (../TradePhantoms_IOF.cs) verbatim. The lifecycle layer (PASS A→B→C→D),
// trail dispatcher, status dashboard, closed-trades table, and stats strip
// all live in the components — this master file is the integration glue.
//
// HARD EXCLUSIONS: NO BOS / CHoCH anywhere (verified by final grep).
// Pine v1.4 lifecycle bugs 1-7 are prevented inside TradeLifecycle.cs; this
// file does not cache TradeRecord state across passes, does not write
// HighestTpHit, and does not override CurSL outside the lifecycle.
//
// -----------------------------------------------------------------------------
// PATCH NOTES
// -----------------------------------------------------------------------------
// 2026-05-06: Trend feature integrated.
//   - TrendStateMachine wired in OnInit; bar-by-bar updates in OnUpdate
//   - Optional CloseOnTrendBroken propagated to lifecycle
//   - Control point markers + trend break markers drawn in OnPaintChart
//   - Zone scoring's Trend factor refreshes live from the state machine
//   - Strip and Dashboard surfaces show current trend state
//   - Alerts wired for ControlPointDetected, TrendBroken, TrendChanged
//
// 2026-05-05 (re-audit phase 3 — three small targeted fixes):
//   * Lifecycle alerts diff (DetectAndFireLifecycleAlerts) is now invoked
//     INSIDE the tradesLock around RunLifecycleForCurrentBar — the prior
//     comment claimed it was, but the call sat outside the lock and could
//     race with OnNewLast's RefreshLiveR mutating R / DollarPnL / CurSL.
//   * OnSettingsUpdated now re-fetches MTF data (and disposes stale feeds)
//     when UseMTFZones / ITFPeriod / HTFPeriod / MTFLookbackBars change at
//     runtime, instead of waiting until the next chart reload.
//   * MTFC bonus is now folded into IofZone.Score BEFORE the MinScore
//     filter (was applied after, defeating the point near the cutoff).
//     IofZone.Score promoted from int to double so the bonus keeps full
//     precision. RescanMTFZones now runs before ScanZones so the overlap
//     set is current; ApplyMTFCBonus split into ComputeMTFCBonusedIds +
//     in-loop bonus mutation inside ScanZones.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.History.Aggregations;

// Component namespaces
using TPLifecycle = TradePhantomsIOF.Lifecycle;
using TPTrail    = TradePhantomsIOF.Trail;
using TPUI       = TradePhantomsIOF.UI;
using TPMath     = TradePhantomsIOF.EntryTPMath;
using TPMTF      = TradePhantomsIOF.MultiTF;
using TPAlerts   = TradePhantomsIOF.Alerts;

namespace TradePhantomsIOF
{
    // =========================================================================
    // Master indicator class
    // =========================================================================
    /// <summary>
    /// Quantower indicator — integrates the v2 component stack.
    /// Inherits from <see cref="Indicator"/> and implements
    /// <see cref="IVolumeAnalysisIndicator"/> for opportunistic HVN scoring.
    /// </summary>
    public class TradePhantoms_IOF_v2 : Indicator, IVolumeAnalysisIndicator
    {
        // ---------------------------------------------------------------------
        // INPUTS — Detection
        // ---------------------------------------------------------------------
        [InputParameter("Min score (0-21)", 1, 0, 21, 1, 0)]
        public int MinScore = 9;

        // Visual: faint dashed entry/SL/TP lines for every tradeable zone so
        // the user sees the plan in advance. Lifecycle's bold lines overlay
        // these when a zone actually ARMs.
        [InputParameter("Show reference entry/SL/TP lines on tradeable zones", 6)]
        public bool ShowReferenceLines = true;

        // Visibility threshold (separate from trade-eligibility threshold).
        // Zones with score >= MinDrawScore stay visible (drawn dim if < MinScore);
        // only zones with score >= MinScore are trade-eligible (can ARM).
        // This prevents the "zone disappears on touch" surprise: when Purity
        // degrades a 14/21 zone to 12 after a touch, the zone stays visible
        // (you can see what got touched) but isn't tradeable until/unless its
        // score recovers (it won't — Purity is monotonic). The zone vanishes
        // only when it's invalidated (close past far wick) per TP doctrine.
        [InputParameter("Min Draw Score (visibility threshold; zones below MinScore drawn dim)", 7)]
        public int MinDrawScore = 8;

        [InputParameter("Base candle max body % of range", 2, 0.0, 1.0, 0.05, 2)]
        public double BaseCandleMaxBodyPct = 0.5;

        [InputParameter("Min impulse / base ratio", 3, 1.0, 10.0, 0.1, 1)]
        public double MinImpulseRatio = 2.0;

        [InputParameter("Max base candles", 4, 1, 20, 1, 0)]
        public int MaxBaseCandles = 7;

        [InputParameter("Lookback bars to scan", 5, 50, 5000, 50, 0)]
        public int LookbackBars = 500;

        [InputParameter("Show demand zones", 6)]
        public bool ShowDemandZones = true;

        [InputParameter("Show supply zones", 7)]
        public bool ShowSupplyZones = true;

        // ---------------------------------------------------------------------
        // INPUTS — Entry & TPs
        // ---------------------------------------------------------------------
        [InputParameter("OF entry depth", 10, variants: new object[]
        {
            "Front of zone", OFEntryLevel.Front,
            "0% (lip)",      OFEntryLevel.Of0,
            "25% into wick", OFEntryLevel.Of25,
            "50% (mid)",     OFEntryLevel.Of50,
            "75% deep",      OFEntryLevel.Of75
        })]
        public OFEntryLevel OFEntry = OFEntryLevel.Of0;

        [InputParameter("TP count (1-5)", 11, 1, 5, 1, 0)]
        public int TpCount = 3;

        [InputParameter("TP step (× zone height)", 12, 0.1, 10.0, 0.1, 2)]
        public double TpStep = 1.0;

        [InputParameter("Stop buffer (ticks)", 13, 0, 50, 1, 0)]
        public int StopBufferTicks = 2;

        // 2026-05-11: R:R target source. Drives EstimateTargetPrice (which
        // feeds both the score band AND the displayed z.EstimatedRrr).
        //   Structural — next opposing-direction active zone, priority
        //                HTF -> ITF -> chart-TF. Use its frontside edge
        //                (BodyLo for supply targets, BodyHi for demand
        //                targets). Best for IOF doctrine ("zones target
        //                zones"). Falls back to VA HVN, then 60-bar
        //                extreme when no opposing zone exists.
        //   FixedRR    — target placed at entry ± (multiple × risk).
        //                Use a small multiple (e.g. 2) for scalp grading,
        //                a large one (e.g. 10) for long-hold grading.
        //                Score band reflects the chosen multiple directly.
        public enum RrrTargetMode { Structural = 0, FixedRR = 1 }

        [InputParameter("R:R target mode", 14, variants: new object[]
        {
            "Structural (next opposing zone)", RrrTargetMode.Structural,
            "Fixed R:R multiple",              RrrTargetMode.FixedRR
        })]
        public RrrTargetMode RrrTarget = RrrTargetMode.Structural;

        [InputParameter("R:R fixed multiple (FixedRR mode only)", 15, 0.5, 20.0, 0.5, 1)]
        public double RrrFixedMultiple = 5.0;

        // ---------------------------------------------------------------------
        // INPUTS — Lifecycle
        // ---------------------------------------------------------------------
        [InputParameter("Use lifecycle (state machine)", 20)]
        public bool UseLifecycle = true;

        // We expose the LIFECYCLE TrailStrategy enum (not the Trail one) — both
        // enums share the same numeric encoding so the bridge is trivial.
        [InputParameter("Trail strategy", 21, variants: new object[]
        {
            "Off",        TPLifecycle.TrailStrategy.Off,
            "FixedR",     TPLifecycle.TrailStrategy.FixedR,
            "Structure",  TPLifecycle.TrailStrategy.Structure,
            "ATR",        TPLifecycle.TrailStrategy.ATR,
            "TimeDecay",  TPLifecycle.TrailStrategy.TimeDecay,
            "Runner",     TPLifecycle.TrailStrategy.Runner,
            "DelayedBE",  TPLifecycle.TrailStrategy.DelayedBE,
            "Cascade",    TPLifecycle.TrailStrategy.Cascade
        })]
        public TPLifecycle.TrailStrategy ActiveTrailStrategy = TPLifecycle.TrailStrategy.Cascade;

        [InputParameter("Trail from entry (Strats 2,3)", 22)]
        public bool TrailFromEntry = false;

        [InputParameter("Use sequential gate (1 active at a time)", 23)]
        public bool UseSequentialGate = true;

        // IM2: Range widened from 0.05..5.0 to 0.05..20.0 to match Pine's range.
        [InputParameter("ARMED proximity (× zoneHeight)", 24, 0.05, 20.0, 0.05, 2)]
        public double ArmProx = 0.5;

        [InputParameter("Dollar risk per trade", 25, 1.0, 100000.0, 5.0, 2)]
        public double DollarRiskPerTrade = 100.0;

        [InputParameter("Max contracts (cap)", 26, 1, 1000, 1, 0)]
        public int MaxContracts = 20;

        [InputParameter("Lifecycle history bars", 27, 100, 100000, 100, 0)]
        public int LifecycleHistoryBars = 1000;

        [InputParameter("Max retained closed trades", 28, 5, 500, 5, 0)]
        public int MaxRetainedClosed = 50;

        [InputParameter("Max closed visible (table)", 29, 1, 100, 1, 0)]
        public int MaxClosedVisible = 20;

        [InputParameter("Max bars to TP1 (Strat 4)", 30, 5, 500, 5, 0)]
        public int MaxBarsToTp1 = 50;

        [InputParameter("Runner scale-out %", 31, 0.0, 1.0, 0.05, 2)]
        public double RunnerScaleOutPct = 0.75;

        // ---------------------------------------------------------------------
        // INPUTS — UI
        // ---------------------------------------------------------------------
        [InputParameter("Show stats strip", 40)]
        public bool ShowStrip = false;

        [InputParameter("Strip position", 41, variants: new object[]
        {
            "Top Left",      TPUI.StripPosition.TopLeft,
            "Top Center",    TPUI.StripPosition.TopCenter,
            "Top Right",     TPUI.StripPosition.TopRight,
            "Middle Left",   TPUI.StripPosition.MiddleLeft,
            "Middle Center", TPUI.StripPosition.MiddleCenter,
            "Middle Right",  TPUI.StripPosition.MiddleRight,
            "Bottom Left",   TPUI.StripPosition.BottomLeft,
            "Bottom Center", TPUI.StripPosition.BottomCenter,
            "Bottom Right",  TPUI.StripPosition.BottomRight
        })]
        public TPUI.StripPosition StripPos = TPUI.StripPosition.TopLeft;

        [InputParameter("Show closed trades table", 42)]
        public bool ShowClosedTradesTable = false;

        [InputParameter("Closed table position", 43, variants: new object[]
        {
            "Top Left",     TPUI.PanelPosition.TopLeft,
            "Top Right",    TPUI.PanelPosition.TopRight,
            "Bottom Left",  TPUI.PanelPosition.BottomLeft,
            "Bottom Right", TPUI.PanelPosition.BottomRight,
            "Middle Left",  TPUI.PanelPosition.MiddleLeft,
            "Middle Right", TPUI.PanelPosition.MiddleRight
        })]
        public TPUI.PanelPosition ClosedTablePos = TPUI.PanelPosition.BottomLeft;

        [InputParameter("Show status dashboard", 44)]
        public bool ShowStatusDashboard = false;

        [InputParameter("Status dashboard position", 45, variants: new object[]
        {
            "Top Left",     TPUI.PanelPosition.TopLeft,
            "Top Right",    TPUI.PanelPosition.TopRight,
            "Bottom Left",  TPUI.PanelPosition.BottomLeft,
            "Bottom Right", TPUI.PanelPosition.BottomRight,
            "Middle Left",  TPUI.PanelPosition.MiddleLeft,
            "Middle Right", TPUI.PanelPosition.MiddleRight
        })]
        public TPUI.PanelPosition DashboardPos = TPUI.PanelPosition.TopRight;

        [InputParameter("Show next-trades preview panel (2D + 2S, 3R+ only)", 48)]
        public bool ShowNextTradesPanel = true;

        [InputParameter("Next-trades panel position", 49, variants: new object[]
        {
            "Top Left",     TPUI.PanelPosition.TopLeft,
            "Top Right",    TPUI.PanelPosition.TopRight,
            "Bottom Left",  TPUI.PanelPosition.BottomLeft,
            "Bottom Right", TPUI.PanelPosition.BottomRight,
            "Middle Left",  TPUI.PanelPosition.MiddleLeft,
            "Middle Right", TPUI.PanelPosition.MiddleRight
        })]
        public TPUI.PanelPosition NextTradesPanelPos = TPUI.PanelPosition.TopLeft;

        [InputParameter("Text size (1=Tiny..5=Huge)", 46, 1, 5, 1, 0)]
        public int TextSize = 3;

        [InputParameter("Show zone score labels", 47)]
        public bool ShowZoneScoreLabels = true;

        // ---------------------------------------------------------------------
        // INPUTS — Colors
        // ---------------------------------------------------------------------
        [InputParameter("Demand color", 60)]
        // 2026-05-11: alpha=180 matches the previous hardcoded upper-bound
        // visibility (was 60..200 score-modulated). User can now dial alpha
        // up or down in settings — score modulates 30..100% of this value.
        public Color DemandColor = Color.FromArgb(180, 0, 200, 80);

        [InputParameter("Supply color", 61)]
        public Color SupplyColor = Color.FromArgb(180, 220, 60, 60);

        [InputParameter("Entry line color", 62)]
        public Color EntryLineColor = Color.Yellow;

        [InputParameter("SL line color", 63)]
        public Color SLLineColor = Color.OrangeRed;

        [InputParameter("TP line color", 64)]
        public Color TPLineColor = Color.DeepSkyBlue;

        [InputParameter("BE line color", 65)]
        public Color BELineColor = Color.Aqua;

        // ---------------------------------------------------------------------
        // INPUTS — Multi-Timeframe (MTF zones + MTFC overlap)
        // ---------------------------------------------------------------------
        // OFF by default to avoid the latency cost of two extra GetHistory()
        // requests on chart load. When enabled, ITF + HTF zones get scanned and
        // any chart-TF zone whose box overlaps a higher-tier zone of the same
        // direction earns a Juice bonus ("the Juice").
        [InputParameter("Use MTF zones", 50)]
        public bool UseMTFZones = true;

        // 2026-05-11: ITF=1H, HTF=4H is the user's preferred default for
        // most chart timeframes (5m / 15m execution). Prior HTF=Daily was
        // too coarse to produce relevant intraday targets.
        [InputParameter("ITF period", 51)]
        public Period ITFPeriod = Period.HOUR1;

        [InputParameter("HTF period", 52)]
        public Period HTFPeriod = Period.HOUR4;

        [InputParameter("MTFC overlap bonus score", 53, 0.0, 10.0, 0.5, 2)]
        public double MTFCBonusScore = 1.0;

        [InputParameter("MTF lookback bars", 54, 50, 5000, 50, 0)]
        public int MTFLookbackBars = 200;

        [InputParameter("MTFC overlap color — LTF in ITF/HTF (yellow)", 55)]
        public Color MTFCOverlapColor = Color.FromArgb(160, 255, 235, 80);

        // 2026-05-10: separate highlight for ITF-in-HTF confluence. Drawn in
        // red dashed so the user can distinguish the two tiers of agreement
        // at a glance: yellow = "my chart-TF zone is inside an ITF zone";
        // red = "an ITF zone is inside an HTF zone (the bigger picture)."
        [InputParameter("MTFC overlap color — ITF in HTF (red)", 56)]
        public Color MTFCOverlapItfHtfColor = Color.FromArgb(180, 230, 60, 60);

        // ---------------------------------------------------------------------
        // INPUTS — Alerts
        // ---------------------------------------------------------------------
        [InputParameter("Enable alerts", 70)]
        public bool EnableAlerts = true;

        [InputParameter("Audible alerts", 71)]
        public bool AudibleAlerts = true;

        [InputParameter("Webhook URL (optional)", 72)]
        public string WebhookUrl = "";

        [InputParameter("CSV log path (optional)", 73)]
        public string AlertsCsvPath = "";

        // ---------------------------------------------------------------------
        // INPUTS — Trend (2026-05-06: TrendStateMachine integration)
        // ---------------------------------------------------------------------
        // Drives the TrendStateMachine: control-point detection (engulfing
        // pivots), trend break detection (close-through HL/LH), and the
        // resulting Bull / Bear / Flat state. When EnableTrendDetection is
        // off the state machine is bypassed and ScoreTrend falls back to its
        // legacy fractal-based logic. CloseOnTrendBroken is propagated to
        // TradeLifecycleManager so PASS A can flatten an active trade
        // whose direction conflicts with the post-break trend.
        [InputParameter("Enable Trend Detection", 80)]
        public bool EnableTrendDetection = true;

        // LTF / ITF / HTF decoupling per TP framework hierarchy:
        //   Range = HTF, Trend = ITF, Execution = LTF, Visual = chart TF.
        // The chart TF is independent of all three. Default behavior (overrides
        // disabled) keeps the legacy "chart-TF = LTF, chart-TF feeds trend"
        // behavior so existing users see no change unless they opt in.
        [InputParameter("Override LTF (use a different TF for execution zones)", 87)]
        public bool LTFOverrideEnabled = false;

        [InputParameter("LTF override period (only used if Override LTF = ON)", 88)]
        public Period LTFOverridePeriod = Period.MIN5;

        [InputParameter("Trend feeds off ITF (per TP framework)", 89)]
        public bool TrendFromITF = true;

        [InputParameter("Engulfing Control Points (strict)", 81)]
        public bool RequireEngulfingControlPoints = true;

        [InputParameter("Trend swing lookback (bars)", 82, 1, 10, 1, 0)]
        public int TrendSwingLookback = 3;

        [InputParameter("Close active trade on trend broken", 83)]
        public bool CloseOnTrendBroken = false;

        // 2026-05-13: counter-trend arming controls. AllowCounterTrend gates
        // whether zones whose direction opposes the active trend can arm at
        // all. OnlyArmClosestPerDirection restricts the lifecycle to a single
        // demand + single supply at a time (the nearest of each by distance
        // from price) so you don't arm a stack of overlapping zones.
        [InputParameter("Allow counter-trend trades", 85)]
        public bool AllowCounterTrend = true;

        [InputParameter("Arm only closest demand + closest supply", 86)]
        public bool OnlyArmClosestPerDirection = true;

        [InputParameter("Show control points on chart", 84)]
        public bool ShowControlPoints = true;

        [InputParameter("Show trend break markers on chart", 85)]
        public bool ShowTrendBreaks = true;

        [InputParameter("Refresh zone Trend factor on flip", 86)]
        public bool RefreshZoneTrendOnFlip = true;

        // ---------------------------------------------------------------------
        // TRADING HQ BRIDGE — fire-and-forget HTTP emitter
        // ---------------------------------------------------------------------
        // Posts every armed intent / fill / close + a 5s heartbeat to the
        // Trading HQ FastAPI backend at localhost:8000. The bridge is
        // entirely non-essential — every call is wrapped in a try/catch and
        // never blocks the indicator's chart thread. If localhost:8000 is
        // not running, indicator continues normally.
        //
        // See C:\TradingHQ\BRIDGE.md for the full event contract.
        // ---------------------------------------------------------------------
        // 2026-05-13: bumped 2s → 10s. The 2s timeout was firing as a false
        // negative when the hub was busy answering 57,600-variant correlation
        // queries (a healthy hub easily takes 2-3s under that load). 10s
        // gives the hub headroom; the fire-and-forget pattern means the
        // chart loop never blocks on this.
        //
        // 2026-05-13 audit fix #3: explicit SocketsHttpHandler with raised
        // MaxConnectionsPerServer. Default pool is 2 per host on .NET
        // Framework / ~10 on .NET 5+; under 4 charts × heartbeat + snapshot
        // + zone events the pool can saturate, queuing emits behind in-
        // flight sockets and causing 10-second timeouts to fire on requests
        // that haven't even hit the wire yet. 64 connections gives every
        // chart headroom even during a startup-burst.
        private static readonly HttpClient _bridgeHttp = BuildBridgeHttpClient();

        private static HttpClient BuildBridgeHttpClient()
        {
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                MaxConnectionsPerServer = 64,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                EnableMultipleHttp2Connections = true,
            };
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
        }
        private const string BridgeIntentUrl = "http://localhost:8000/api/intent";
        private const string BridgeHealthUrl = "http://localhost:8000/api/intent/latest?limit=1";

        // 2026-05-13: durable retry queue for hub POST failures.
        // When the hub is down or slow, every event posted between failure and
        // recovery was previously SILENTLY LOST inside the .ContinueWith swallow.
        // That gap-loss was the root cause of audit_engine flagging 100% of
        // recent trades as DISAGREE (lifecycle saw the wrong bars after a missing
        // market_snapshot). Fix: on POST failure, append the envelope JSON to a
        // jsonl file on disk. A separate Python drain script (scripts/
        // drain_pending_events.py) replays the file to the hub when it's back up.
        // Fire-and-forget remains the design — we never block the chart loop.
        private const string PendingEventsLogPath =
            @"C:\TradingHQ\data\logs\pending_events.jsonl";
        private static readonly object _pendingEventsLock = new object();
        private static long _pendingEventsAppendCount = 0;

        private static void AppendPendingEvent(string envelopeJson)
        {
            if (string.IsNullOrEmpty(envelopeJson)) return;
            try
            {
                lock (_pendingEventsLock)
                {
                    System.IO.Directory.CreateDirectory(
                        System.IO.Path.GetDirectoryName(PendingEventsLogPath));
                    System.IO.File.AppendAllText(
                        PendingEventsLogPath,
                        envelopeJson + "\n",
                        Encoding.UTF8);
                    _pendingEventsAppendCount++;
                }
            }
            catch
            {
                // Never let the retry logger break the chart loop. If the disk is
                // full, we accept losing this event — it was already going to be
                // lost without this code.
            }
        }
        // 2026-05-11: IOF hub event endpoint. Structured per-event ingest for
        // the capture-only observer pipeline (see Trading HQ docs). The
        // existing /api/intent endpoint stays for the legacy chart_state /
        // bar emissions; new typed events go here.
        private const string BridgeIofEventUrl = "http://localhost:8000/api/iof/event";

        // Stable per-observer id. Built once on init from symbol root +
        // chart-TF + machine name so the hub can tell observers apart across
        // multiple Quantower workspaces (different prop-firm accounts).
        // Convention: "<root>-<tf>-<machine>" lower-cased.
        private string _observerId = "";

        // Idempotency guard: emit observer_hello exactly once per indicator
        // instance, even if OnInit fires multiple times on settings reload.
        private bool _observerHelloEmitted = false;

        // Track which zone IDs we've already emitted a zone_detected event
        // for. Set when emitted; cleared on a full Reset.
        private readonly HashSet<string> _emittedZoneDetectedIds = new HashSet<string>(StringComparer.Ordinal);

        // 2026-05-11: Bot-ready additions per IOF_Bot_Hub_Complete.md design.
        //
        // SCHEMA VERSION — every observer event includes this so consumers
        // (scouts, hub bots, external tools) can detect breaking changes and
        // fail fast rather than silently misinterpret. Bump on any field
        // rename or semantic change.
        public const string IofObserverApiVersion = "1.0.0";

        // SEQUENCE NUMBER — monotonic, per-indicator-instance. Lets the hub
        // detect missed events (gaps in the sequence) without depending on
        // wall-clock timestamps (which can be ambiguous on bar-boundary
        // emissions). Interlocked.Increment for thread safety since events
        // may emit from OnUpdate, OnNewLast, and lifecycle event handlers.
        private long _observerEventSeq = 0;
        private const string BridgeLauncherVbs = @"C:\TradingHQ\start_backend_hidden.vbs";
        private static readonly JsonSerializerOptions _bridgeJsonOptions =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private System.Threading.Timer _bridgeHeartbeatTimer;
        private string _indicatorVersion = "v2.1.3-tp-entries";

        // 2026-05-09 — Auto-spawn guard. Quantower loads the indicator once
        // per chart but the AppDomain is shared, so this static flag stops
        // multiple simultaneous loads from each trying to start the backend.
        // Set on the first attempt regardless of outcome — we don't retry
        // mid-session even if the spawn failed.
        private static int _bridgeSpawnAttempted = 0;

        // Snapshot of zone state from the previous chart_state emission, keyed
        // by ZoneId. Used to compute violations[] (score_drop, lost_tradeable,
        // touch, invalidated, etc.) by diffing against the current scan.
        private Dictionary<string, ZoneSnapshot> _previousZoneSnapshots
            = new Dictionary<string, ZoneSnapshot>(StringComparer.Ordinal);

        private class ZoneSnapshot
        {
            public double Score;
            public int TouchCount;
            public bool Tradeable;
            public bool IsArmed;
        }

        // ---------------------------------------------------------------------
        // INTERNAL STATE
        // ---------------------------------------------------------------------
        // Zone detection store (ported from v1).
        private readonly List<IofZone> zones = new List<IofZone>();

        // Lifecycle layer.
        private TPLifecycle.TradeLifecycleManager lifecycleManager;

        // EMA cache (for Juice scoring).
        private double[] emaCache;
        // 2026-05-12 CVD caches per Agent 5 research synthesis (CVD methodology).
        //   _cvdSessionCache[i] = cumulative delta from the bar's session start
        //                         (resets at RTH open 09:30 ET; reset on roll).
        //   _cvdRollingCache[i] = sum of last 20 bars' deltas.
        // Both are per-bar parallel arrays sized to HistoricalData.Count.
        // Indexing matches the chart's End-relative convention: index 0 is
        // the currently-forming bar, index 1 the last closed bar.
        private double[] _cvdSessionCache;
        private double[] _cvdRollingCache;
        // Synthetic-vs-real delta quality flag. Set true when VolumeAnalysisData
        // exposes real bid/ask aggregator data; false when zero-delta on every
        // bar suggests tick-rule estimation. Re-checked on each scan.
        private bool _cvdFeedIsReal;
        private const int EmaPeriod = 50;

        // HTF history for Range factor.
        private HistoricalData htfHistory;

        // Bar-index tracking for the lifecycle pass.
        private int lastProcessedBarCount = -1;

        // Volume-analysis loaded flag.
        private bool volumeAnalysisLoaded;

        // Cached pens — rebuilt OnInit / when colors change.
        private Pen entryPen, slPen, tpPen, bePen;

        // ATR window for trail strategies.
        private const int AtrPeriod = 14;
        private double[] atrCache;

        // 2026-05-12 Phase 1b — Daily ATR percentile (observation-only,
        // emitted on market_snapshot.atr_percentile). Pure regime tag for
        // hub-side analytics ("dead tape" vs "whipsaw"); per project
        // doctrine ("ATR observed never traded") it does NOT feed back
        // into zone scoring, sizing, or entry/exit decisions.
        //
        //   dailyHistory  — Period.DAY1 feed, loaded once at OnInit.
        //   dailyAtrCache — Wilder ATR(14) per daily bar, end-relative
        //                   (index 0 = most recent / in-progress day).
        //   ComputeDailyAtrPercentile() ranks the latest CLOSED daily ATR
        //                   (index 1, not 0, to stay stable within a
        //                   session) against the prior AtrPercentileWindow
        //                   closed daily ATRs. Returns null if the cache
        //                   doesn't have enough samples yet.
        private HistoricalData dailyHistory;
        private double[] dailyAtrCache;
        private const int AtrPercentileWindow = 60;

        // ---------------------------------------------------------------------
        // CR4: Thread safety. OnNewLast fires on a Quantower IO thread; the
        // OnUpdate / OnPaintChart pipeline runs on the indicator's main thread.
        // Both touch lifecycleManager.Trades (read or mutate). This lock is the
        // single guard around every cross-thread access path.
        // ---------------------------------------------------------------------
        private readonly object tradesLock = new object();

        // ---------------------------------------------------------------------
        // MTF zone state (lazy-initialized when UseMTFZones is true).
        // itfData / htfData are HistoricalData feeds we own (must Dispose).
        // itfZones / htfZones / chartZones are scan results, refreshed per bar.
        // mtfcOverlaps is the latest set of correlations between LTF<->ITF and
        // LTF<->HTF — used both to apply the Juice bonus and for paint.
        // ---------------------------------------------------------------------
        private HistoricalData itfData;
        private HistoricalData htfData;
        private List<TPMTF.TimeframeZone> itfZones    = new List<TPMTF.TimeframeZone>();
        private List<TPMTF.TimeframeZone> htfZones    = new List<TPMTF.TimeframeZone>();
        private List<TPMTF.TimeframeZone> chartZones  = new List<TPMTF.TimeframeZone>();
        private List<TPMTF.MTFCOverlap> mtfcOverlaps      = new List<TPMTF.MTFCOverlap>();
        // 2026-05-10: ITF-in-HTF overlaps, drawn separately as red dashed
        // boxes. These reflect "an ITF zone sits inside an HTF zone" —
        // higher-tier confluence than the chart-TF/ITF agreement.
        private List<TPMTF.MTFCOverlap> itfHtfOverlaps    = new List<TPMTF.MTFCOverlap>();

        // Cache of zone IDs we've already alerted on (so we only fire once per
        // detection, not on every rescan).
        private readonly HashSet<string> alertedZoneIds = new HashSet<string>();

        // ---------------------------------------------------------------------
        // Alerts state. config is rebuilt OnInit / OnSettingsUpdated. The
        // lastTickState dict snapshots TradeRecord.State per ID after each
        // RunPerBar so the next pass can detect transitions. The supporting
        // dicts track per-trade levels we've already alerted on.
        // ---------------------------------------------------------------------
        private TPAlerts.AlertsConfig alertsConfig;
        private readonly Dictionary<int, TPLifecycle.TradeState> lastTickState
            = new Dictionary<int, TPLifecycle.TradeState>();
        private readonly Dictionary<int, int> lastTickHighestTpHit
            = new Dictionary<int, int>();
        private readonly Dictionary<int, double> lastTickCurSL
            = new Dictionary<int, double>();
        private readonly Dictionary<int, bool> beAlertedTrades
            = new Dictionary<int, bool>();

        // ---------------------------------------------------------------------
        // TREND state (2026-05-06).
        // The state machine is fed each newly-closed bar in OnUpdate and the
        // marker lists are appended via the three event handlers
        // (OnControlPointDetected_Handler / OnTrendBroken_Handler /
        // OnTrendStateChanged_Handler). All marker mutation runs under
        // tradesLock so OnPaintChart can iterate safely.
        // ---------------------------------------------------------------------
        private TradePhantomsIOF.Trend.TrendStateMachine trendStateMachine;
        // 2026-05-10: Set during the one-time historical replay into the trend
        // state machine on init (or after a feed gap). The CP/Break/State-change
        // handlers gate alert firing on this flag — markers still get added so
        // historical context paints, but we don't spam the user with stale
        // alerts for events that happened before they opened the chart.
        private bool isReplayingTrendHistory;
        private List<TradePhantomsIOF.UI.ControlPointMarker> controlPointMarkers
            = new List<TradePhantomsIOF.UI.ControlPointMarker>();
        private List<TradePhantomsIOF.UI.TrendBreakMarker> trendBreakMarkers
            = new List<TradePhantomsIOF.UI.TrendBreakMarker>();
        private TradePhantomsIOF.Trend.TrendState lastNotifiedTrendState
            = TradePhantomsIOF.Trend.TrendState.Flat;
        private DateTime lastTrendStateChangeAt;
        private int lastBarIndexProcessedForTrend = -1;

        // LTF / ITF decoupled feeds (independent of chart TF).
        private HistoricalData ltfHistory;       // execution-layer feed (zones, lifecycle)
        private HistoricalData itfTrendHistory;  // ITF trend-detection feed
        private int lastItfBarIndexProcessedForTrend = -1;

        // ---------------------------------------------------------------------
        // CONSTRUCTOR
        // ---------------------------------------------------------------------
        public TradePhantoms_IOF_v2() : base()
        {
            Name = "TradePhantoms IOF v2";
            Description = "Pine v1.4 port: zone detection + 20-pt rubric + lifecycle " +
                          "state machine (ARMED→ACTIVE→W/L/BE), trail strategies, " +
                          "stats strip + closed-trades table + status dashboard.";

            AddLineSeries("IOFv2_marker", Color.Transparent, 1, LineStyle.Solid);
            SeparateWindow = false;
        }

        public bool IsRequirePriceLevelsCalculation => false;

        public void VolumeAnalysisData_Loaded()
        {
            this.volumeAnalysisLoaded = true;
            this.lastProcessedBarCount = -1;
        }

        // ---------------------------------------------------------------------
        // OnInit — wire up trail dispatch, build lifecycle manager, fetch HTF.
        // ---------------------------------------------------------------------
        protected override void OnInit()
        {
            ShortName = $"IOFv2 (>={MinScore}/21)";

            this.zones.Clear();
            this.emaCache = null;
            this.atrCache = null;
            this.dailyAtrCache = null;
            this.lastProcessedBarCount = -1;

            // Build pens.
            DisposePens();
            this.entryPen = new Pen(EntryLineColor, 2f) { DashStyle = DashStyle.Dash };
            this.slPen    = new Pen(SLLineColor,    2f) { DashStyle = DashStyle.Dot };
            this.tpPen    = new Pen(TPLineColor,    1.5f) { DashStyle = DashStyle.Dash };
            this.bePen    = new Pen(BELineColor,    1.5f) { DashStyle = DashStyle.DashDot };

            // ── Bridge: wire TrailStrategies.Apply (component) into the
            // lifecycle's TrailStrategiesFallback.OverrideApply hook. The two
            // components have intentionally different signatures — the lifecycle
            // calls the hook with a TradeRecord (its canonical type), the trail
            // component takes flat arrays of inputs. We adapt here.
            //
            // Note on judgment call: lifecycle's TrailStrategy enum and the
            // trail component's TrailStrategy enum share numeric values 0..7.
            // We cast across via (int) so a Cascade in one is Cascade in the
            // other. The lifecycle's enum is the user-facing one (exposed via
            // the InputParameter dropdown).
            TPLifecycle.TrailStrategiesFallback.OverrideApply = BridgeApplyTrail;

            // Lifecycle manager. CR3: ActiveOFEntry is wired to the master's
            // OFEntry input so PASS C uses the configured depth when computing
            // entry prices on newly armed trades.
            this.lifecycleManager = new TPLifecycle.TradeLifecycleManager
            {
                ActiveStrategy        = this.ActiveTrailStrategy,
                UseSequentialGate     = this.UseSequentialGate,
                MaxRetainedClosed     = this.MaxRetainedClosed,
                LifecycleHistoryBars  = this.LifecycleHistoryBars,
                ArmProx               = this.ArmProx,
                StopBufferTicks       = this.StopBufferTicks,
                TrailFromEntry        = this.TrailFromEntry,
                MaxBarsToTp1          = this.MaxBarsToTp1,
                RunnerScaleOutPct     = this.RunnerScaleOutPct,
                NumTPs                = this.TpCount,
                TpMult                = this.TpStep,
                ActiveOFEntry         = this.OFEntry
            };

            // Reset transition-detection caches for the alerts diff snapshot.
            this.lastTickState.Clear();
            this.lastTickHighestTpHit.Clear();
            this.lastTickCurSL.Clear();
            this.beAlertedTrades.Clear();
            this.alertedZoneIds.Clear();

            // Build alerts config from inputs.
            this.alertsConfig = BuildAlertsConfig();

            // HTF history for Range scoring (chart-TF "context" feed).
            try
            {
                var htfPeriod = ChooseHigherTimeframe(GetChartPeriod());
                if (htfPeriod != null && this.Symbol != null)
                {
                    this.htfHistory = this.Symbol.GetHistory(
                        htfPeriod.Value,
                        this.Symbol.HistoryType,
                        Core.TimeUtils.DateTimeUtcNow.AddDays(-30));
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.HTFInit");
            }

            // 2026-05-12 Phase 1b — Daily history feed for ATR percentile.
            // Need at least AtrPeriod (14) + AtrPercentileWindow (60) = 74
            // daily bars. Pulling 120 calendar days covers ~85 trading days
            // with comfortable headroom for holidays / weekends.
            try
            {
                if (this.Symbol != null)
                {
                    this.dailyHistory = this.Symbol.GetHistory(
                        Period.DAY1,
                        this.Symbol.HistoryType,
                        Core.TimeUtils.DateTimeUtcNow.AddDays(-120));
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.DailyHistInit");
            }

            // MTF data feeds — lazy fetch only when feature is on. Quantower's
            // GetHistory call is async (returns a HistoricalData that loads on
            // a background thread), so we tolerate Count == 0 in the scan loop.
            if (this.UseMTFZones && this.Symbol != null)
            {
                try
                {
                    this.itfData = TPMTF.MultiTFZoneScanner.FetchTimeframeData(
                        this.Symbol, this.ITFPeriod, this.MTFLookbackBars);
                    this.htfData = TPMTF.MultiTFZoneScanner.FetchTimeframeData(
                        this.Symbol, this.HTFPeriod, this.MTFLookbackBars);
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.MTFInit");
                }
            }

            // LTF override feed (zones detected on a different TF than the chart).
            // Only when the user opts in — defaults preserve legacy chart-TF=LTF.
            if (this.LTFOverrideEnabled && this.Symbol != null)
            {
                try
                {
                    this.ltfHistory = this.Symbol.GetHistory(
                        this.LTFOverridePeriod,
                        this.Symbol.HistoryType,
                        Core.TimeUtils.DateTimeUtcNow.AddDays(-30));
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.LTFInit");
                }
            }

            // ITF trend feed (always when trend detection is on AND TrendFromITF).
            // Per TP framework: Trend = ITF. The trend state machine should NOT
            // see the chart-TF candles — that conflates execution speed with
            // trend speed. The fetched ITF data is the same ITFPeriod used by
            // the MTFC overlap detector.
            if (this.EnableTrendDetection && this.TrendFromITF && this.Symbol != null)
            {
                try
                {
                    this.itfTrendHistory = this.Symbol.GetHistory(
                        this.ITFPeriod,
                        this.Symbol.HistoryType,
                        Core.TimeUtils.DateTimeUtcNow.AddDays(-30));
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.ITFTrendInit");
                }
            }

            // ── Trend state machine (2026-05-06). Always instantiated so the
            // event-handler subscriptions are safe even when EnableTrendDetection
            // is false at startup; OnUpdate gates the per-bar feeding by the
            // input flag. Wiring lives here (rather than at field-init time) so
            // the SwingFractalLookback / RequireEngulfingForControlPoint inputs
            // are picked up after panel edits via OnSettingsUpdated.
            this.trendStateMachine = new TradePhantomsIOF.Trend.TrendStateMachine
            {
                SwingFractalLookback             = this.TrendSwingLookback,
                RequireEngulfingForControlPoint  = this.RequireEngulfingControlPoints,
                MaxPivotHistory                  = 50
            };
            this.trendStateMachine.OnControlPointDetected += OnControlPointDetected_Handler;
            this.trendStateMachine.OnTrendBroken          += OnTrendBroken_Handler;
            this.trendStateMachine.OnTrendStateChanged    += OnTrendStateChanged_Handler;

            // Wire the lifecycle's TrendStateFallback so PASS A can query the
            // current trend without taking a hard dependency on the state-machine
            // type (the lifecycle layer remains decoupled from Trend.* types).
            TradePhantomsIOF.Lifecycle.TrendStateFallback.GetCurrentTrendState =
                () => (int)this.trendStateMachine.CurrentState;

            // Forward the CloseOnTrendBroken input into the lifecycle so the
            // optional "flatten on conflicting trend break" check is gated by
            // the user's setting.
            this.lifecycleManager.CloseOnTrendBroken = this.CloseOnTrendBroken;
            // 2026-05-13: counter-trend gating + closest-zone-only arming.
            this.lifecycleManager.AllowCounterTrend         = this.AllowCounterTrend;
            this.lifecycleManager.OnlyArmClosestPerDirection = this.OnlyArmClosestPerDirection;

            // Reset trend bookkeeping on init.
            this.controlPointMarkers.Clear();
            this.trendBreakMarkers.Clear();
            this.lastNotifiedTrendState = TradePhantomsIOF.Trend.TrendState.Flat;
            this.lastTrendStateChangeAt = DateTime.UtcNow;
            this.lastBarIndexProcessedForTrend = -1;

            // IM3: defensive unsubscribe before subscribe to prevent double-fire
            // across symbol switches / chart re-binds. The try/catch swallows
            // the case where the handler wasn't subscribed yet (first run).
            if (this.Symbol != null)
            {
                try { this.Symbol.NewLast -= OnNewLast; } catch { }
                this.Symbol.NewLast += OnNewLast;
            }

            // 2026-05-07 — Subscribe to Core.PositionAdded so the lifecycle
            // tracks manual entries (clicks-in without going through ARM).
            try { Core.Instance.PositionAdded -= OnPositionAdded_Handler; } catch { }
            Core.Instance.PositionAdded += OnPositionAdded_Handler;

            // 2026-05-09 — Trading HQ bridge: track position closes so the
            // dashboard sees "close" events with R-realized + close reason.
            try { Core.Instance.PositionRemoved -= OnPositionRemoved_Handler; } catch { }
            Core.Instance.PositionRemoved += OnPositionRemoved_Handler;

            // 2026-05-09 — Trading HQ bridge: ensure the backend is up. If
            // localhost:8000 doesn't answer within 500ms, fire-and-forget
            // launch the hidden VBS wrapper that boots uvicorn. Tying
            // backend lifetime to Quantower lifetime — no Windows-login
            // autostart needed; opening Quantower is what brings up the
            // bridge. Self-guarded by Interlocked so multiple chart loads
            // don't race.
            EnsureBridgeBackendRunning();

            // 2026-05-09 — Trading HQ bridge: start heartbeat. Fires every 5
            // seconds while the indicator is loaded so dashboard's connection
            // status pip stays accurate.
            StartBridgeHeartbeat();

            // 2026-05-09 — Trading HQ bridge: backfill recent OHLC so the
            // dashboard chart pane has real Quantower price action even when
            // the market is closed (weekends, holidays, off-hours). Without
            // this, the chart can't go LIVE until the next bar closes —
            // which on a Saturday means waiting until Sunday evening.
            BackfillBarsToBridge(300);

            // 2026-05-11: IOF hub handshake. One observer_hello per indicator
            // instance, idempotent across settings reloads. Carries the symbol
            // metadata + settings snapshot so the hub registers this observer
            // and knows its tick / point-value context up front.
            if (!_observerHelloEmitted)
            {
                _observerHelloEmitted = true;
                _observerId = BuildObserverId();
                _emittedZoneDetectedIds.Clear();
                try
                {
                    double tickSizeHello = 0;
                    try { tickSizeHello = this.Symbol?.TickSize ?? 0; } catch { }
                    double pointValueHello = 0;
                    try { pointValueHello = ResolvePointValue(); } catch { }
                    EmitObserverEvent("observer_hello", new
                    {
                        indicator_version = this._indicatorVersion,
                        tick_size         = tickSizeHello,
                        point_value       = pointValueHello,
                        itf_period        = this.ITFPeriod.ToString(),
                        htf_period        = this.HTFPeriod.ToString(),
                        settings_snapshot = new
                        {
                            MinScore               = this.MinScore,
                            MinDrawScore           = this.MinDrawScore,
                            MaxBaseCandles         = this.MaxBaseCandles,
                            MinImpulseRatio        = this.MinImpulseRatio,
                            BaseCandleMaxBodyPct   = this.BaseCandleMaxBodyPct,
                            LookbackBars           = this.LookbackBars,
                            StopBufferTicks        = this.StopBufferTicks,
                            DollarRiskPerTrade     = this.DollarRiskPerTrade,
                            OFEntry                = this.OFEntry.ToString(),
                            TpCount                = this.TpCount,
                            TpStep                 = this.TpStep,
                            EnableTrendDetection   = this.EnableTrendDetection,
                            TrendFromITF           = this.TrendFromITF,
                            UseMTFZones            = this.UseMTFZones,
                            RrrTargetMode          = this.RrrTarget.ToString(),
                            RrrFixedMultiple       = this.RrrFixedMultiple,
                        },
                    });
                }
                catch { /* never throw from init */ }
            }
        }

        // ---------------------------------------------------------------------
        // OnPositionRemoved — emit a "close" event to the Trading HQ bridge
        // when a tracked position closes. Best-effort R-realized computation
        // from realized P&L if available.
        // ---------------------------------------------------------------------
        private void OnPositionRemoved_Handler(Position position)
        {
            if (position == null || this.Symbol == null) return;
            if (position.Symbol == null ||
                !string.Equals(position.Symbol.Name, this.Symbol.Name, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                bool isLong = position.Side == Side.Buy;
                // Quantower's Position doesn't expose a close price directly
                // — pull it from the most recent trade record on this account
                // if available; otherwise fall back to last tick price.
                double exitPrice = 0;
                try
                {
                    double last = this.Symbol?.Last ?? 0;
                    if (last > 0) exitPrice = last;
                } catch { }
                double pnl = 0;
                try { pnl = position.GrossPnL?.Value ?? 0; } catch { }

                // Reason inference is best-effort — Quantower doesn't expose
                // a structured close reason. Default to "Manual"; downstream
                // logic can refine when the lifecycle exposes its own.
                string reason = pnl > 0 ? "TP" : pnl < 0 ? "SL" : "BE";

                EmitToBridge(new
                {
                    type = "close",
                    ts = DateTime.UtcNow.ToString("o"),
                    tradeId = position.Id?.ToString() ?? Guid.NewGuid().ToString(),
                    exitPrice = exitPrice,
                    pnl = pnl,
                    rRealized = 0.0,           // computed downstream when bracket math is wired
                    closeReason = reason,
                    side = isLong ? "Long" : "Short"
                });
            }
            catch (Exception ex)
            {
                try { Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.OnPositionRemoved_Handler"); } catch { }
            }
        }

        // ---------------------------------------------------------------------
        // OnUpdate — historical/new-bar drives zone scan + lifecycle pass;
        // ticks drive live R refresh.
        // ---------------------------------------------------------------------
        protected override void OnUpdate(UpdateArgs args)
        {
            if (this.HistoricalData == null || this.Count < 5) return;

            UpdateEmaCache();
            UpdateCvdCache();
            UpdateAtrCache();
            UpdateDailyAtrCache();

            // Re-scan zones + run lifecycle once per new bar (or on first paint).
            bool newBar = (this.Count != this.lastProcessedBarCount) &&
                          (args.Reason == UpdateReason.HistoricalBar
                           || args.Reason == UpdateReason.NewBar
                           || this.lastProcessedBarCount < 0);

            if (newBar)
            {
                // 2026-05-06: feed the TrendStateMachine the most recently
                // closed bar BEFORE the lifecycle pass runs, so the lifecycle
                // (and ScoreZone via the rubric) sees an up-to-date trend
                // state when querying TrendStateFallback / GetSnapshot. The
                // monotonic guard on last*BarIndexProcessedForTrend prevents
                // double-feeding on the same bar across NewBar+HistoricalBar
                // races (Quantower can dispatch both in quick succession).
                //
                // LTF/ITF/HTF refactor (per TP framework):
                //   - Trend feeds off ITF (TrendFromITF=true, default)
                //   - When TrendFromITF is OFF, falls back to chart-TF (legacy)
                if (this.EnableTrendDetection && this.trendStateMachine != null)
                {
                    // 2026-05-10 STRICT ITF MODE: per TP doctrine "ITF
                    // determines trend state." Previously we'd fall through
                    // to chart-TF when ITF data hadn't async-loaded yet —
                    // that polluted the state machine with chart-TF pivots
                    // until something reset it (e.g. settings change). Now,
                    // when TrendFromITF is on we ONLY feed the state machine
                    // from ITF data. If ITF isn't loaded yet, we skip the
                    // update entirely (state stays FLAT for one tick) and
                    // retry on the next OnUpdate. The async ITF fetch
                    // populates within a few ticks of indicator load.
                    HistoricalData trendFeed = null;
                    bool isItfFeed = false;
                    if (this.TrendFromITF)
                    {
                        if (this.itfTrendHistory != null && this.itfTrendHistory.Count >= 2)
                        {
                            trendFeed = this.itfTrendHistory;
                            isItfFeed = true;
                        }
                        // else: deliberately skip — never feed chart-TF when
                        //       the user has asked for ITF authority.
                    }
                    else if (this.HistoricalData != null && this.HistoricalData.Count >= 2)
                    {
                        // Legacy mode (TrendFromITF=false): chart-TF feed.
                        trendFeed = this.HistoricalData;
                        isItfFeed = false;
                    }

                    if (trendFeed != null)
                    {
                        int currentBarIndex = trendFeed.Count - 2; // last closed bar of feed
                        int lastProcessed = isItfFeed
                            ? this.lastItfBarIndexProcessedForTrend
                            : this.lastBarIndexProcessedForTrend;
                        if (currentBarIndex > lastProcessed)
                        {
                            // 2026-05-10 BUGFIX (stuck-FLAT): the previous
                            // implementation fed only the SINGLE newest closed
                            // bar each tick and then stamped lastProcessed =
                            // currentBarIndex. On indicator load, lastProcessed
                            // is -1 and currentBarIndex is e.g. 999, so bars
                            // 0..998 were silently skipped. The state machine
                            // needs alternating HH/HL pivots from history to
                            // confirm a trend, so it stayed FLAT until enough
                            // bars printed live forward. Fix: catch up from
                            // (lastProcessed + 1) to currentBarIndex, capping
                            // the initial replay to the most recent N bars
                            // (the 3-segment structure settles inside that
                            // window). isReplayingTrendHistory is set during
                            // the catch-up so handlers skip alert firing on
                            // historical events.
                            const int trendReplayCap = 500;
                            int startBar = lastProcessed + 1;
                            if (lastProcessed < 0)
                                startBar = Math.Max(1, currentBarIndex - trendReplayCap + 1);
                            if (startBar < 1) startBar = 1;

                            bool catchingUp = (currentBarIndex - startBar) >= 1;
                            if (catchingUp) this.isReplayingTrendHistory = true;

                            double tickSizeT = this.Symbol?.TickSize ?? 0.25;
                            try
                            {
                                for (int j = startBar; j <= currentBarIndex; j++)
                                {
                                    var trendBar = trendFeed[j, SeekOriginHistory.Begin] as HistoryItemBar;
                                    if (trendBar == null) continue;

                                    // Switch off the replay flag for the FINAL
                                    // bar so any state change crossing into the
                                    // live present still fires alerts normally.
                                    if (j == currentBarIndex)
                                        this.isReplayingTrendHistory = false;

                                    // CR4: serialize against OnNewLast /
                                    // OnPaintChart; event handlers also mutate
                                    // the marker lists.
                                    lock (this.tradesLock)
                                    {
                                        try
                                        {
                                            this.trendStateMachine.OnBarClose(
                                                j,
                                                trendBar.TimeLeft,
                                                trendBar.Open,
                                                trendBar.High,
                                                trendBar.Low,
                                                trendBar.Close,
                                                tickSizeT);
                                        }
                                        catch (Exception exTrend)
                                        {
                                            Core.Instance.Loggers.Log(
                                                exTrend, "TradePhantoms_IOF_v2.TrendOnBarClose");
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                this.isReplayingTrendHistory = false;
                            }

                            if (isItfFeed) this.lastItfBarIndexProcessedForTrend = currentBarIndex;
                            else           this.lastBarIndexProcessedForTrend = currentBarIndex;
                        }
                    }
                }

                // IM7: Rescan MTF (ITF/HTF) zones BEFORE ScanZones so the
                // MTFC overlap detection — which now folds the bonus into
                // each chart zone's score before the MinScore filter — sees
                // the latest higher-tier zones. Previously ApplyMTFCBonus
                // ran AFTER ScanZones had already discarded any zone whose
                // pre-bonus score fell below MinScore, defeating the whole
                // point of the bonus near the cutoff.
                if (this.UseMTFZones)
                {
                    RescanMTFZones();
                }

                ScanZones();

                if (this.UseLifecycle && this.lifecycleManager != null)
                {
                    // CR4: serialize against OnNewLast which can fire on the
                    // Quantower IO thread. The transition-diff alert detector
                    // also lives inside the lock so the Trades list (and the
                    // scalar fields RefreshLiveR mutates — R, DollarPnL,
                    // CurSL) stays consistent for the duration of the scan.
                    lock (this.tradesLock)
                    {
                        RunLifecycleForCurrentBar();
                        DetectAndFireLifecycleAlerts();
                    }
                }

                // 2026-05-11: bot-ready periodic market snapshot. Emits once
                // per closed bar AFTER scan + lifecycle so the snapshot reflects
                // the freshest state. Bundles everything an observer bot would
                // need on a per-bar tick: price, ATR, EMA, session, trend, zone
                // counts, lifecycle stats, nearest zones. Failure swallowed —
                // never blocks the bar loop.
                //
                // 2026-05-13 audit fix: only emit for LIVE new bars, not for
                // historical-bar replays during indicator load. The 500-bar
                // initial replay would otherwise flood the hub with 500
                // snapshot POSTs in rapid succession on every Remove → Add,
                // saturating the connection pool and producing the gap-driven
                // phantom fills the audit caught today.
                if (args.Reason == UpdateReason.NewBar)
                {
                    try { EmitMarketSnapshot(); } catch { }
                }
                // Heartbeat: separate cadence (≥15s). Decoupled from bar so
                // executors get a liveness signal even on slow TFs (1H bar
                // = once an hour; heartbeat still ticks every 15s here).
                try { EmitHeartbeat(); } catch { }

                // 2026-05-09 — Trading HQ bridge: emit the just-closed bar
                // so the dashboard chart pane can render real Quantower OHLC
                // instead of synthetic demo candles. HistoricalData[1] is
                // the most-recently-closed bar (index 0 is the forming bar).
                try
                {
                    var closedBar = this.HistoricalData[1] as HistoryItemBar;
                    if (closedBar != null && this.Symbol != null)
                    {
                        int barTfSec = 0;
                        try
                        {
                            var period = GetChartPeriod();
                            if (period != null)
                                barTfSec = (int)period.Value.Duration.TotalSeconds;
                        }
                        catch { /* tick/range aggregations have no duration */ }

                        EmitToBridge(new
                        {
                            type             = "bar",
                            ts               = closedBar.TimeLeft.ToString("o"),
                            symbolName       = this.Symbol.Name ?? "",
                            open             = closedBar.Open,
                            high             = closedBar.High,
                            low              = closedBar.Low,
                            close            = closedBar.Close,
                            volume           = closedBar.Volume,
                            barTimeframeSec  = barTfSec
                        });
                    }
                }
                catch { /* never throw from the bridge */ }

                // 2026-05-09 — Trading HQ bridge: emit a complete chart_state
                // snapshot so the Next.js dashboard can render exact visual
                // parity with the indicator (zones, trend breaks, control
                // points, settings). Piggybacks on the per-bar pass — no extra
                // timer. Runs AFTER ScanZones() so MTFC bonus is reflected in
                // each zone's score.
                EmitChartStateToBridge();

                this.lastProcessedBarCount = this.Count;
            }
            else if (args.Reason == UpdateReason.NewTick &&
                     this.UseLifecycle && this.lifecycleManager != null)
            {
                // Live R refresh for the active trade — uses last close. CR4:
                // lock against OnNewLast which mutates the same fields.
                var lastBar = this.HistoricalData[0] as HistoryItemBar;
                if (lastBar != null)
                {
                    lock (this.tradesLock)
                    {
                        this.lifecycleManager.RefreshLiveR(lastBar.Close);
                    }
                }
            }
        }

        // ---------------------------------------------------------------------
        // OnSettingsUpdated — push input changes into the lifecycle manager.
        // ---------------------------------------------------------------------
        protected override void OnSettingsUpdated()
        {
            base.OnSettingsUpdated();

            // Rebuild pens to reflect new colors.
            DisposePens();
            this.entryPen = new Pen(EntryLineColor, 2f) { DashStyle = DashStyle.Dash };
            this.slPen    = new Pen(SLLineColor,    2f) { DashStyle = DashStyle.Dot };
            this.tpPen    = new Pen(TPLineColor,    1.5f) { DashStyle = DashStyle.Dash };
            this.bePen    = new Pen(BELineColor,    1.5f) { DashStyle = DashStyle.DashDot };

            if (this.lifecycleManager != null)
            {
                bool wasOn = this.lifecycleManager.Trades.Count > 0;
                this.lifecycleManager.ActiveStrategy        = this.ActiveTrailStrategy;
                this.lifecycleManager.UseSequentialGate     = this.UseSequentialGate;
                this.lifecycleManager.MaxRetainedClosed     = this.MaxRetainedClosed;
                this.lifecycleManager.LifecycleHistoryBars  = this.LifecycleHistoryBars;
                this.lifecycleManager.ArmProx               = this.ArmProx;
                this.lifecycleManager.StopBufferTicks       = this.StopBufferTicks;
                this.lifecycleManager.TrailFromEntry        = this.TrailFromEntry;
                this.lifecycleManager.MaxBarsToTp1          = this.MaxBarsToTp1;
                // CR5: scale-out percentage drives Strat 5 (Runner) partial
                // close at TP1 inside the lifecycle. Wire on every settings
                // change so live tweaks take effect immediately.
                this.lifecycleManager.RunnerScaleOutPct     = this.RunnerScaleOutPct;
                this.lifecycleManager.NumTPs                = this.TpCount;
                this.lifecycleManager.TpMult                = this.TpStep;
                // CR3: keep the lifecycle's OF entry depth in lockstep with
                // the master input so PASS C uses the latest value.
                this.lifecycleManager.ActiveOFEntry         = this.OFEntry;
                // 2026-05-13: counter-trend gating + closest-zone-only arming.
                this.lifecycleManager.AllowCounterTrend         = this.AllowCounterTrend;
                this.lifecycleManager.OnlyArmClosestPerDirection = this.OnlyArmClosestPerDirection;

                // If lifecycle just got disabled, drop everything for clarity.
                if (!this.UseLifecycle && wasOn)
                {
                    this.lifecycleManager.Reset();
                    this.lastTickState.Clear();
                    this.lastTickHighestTpHit.Clear();
                    this.lastTickCurSL.Clear();
                    this.beAlertedTrades.Clear();
                }
            }

            // Rebuild alerts config — channel toggles + paths come from inputs.
            this.alertsConfig = BuildAlertsConfig();

            // IM6: Re-fetch MTF data if settings changed (UseMTFZones toggled
            // or ITFPeriod / HTFPeriod / MTFLookbackBars changed). Without
            // this, flipping UseMTFZones off→on at runtime — or rotating
            // periods on a live chart — would leave itfData / htfData stale
            // (or null) and the feature silently dead until next chart load.
            if (this.UseMTFZones && this.Symbol != null)
            {
                try { this.itfData?.Dispose(); } catch { }
                try { this.htfData?.Dispose(); } catch { }
                this.itfData = null;
                this.htfData = null;

                try
                {
                    this.itfData = TPMTF.MultiTFZoneScanner.FetchTimeframeData(
                        this.Symbol, this.ITFPeriod, this.MTFLookbackBars);
                    this.htfData = TPMTF.MultiTFZoneScanner.FetchTimeframeData(
                        this.Symbol, this.HTFPeriod, this.MTFLookbackBars);
                }
                catch (Exception ex)
                {
                    // Log but don't throw — let the next scan return empty.
                    try
                    {
                        Core.Instance.Loggers.Log(
                            "TradePhantoms IOF v2: MTF re-fetch failed: " + ex.Message);
                    }
                    catch { }
                }
            }
            else
            {
                // Turning off — dispose existing feeds so we don't pay for
                // background updates we'll never read.
                try { this.itfData?.Dispose(); } catch { }
                try { this.htfData?.Dispose(); } catch { }
                this.itfData = null;
                this.htfData = null;
            }

            // LTF override re-fetch on settings change (so the user can flip
            // it on/off or change the period mid-session without restart).
            try { this.ltfHistory?.Dispose(); } catch { }
            this.ltfHistory = null;
            if (this.LTFOverrideEnabled && this.Symbol != null)
            {
                try
                {
                    this.ltfHistory = this.Symbol.GetHistory(
                        this.LTFOverridePeriod,
                        this.Symbol.HistoryType,
                        Core.TimeUtils.DateTimeUtcNow.AddDays(-30));
                }
                catch (Exception ex)
                {
                    try { Core.Instance.Loggers.Log("TradePhantoms IOF v2: LTF re-fetch failed: " + ex.Message); } catch { }
                }
            }

            // ITF trend feed re-fetch (independent of MTF zones — trend can
            // be on without MTF zone overlay).
            try { this.itfTrendHistory?.Dispose(); } catch { }
            this.itfTrendHistory = null;
            this.lastItfBarIndexProcessedForTrend = -1;
            if (this.EnableTrendDetection && this.TrendFromITF && this.Symbol != null)
            {
                try
                {
                    this.itfTrendHistory = this.Symbol.GetHistory(
                        this.ITFPeriod,
                        this.Symbol.HistoryType,
                        Core.TimeUtils.DateTimeUtcNow.AddDays(-30));
                }
                catch (Exception ex)
                {
                    try { Core.Instance.Loggers.Log("TradePhantoms IOF v2: ITF trend re-fetch failed: " + ex.Message); } catch { }
                }
            }

            // 2026-05-06: re-apply Trend inputs on every settings change so
            // panel edits (engulfing strictness / swing lookback / close-on-
            // broken) take effect without a chart reload.
            //
            // 2026-05-11 BUGFIX: when the user toggles TrendFromITF (or other
            // settings that affect WHICH feed populates the state machine),
            // we now MUST reset the state machine. Otherwise pivots from the
            // previous feed source (e.g. chart-TF pivots collected before
            // the user enabled ITF mode) stay in pivotHistory and corrupt
            // the next confirmation pass. The historical replay loop in
            // OnUpdate will then re-populate from the correct feed
            // (lastItfBarIndexProcessedForTrend / lastBarIndexProcessedForTrend
            // are also reset above so the replay catches up from the
            // appropriate startBar).
            if (this.trendStateMachine != null)
            {
                try { this.trendStateMachine.Reset(); } catch { }
                this.trendStateMachine.RequireEngulfingForControlPoint
                    = this.RequireEngulfingControlPoints;
                this.trendStateMachine.SwingFractalLookback
                    = this.TrendSwingLookback;

                // Also reset the chart-TF replay counter — it's the partner
                // of lastItfBarIndexProcessedForTrend and we want symmetric
                // behavior whether the user is in ITF or chart-TF feed mode.
                this.lastBarIndexProcessedForTrend = -1;

                // Marker lists carry stale historical control-point / break
                // visuals from the OLD feed. Clear so the replay re-populates
                // from the correct feed on the next OnUpdate tick.
                lock (this.tradesLock)
                {
                    this.controlPointMarkers.Clear();
                    this.trendBreakMarkers.Clear();
                }
            }
            if (this.lifecycleManager != null)
            {
                this.lifecycleManager.CloseOnTrendBroken = this.CloseOnTrendBroken;
            }

            this.lastProcessedBarCount = -1;
            ShortName = $"IOFv2 (>={MinScore}/21)";
        }

        // ---------------------------------------------------------------------
        // BuildAlertsConfig — input panel → AlertsConfig DTO. Centralized so
        // OnInit and OnSettingsUpdated stay in sync. Webhook + log-file
        // channels auto-disable when the URL/path is blank.
        // ---------------------------------------------------------------------
        private TPAlerts.AlertsConfig BuildAlertsConfig()
        {
            return new TPAlerts.AlertsConfig
            {
                EnableInPlatform = this.EnableAlerts,
                EnableAudible    = this.EnableAlerts && this.AudibleAlerts,
                EnableWebhook    = this.EnableAlerts && !string.IsNullOrWhiteSpace(this.WebhookUrl),
                WebhookUrl       = this.WebhookUrl ?? "",
                EnableLogFile    = this.EnableAlerts && !string.IsNullOrWhiteSpace(this.AlertsCsvPath),
                LogFilePath      = this.AlertsCsvPath ?? "",
                EventsToAlert    = TPAlerts.AlertEventMask.All,
                SourceName       = "TradePhantoms IOF v2"
            };
        }

        // ---------------------------------------------------------------------
        // TREND event handlers (2026-05-06).
        //
        // Fired by TrendStateMachine on the indicator-thread call stack inside
        // OnBarClose (we wrapped that call in tradesLock above). The handlers
        // append marker entries — both lists are FIFO-capped to keep paint
        // bounded — and emit the matching alert. The marker lists live under
        // the same tradesLock as the lifecycle's Trades collection so paint
        // can iterate without locking twice.
        // ---------------------------------------------------------------------
        private void OnControlPointDetected_Handler(TradePhantomsIOF.Trend.ControlPoint cp)
        {
            if (cp == null) return;
            try
            {
                var marker = new TradePhantomsIOF.UI.ControlPointMarker
                {
                    Time              = cp.Time,
                    BarIndex          = cp.BarIndex,
                    Price             = cp.Price,
                    IsBull            = cp.Direction == TradePhantomsIOF.Trend.TrendState.Bull,
                    EngulfingHigh     = cp.EngulfingHigh,
                    EngulfingLow      = cp.EngulfingLow,
                    EngulfingOpen     = cp.EngulfingOpen,
                    EngulfingClose    = cp.EngulfingClose,
                    // The renderer flags the active controlling pivot via this
                    // bool; we stamp it in OnPaintChart against the latest
                    // snapshot so CTRL doesn't drift after the next CP fires.
                    IsControllingPivot = false
                };

                // We're already inside tradesLock (OnBarClose wrapper) when this
                // event fires from the per-bar feed; lock again is safe (it's a
                // recursive monitor in C#) and keeps the call site hardened
                // against future invocations from a different stack.
                lock (this.tradesLock)
                {
                    this.controlPointMarkers.Add(marker);
                    if (this.controlPointMarkers.Count > 50)
                        this.controlPointMarkers.RemoveAt(0); // FIFO cap
                }

                if (this.alertsConfig != null && this.EnableAlerts && !this.isReplayingTrendHistory)
                {
                    TPAlerts.AlertsManager.FireControlPointDetected(
                        this.alertsConfig,
                        this.Symbol?.Name ?? "?",
                        cp.Direction.ToString().ToUpper(),
                        cp.Price,
                        GetChartPeriodString());
                }

                // 2026-05-11: emit to IOF hub. Suppressed during historical
                // replay so we don't backfill the hub with stale CPs.
                if (!this.isReplayingTrendHistory)
                {
                    EmitObserverEvent("control_point_detected", new
                    {
                        direction       = cp.Direction.ToString().ToUpper(),
                        price           = cp.Price,
                        engulfing_high  = cp.EngulfingHigh,
                        engulfing_low   = cp.EngulfingLow,
                        engulfing_open  = cp.EngulfingOpen,
                        engulfing_close = cp.EngulfingClose,
                        bar_time        = cp.Time.ToString("o",
                            System.Globalization.CultureInfo.InvariantCulture),
                    });
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.OnControlPointDetected_Handler");
            }
        }

        // ---------------------------------------------------------------------
        // OnPositionAdded — track ANY position open on the chart's symbol+account,
        // not just lifecycle-armed ones. Manual entries (clicks-in without going
        // through an ARM gate) get picked up here. If a published TradeIntent
        // matches the fill, we use that intent's zone-derived plan; otherwise
        // we record a "manual" entry for tracking only.
        // ---------------------------------------------------------------------
        private void OnPositionAdded_Handler(Position position)
        {
            if (position == null || this.Symbol == null) return;

            // Filter — only positions on this chart's symbol.
            if (position.Symbol == null ||
                !string.Equals(position.Symbol.Name, this.Symbol.Name, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                bool isLong = position.Side == Side.Buy;
                double fillPrice = position.OpenPrice;
                double tickSize = this.Symbol.TickSize;
                string acctId = position.Account?.Id ?? "";

                // Look for a matching TradeIntent published by an ARMED zone.
                var intent = TradePhantomsIOF.IntentBus.TradeIntentChannel.ConsumeMatching(
                    this.Symbol.Name, acctId, isLong, fillPrice, tickSize);

                Core.Instance.Loggers.Log(
                    intent != null
                        ? $"TradePhantoms IOF v2: position #{position.Id} matched intent ZoneId={intent.ZoneId}, score={intent.Score} — using zone-derived plan"
                        : $"TradePhantoms IOF v2: position #{position.Id} opened without matching IOF zone — tracking as manual",
                    LoggingLevel.System);

                // The strategy (AutoSLTP) is the one that actually places brackets;
                // the indicator just records the intent + lifecycle state. The
                // strategy's own PositionAdded handler runs in parallel and
                // consumes its own intent reference (we don't double-consume here
                // — we use ConsumeMatching above which removes from the queue).
                //
                // Wait — that means the strategy can't consume after us. Fix:
                // re-publish the intent for the strategy to consume.
                if (intent != null)
                {
                    TradePhantomsIOF.IntentBus.TradeIntentChannel.Publish(intent);
                }

                // Mirror the fill to Trading HQ dashboard.
                EmitToBridge(new
                {
                    type = "fill",
                    ts = DateTime.UtcNow.ToString("o"),
                    tradeId = position.Id?.ToString() ?? Guid.NewGuid().ToString(),
                    symbolName = this.Symbol?.Name ?? "",
                    accountId = acctId,
                    side = isLong ? "Long" : "Short",
                    contracts = (int)Math.Abs(position.Quantity),
                    fillPrice = fillPrice,
                    zoneId = intent?.ZoneId ?? "",
                    matchedIntentId = intent?.ZoneId ?? "",
                    score = intent?.Score ?? 0
                });

                // TODO (next iteration): synthesize a TradeRecord in the
                // lifecycle for visibility on the dashboard. For now the trade
                // shows up in Quantower's native Trades panel; the indicator
                // dashboard's "Live trades" section only reflects lifecycle-armed
                // entries until we wire a "manual injection" path.
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.OnPositionAdded_Handler");
            }
        }

        private void OnTrendBroken_Handler(TradePhantomsIOF.Trend.TrendBreakEvent ev)
        {
            if (ev == null) return;
            try
            {
                var marker = new TradePhantomsIOF.UI.TrendBreakMarker
                {
                    Time          = ev.Time,
                    BarIndex      = ev.BarIndex,
                    WasBull       = ev.OldTrend == TradePhantomsIOF.Trend.TrendState.Bull,
                    BrokenPrice   = ev.BrokenControlPoint,
                    BreakBarClose = ev.BreakBarClose
                };

                int affectedTrades = 0;
                lock (this.tradesLock)
                {
                    // 2026-05-12: visual dedup. In choppy consolidation
                    // areas the state machine legitimately confirms and
                    // breaks short-lived trends multiple times near the
                    // same price (see screenshot: 8 cyan BRs clustered
                    // at ~28,770). The transitions are real and DO get
                    // emitted to the hub via the trend_break event below
                    // — but rendering all of them as separate BR markers
                    // is visual noise. Suppress a new marker when a
                    // same-direction marker already exists within
                    //   ±2 ticks of broken price AND ±2 hours of time.
                    // Chosen so a ~one-session chop window collapses to
                    // a single BR; cleanly distinct breaks at different
                    // price levels or after a real trend regime still
                    // print individually.
                    double tickSize = this.Symbol?.TickSize ?? 0.25;
                    double priceTol = tickSize * 2.0;
                    TimeSpan timeTol = TimeSpan.FromHours(2);
                    bool nearDuplicate = false;
                    for (int i = 0; i < this.trendBreakMarkers.Count; i++)
                    {
                        var existing = this.trendBreakMarkers[i];
                        if (existing.WasBull != marker.WasBull) continue;
                        if (Math.Abs(existing.BrokenPrice - marker.BrokenPrice) > priceTol) continue;
                        var dt = existing.Time - marker.Time;
                        if (dt < TimeSpan.Zero) dt = -dt;
                        if (dt > timeTol) continue;
                        nearDuplicate = true;
                        break;
                    }
                    if (!nearDuplicate)
                    {
                        this.trendBreakMarkers.Add(marker);
                        if (this.trendBreakMarkers.Count > 20)
                            this.trendBreakMarkers.RemoveAt(0); // FIFO cap
                    }

                    if (this.lifecycleManager != null)
                    {
                        foreach (var t in this.lifecycleManager.GetActive())
                        {
                            // Long active + bull broken = conflict;
                            // short active + bear broken = conflict.
                            bool conflicts =
                                ( t.IsLong && ev.OldTrend == TradePhantomsIOF.Trend.TrendState.Bull) ||
                                (!t.IsLong && ev.OldTrend == TradePhantomsIOF.Trend.TrendState.Bear);
                            if (conflicts) affectedTrades++;
                        }
                    }
                }

                if (this.alertsConfig != null && this.EnableAlerts && !this.isReplayingTrendHistory)
                {
                    TPAlerts.AlertsManager.FireTrendBroken(
                        this.alertsConfig,
                        this.Symbol?.Name ?? "?",
                        ev.OldTrend.ToString().ToUpper(),
                        $"{(ev.OldTrend == TradePhantomsIOF.Trend.TrendState.Bull ? "HL" : "LH")} {ev.BrokenControlPoint:F2}",
                        ev.BrokenControlPoint,
                        affectedTrades,
                        GetChartPeriodString());
                }

                // 2026-05-11: emit to IOF hub.
                if (!this.isReplayingTrendHistory)
                {
                    EmitObserverEvent("trend_break", new
                    {
                        old_trend         = ev.OldTrend.ToString().ToUpper(),
                        new_trend         = ev.NewTrend.ToString().ToUpper(),
                        broken_at_price   = ev.BrokenControlPoint,
                        break_bar_close   = ev.BreakBarClose,
                        affected_trades   = affectedTrades,
                        bar_time          = ev.Time.ToString("o",
                            System.Globalization.CultureInfo.InvariantCulture),
                    });
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.OnTrendBroken_Handler");
            }
        }

        private void OnTrendStateChanged_Handler(
            TradePhantomsIOF.Trend.TrendState oldState,
            TradePhantomsIOF.Trend.TrendState newState)
        {
            try
            {
                this.lastNotifiedTrendState = newState;
                this.lastTrendStateChangeAt = DateTime.UtcNow;

                if (this.RefreshZoneTrendOnFlip)
                {
                    // The next OnUpdate new-bar branch always re-runs ScanZones
                    // (via lastProcessedBarCount tracking), and ScoreTrend is now
                    // trend-state-aware. To force a refresh on this very tick,
                    // reset the bar-count cache so the next OnUpdate triggers a
                    // rescan even if the bar didn't advance.
                    this.lastProcessedBarCount = -1;
                }

                if (this.alertsConfig != null && this.EnableAlerts && !this.isReplayingTrendHistory)
                {
                    TPAlerts.AlertsManager.FireTrendChanged(
                        this.alertsConfig,
                        this.Symbol?.Name ?? "?",
                        oldState.ToString().ToUpper(),
                        newState.ToString().ToUpper(),
                        GetChartPeriodString());
                }

                // 2026-05-11: emit to IOF hub. We DO emit during replay for
                // state changes (vs CP/break) because a final state-change
                // crossing into the live present matters for the hub's view.
                // The replay flag flips off for the very last bar in the
                // replay loop, so by the time we get a "real" state change,
                // we're already past replay anyway. Sending all state
                // transitions keeps the projection in sync.
                EmitObserverEvent("trend_state_changed", new
                {
                    from = oldState.ToString().ToUpper(),
                    to   = newState.ToString().ToUpper(),
                });
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.OnTrendStateChanged_Handler");
            }
        }

        // Helper: count how many chart-TF zones agree with `trend` for a given
        // direction (long zones if isLong=true, short zones otherwise). When
        // trend is Flat we conservatively report 0. Caller sums the two
        // directions to get the total aligned count.
        private int CountZonesAlignedWithTrend(
            TradePhantomsIOF.Trend.TrendState trend, bool isLong)
        {
            if (trend == TradePhantomsIOF.Trend.TrendState.Flat) return 0;
            bool wantLong = (trend == TradePhantomsIOF.Trend.TrendState.Bull);
            if (wantLong != isLong) return 0; // counting one direction at a time
            int n = 0;
            foreach (var z in this.zones)
            {
                bool zoneIsLong = z.Type == ZoneType.RBR || z.Type == ZoneType.DBR;
                if (zoneIsLong == isLong) n++;
            }
            return n;
        }

        // Helper: counts chart-TF zones whose direction OPPOSES `trend`. Used
        // by the dashboard's OpposedZoneCount.
        private int CountZonesOpposedToTrend(TradePhantomsIOF.Trend.TrendState trend)
        {
            if (trend == TradePhantomsIOF.Trend.TrendState.Flat) return 0;
            int n = 0;
            foreach (var z in this.zones)
            {
                bool zoneIsLong = z.Type == ZoneType.RBR || z.Type == ZoneType.DBR;
                if (zoneIsLong && trend == TradePhantomsIOF.Trend.TrendState.Bear) n++;
                else if (!zoneIsLong && trend == TradePhantomsIOF.Trend.TrendState.Bull) n++;
            }
            return n;
        }

        // ---------------------------------------------------------------------
        // OnClear / Dispose — release resources.
        // ---------------------------------------------------------------------
        protected override void OnClear()
        {
            // 2026-05-13 HIGH-severity audit fix #1: reset the observer_hello
            // sentinel + the per-instance seq counter so a Remove → Add cycle
            // emits a fresh observer_hello with a fresh observer_id (or, if
            // the symbol+TF generate the same id, at least re-announces it
            // cleanly). Without this reset the new indicator instance silently
            // collides with the prior observer_id on the hub, causing seq
            // gaps and event misattribution. See iof_audit_CONTRACTS.md.
            _observerHelloEmitted = false;
            System.Threading.Interlocked.Exchange(ref _observerEventSeq, 0);

            if (this.Symbol != null)
            {
                try { this.Symbol.NewLast -= OnNewLast; } catch { }
            }
            try { Core.Instance.PositionAdded -= OnPositionAdded_Handler; } catch { }
            try { Core.Instance.PositionRemoved -= OnPositionRemoved_Handler; } catch { }
            try { TradePhantomsIOF.IntentBus.TradeIntentChannel.Clear(); } catch { }
            StopBridgeHeartbeat();

            this.htfHistory?.Dispose();
            this.htfHistory = null;

            this.itfData?.Dispose();
            this.itfData = null;

            this.htfData?.Dispose();
            this.htfData = null;

            // Daily ATR feed (2026-05-12). Must be disposed before OnInit
            // re-fetches it for the new symbol — without this the old symbol's
            // background data thread keeps running, leaking resources and
            // occasionally triggering a stale-data access on the indicator
            // thread that causes the indicator to disappear on TF/symbol change.
            try { this.dailyHistory?.Dispose(); } catch { }
            this.dailyHistory = null;

            // LTF / ITF-trend feeds (2026-05-06)
            try { this.ltfHistory?.Dispose(); } catch { }
            this.ltfHistory = null;
            try { this.itfTrendHistory?.Dispose(); } catch { }
            this.itfTrendHistory = null;
            this.lastItfBarIndexProcessedForTrend = -1;

            this.itfZones.Clear();
            this.htfZones.Clear();
            this.chartZones.Clear();
            this.mtfcOverlaps.Clear();
            this.alertedZoneIds.Clear();
            this.lastTickState.Clear();
            this.lastTickHighestTpHit.Clear();
            this.lastTickCurSL.Clear();
            this.beAlertedTrades.Clear();

            // Trend cleanup (2026-05-06): unsubscribe events so a re-init
            // doesn't double-fire onto a fresh handler set, and flush the
            // marker lists so a chart reload starts visually clean.
            if (this.trendStateMachine != null)
            {
                try
                {
                    this.trendStateMachine.OnControlPointDetected -= OnControlPointDetected_Handler;
                    this.trendStateMachine.OnTrendBroken          -= OnTrendBroken_Handler;
                    this.trendStateMachine.OnTrendStateChanged    -= OnTrendStateChanged_Handler;
                }
                catch { /* defensive — Quantower can call OnClear in odd orders */ }
            }
            // Drop the lifecycle's TrendStateFallback delegate so it doesn't
            // capture a stale state-machine reference across reloads.
            try { TradePhantomsIOF.Lifecycle.TrendStateFallback.GetCurrentTrendState = null; } catch { }
            this.controlPointMarkers.Clear();
            this.trendBreakMarkers.Clear();
            this.lastBarIndexProcessedForTrend = -1;

            DisposePens();
        }

        public override void Dispose()
        {
            OnClear();
            base.Dispose();
        }

        private void DisposePens()
        {
            this.entryPen?.Dispose();
            this.slPen?.Dispose();
            this.tpPen?.Dispose();
            this.bePen?.Dispose();
            this.entryPen = this.slPen = this.tpPen = this.bePen = null;
        }

        // ---------------------------------------------------------------------
        // NewLast — opportunistic live-R refresh at tick rate.
        // ---------------------------------------------------------------------
        private void OnNewLast(Symbol symbol, Last last)
        {
            try
            {
                // CR4: thread safety. OnNewLast runs on a Quantower IO thread;
                // OnUpdate / OnPaintChart run on the indicator thread. Lock so
                // Trades / TradeRecord fields can't be written concurrently.
                if (this.UseLifecycle && this.lifecycleManager != null)
                {
                    lock (this.tradesLock)
                    {
                        this.lifecycleManager.RefreshLiveR(last.Price);
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.OnNewLast");
            }
        }

        // ---------------------------------------------------------------------
        // BRIDGE — adapt TrailStrategies.Apply (component) to lifecycle hook.
        // ---------------------------------------------------------------------
        // Hook delegate signature (per TradeLifecycle.cs):
        //   (TrailStrategy, TradeRecord, int newHighest, double high, double low,
        //    double atr, Func<int,int,double> swingLowAt,
        //    Func<int,int,double> swingHighAt, double tickSize, bool trailFromEntry,
        //    int maxBarsToTp1, double runnerScaleOutPct, int barIndex) -> double
        //
        // Component signature:
        //   Apply(TrailStrategy strategy, bool isLong, double entry, double origSL,
        //         double curSL, double[] tps, int prevHighestTpHit,
        //         int newHighestTpHit, double high, double low, double close,
        //         double atr, Func<int,int,double> swingLowAt,
        //         Func<int,int,double> swingHighAt, double tickSize,
        //         bool trailFromEntry, int barsSinceFill, int maxBarsToTp1,
        //         double atrMultiplier) -> double
        //
        // We unpack the trade record + look up close/barsSinceFill from local
        // state. The trail component's atrMultiplier is hardcoded to 1.5 here
        // (Pine v1.4 default); a future settings input could expose it.
        private double BridgeApplyTrail(
            TPLifecycle.TrailStrategy lifeStrategy,
            TPLifecycle.TradeRecord trade,
            int newHighestTpHit,
            double high,
            double low,
            double atr,
            Func<int, int, double> swingLowAt,
            Func<int, int, double> swingHighAt,
            double tickSize,
            bool trailFromEntry,
            int maxBarsToTp1,
            double runnerScaleOutPct,
            int barIndex)
        {
            // Map enum across by integer value (encodings are aligned).
            var trailStrategy = (TPTrail.TrailStrategy)(int)lifeStrategy;

            // Pull last close from history (caller-pass would be cleaner but
            // the lifecycle's signature does not include it; this is a small
            // judgment call — the alternative is widening the hook delegate).
            double close = double.NaN;
            if (this.HistoricalData != null && this.HistoricalData.Count > 0)
            {
                var bar = this.HistoricalData[0] as HistoryItemBar;
                if (bar != null) close = bar.Close;
            }

            // barsSinceFill — derived from FillBarIndex captured at promotion.
            int barsSinceFill = (trade.FillBarIndex > 0)
                ? Math.Max(0, barIndex - trade.FillBarIndex)
                : 0;

            // prevHighestTpHit is whatever's currently stored on the trade —
            // BUG B prevention: the lifecycle hasn't yet written newHighestTpHit
            // when this hook fires, so trade.HighestTpHit is the OLD value.
            int prevHighestTp = trade.HighestTpHit;

            const double atrMultiplier = 1.5;

            return TPTrail.TrailStrategies.Apply(
                trailStrategy,
                trade.IsLong,
                trade.Entry,
                trade.OrigSL,
                trade.CurSL,
                trade.TPs,
                prevHighestTp,
                newHighestTpHit,
                high,
                low,
                close,
                atr,
                swingLowAt,
                swingHighAt,
                tickSize,
                trailFromEntry,
                barsSinceFill,
                maxBarsToTp1,
                atrMultiplier);
        }

        // ---------------------------------------------------------------------
        // LIFECYCLE PASS DRIVER
        // ---------------------------------------------------------------------
        // Builds the snapshot of active zones (from `this.zones`), constructs
        // swing-lookup lambdas, and calls RunPerBar on the most recent
        // confirmed bar.
        private void RunLifecycleForCurrentBar()
        {
            if (this.HistoricalData == null || this.HistoricalData.Count < 2) return;

            // IM1: use the most recently CLOSED bar (index 1, since 0 is the
            // in-progress live bar). barIndex matches the closed bar's position
            // in the index space (Count-2, not Count-1) — this is what PASS A
            // records as FillBarIndex so trail strategies that compute
            // barsSinceFill don't drift by one.
            var bar = this.HistoricalData[1] as HistoryItemBar;
            if (bar == null) return;

            int barIndex = this.HistoricalData.Count - 2;

            double tickSize   = this.Symbol?.TickSize ?? 0.25;
            double pointValue = ResolvePointValue();
            double atr        = ResolveAtr(1);

            // Build ZoneInfo snapshot.
            var zoneInfos = BuildZoneInfoSnapshot();

            // Swing lookups — simple 5-bar fractal scanning the lifecycle window.
            Func<int, int, double> swingLowAt = (curBar, lookback) =>
                ResolveSwingExtreme(lookback, isLow: true);
            Func<int, int, double> swingHighAt = (curBar, lookback) =>
                ResolveSwingExtreme(lookback, isLow: false);

            int contracts = this.MaxContracts;
            double dollarRisk = this.DollarRiskPerTrade;

            this.lifecycleManager.RunPerBar(
                bar.High, bar.Low, bar.Close,
                atr,
                bar.TimeLeft,
                barIndex,
                zoneInfos,
                swingLowAt,
                swingHighAt,
                tickSize,
                pointValue,
                contracts,
                dollarRisk);
        }

        // 5-bar simple-fractal swing scan over recent history. Returns NaN if
        // no qualifying pivot found within the lookback window.
        private double ResolveSwingExtreme(int lookback, bool isLow)
        {
            if (this.HistoricalData == null || this.HistoricalData.Count < 5)
                return double.NaN;

            int maxLook = Math.Min(lookback, this.HistoricalData.Count - 3);
            double best = double.NaN;

            // Index 0 is current bar — start at i=2 (so the 5-bar window
            // i-2..i+2 is inside data) and walk back.
            for (int i = 2; i <= maxLook && i + 2 < this.HistoricalData.Count; i++)
            {
                var b0 = this.HistoricalData[i + 2] as HistoryItemBar;
                var b1 = this.HistoricalData[i + 1] as HistoryItemBar;
                var b2 = this.HistoricalData[i + 0] as HistoryItemBar;
                var b3 = this.HistoricalData[i - 1] as HistoryItemBar;
                var b4 = this.HistoricalData[i - 2] as HistoryItemBar;
                if (b0 == null || b1 == null || b2 == null || b3 == null || b4 == null)
                    continue;

                if (isLow)
                {
                    // 5-bar swing low: b2 is the lowest of the five.
                    if (b2.Low <= b0.Low && b2.Low <= b1.Low &&
                        b2.Low <= b3.Low && b2.Low <= b4.Low)
                    {
                        if (double.IsNaN(best) || b2.Low > best) best = b2.Low;
                        return best; // newest qualifying — return immediately
                    }
                }
                else
                {
                    if (b2.High >= b0.High && b2.High >= b1.High &&
                        b2.High >= b3.High && b2.High >= b4.High)
                    {
                        if (double.IsNaN(best) || b2.High < best) best = b2.High;
                        return best;
                    }
                }
            }

            return best;
        }

        // ZoneInfo snapshot: convert the master's IofZone records into the
        // shape the lifecycle expects. Direction comes from zone type;
        // body/wick fields are populated from the zone's actual geometry
        // captured by MultiTFZoneScanner.ScanTimeframe.
        //
        // CR1 fix: WickHi for supply zones is now the highest base wick
        // (NOT the highest body). We just pass through the IofZone fields
        // since the scanner already populated them correctly.
        // CR2: Invalidated already reflects scanner.Active (set during
        // ScanZones), so the lifecycle's PASS A invalidation drop fires
        // as soon as price closes through the far wick.
        private List<TPLifecycle.ZoneInfo> BuildZoneInfoSnapshot()
        {
            var list = new List<TPLifecycle.ZoneInfo>(this.zones.Count);
            foreach (var z in this.zones)
            {
                bool isDemand = z.Type == ZoneType.RBR || z.Type == ZoneType.DBR;
                bool active   = !z.Invalidated;

                // Trade-eligibility filter (lifecycle ARM gating). Zones below
                // MinScore are visible on chart but cannot ARM/fill — keeps
                // the trader's eye informed without polluting trade decisions.
                if (z.Score < MinScore) continue;

                list.Add(new TPLifecycle.ZoneInfo
                {
                    Id        = z.Id,
                    Tier      = TPLifecycle.ZoneTier.LTF,  // chart-TF zones are LTF tier
                    Dir       = isDemand ? TPLifecycle.ZoneDirection.Demand
                                         : TPLifecycle.ZoneDirection.Supply,
                    BodyHi    = z.BodyHi,
                    BodyLo    = z.BodyLo,
                    WickHi    = z.WickHi,
                    WickLo    = z.WickLo,
                    IsLong    = isDemand,
                    Active    = active,
                    BaseTime  = z.StartTime,
                    Score     = z.Score
                });

                // 2026-05-07 — Publish a TradeIntent for this zone so the
                // AutoSLTP_Strategy can use the zone-derived entry/SL/TPs/
                // contracts when a position opens at this entry. This is the
                // "indicator-aware bracket placement" the user asked for.
                if (active && this.Symbol != null)
                {
                    try
                    {
                        TryPublishIntent(z, isDemand);
                    }
                    catch { /* defensive — intent publish must not block snapshot */ }
                }
            }
            return list;
        }

        /// <summary>
        /// Compute zone-derived plan (entry/SL/TPs/contracts) and publish to
        /// the TradeIntentChannel so AutoSLTP_Strategy can pick it up on fill.
        /// </summary>
        private void TryPublishIntent(IofZone z, bool isDemand)
        {
            double tickSize = this.Symbol.TickSize;
            double pointVal = ResolvePointValue();
            if (tickSize <= 0 || pointVal <= 0) return;

            // Entry/SL/TPs from the same math the lifecycle uses.
            var ofLevel = (TradePhantomsIOF.OFEntryLevel)this.OFEntry;
            double entry = TradePhantomsIOF.EntryTPMath.ComputeEntry(
                ofLevel, isDemand, z.BodyHi, z.BodyLo, z.WickHi, z.WickLo);
            double sl = TradePhantomsIOF.EntryTPMath.ComputeOrigSL(
                isDemand, z.WickHi, z.WickLo, tickSize, this.StopBufferTicks);

            double zoneHeight = Math.Abs(z.Top - z.Bottom);
            var tps = TradePhantomsIOF.EntryTPMath.ComputeTPs(
                isDemand, entry, zoneHeight, this.TpCount, this.TpStep);

            // 2026-05-11: snap to tick before anyone downstream sees these.
            // Deep-OF entries (OF25/50/75) interpolate within the wick and
            // often land sub-tick; brokers reject or silently round those.
            // Snap entry to NEAREST, SL/TP AWAY from entry so rounding can
            // only widen protection / push targets further (never tighten
            // the stop or make a TP easier to hit). See EntryTPMath
            // comments for the full policy.
            entry = TradePhantomsIOF.EntryTPMath.SnapToTickNearest(entry, tickSize);
            sl    = TradePhantomsIOF.EntryTPMath.SnapAwayFromReference(sl, entry, tickSize);
            for (int ti = 0; ti < tps.Length; ti++)
            {
                if (tps[ti] == 0.0) continue;  // "no TP at this slot" sentinel
                tps[ti] = TradePhantomsIOF.EntryTPMath.SnapAwayFromReference(
                    tps[ti], entry, tickSize);
            }

            // slDist is recomputed AFTER snapping so contract sizing reflects
            // the real (slightly wider) post-snap risk.
            double slDist = TradePhantomsIOF.EntryTPMath.ComputeSlDistance(entry, sl);
            if (slDist <= 0) return;

            int contracts = TradePhantomsIOF.EntryTPMath.ComputeContracts(
                this.DollarRiskPerTrade, slDist, pointVal, this.MaxContracts);
            if (contracts <= 0) return;  // dollar risk too small for this zone

            var intent = new TradePhantomsIOF.IntentBus.TradeIntent
            {
                SymbolName    = this.Symbol.Name,
                AccountId     = "",                          // any account on this symbol
                IsLong        = isDemand,
                Entry         = entry,
                SL            = sl,
                TP1           = tps.Length > 0 ? tps[0] : entry,
                TP2           = tps.Length > 1 ? tps[1] : entry,
                TP3           = tps.Length > 2 ? tps[2] : entry,
                Contracts     = contracts,
                DollarRisk    = this.DollarRiskPerTrade,
                ZoneHeight    = zoneHeight,
                ZoneId        = z.Id,
                Score         = (int)Math.Round(z.Score),
                FormationCode = z.Type.ToString(),
                Source        = "ARMED"
            };
            TradePhantomsIOF.IntentBus.TradeIntentChannel.Publish(intent);

            // 2026-05-11: also emit a typed zone_armed event to the IOF hub
            // so the hub's virtual_trades projection lights up. Carries the
            // full plan a bot needs to act: entry, SL, TP ladder, contracts,
            // dollar risk, OF entry depth used, displayed R:R, and the
            // score-band snapshot at arm time.
            // 2026-05-11 (Tier 1 bot-ready enrichments — STRUCTURE-ONLY):
            //
            // DOCTRINAL NOTE: ATR is deliberately NOT used in this event's
            // payload. The IOF strategy is strictly structure-based —
            // entry/SL/TP/sizing all derive from zone geometry, not from
            // a volatility envelope. ATR is still computed internally for
            // chart rendering and for the optional ATR-trail strategy, and
            // is published in market_snapshot.atr for the hub/dashboard's
            // observational use, but it must NOT influence how this zone
            // gets traded. Including atr-derived slippage budgets here
            // would tempt a bot author to add an ATR filter that fights the
            // zone signal — exactly the anti-pattern to avoid.
            //
            //   • invalidate_at_price: the price beyond which the zone is
            //     definitively dead. Equal to SL by construction; emitted
            //     separately so executor code can read it as a semantic field.
            //   • expires_at: hard wall-clock TTL. Defaults to bar duration ×
            //     20 (e.g. 20×5m = 100m on a 5m chart). Cheap signal-staleness
            //     guard for executor restarts.
            //   • risk_per_contract_ticks: STRUCTURAL — from slDist (zone
            //     geometry + StopBufferTicks). Lets per-account executors
            //     compute their own size in their own $-per-R world.
            //   • slippage_budget_ticks: STRUCTURAL — derived from
            //     StopBufferTicks, NOT from ATR. The buffer that protects
            //     SL execution is also the natural slippage tolerance.
            //   • urgency: STRUCTURAL — derived from OFEntry depth. Front =
            //     "catch any wick" (aggressive); OF75 = "wait for the deep
            //     retrace" (passive). Nothing volatility-based about it.
            double tickSizeArm = 0;
            try { tickSizeArm = this.Symbol?.TickSize ?? 0; } catch { }
            int slDistTicks = (tickSizeArm > 0)
                ? (int)Math.Round(Math.Abs(entry - sl) / tickSizeArm)
                : 0;
            int slippageBudgetTicks = Math.Max(1, this.StopBufferTicks);
            // Urgency hint: deeper OFEntry = expect price to barely reach
            // (passive limit). Front-of-zone = catch any wick (aggressive).
            string urgency;
            switch (this.OFEntry)
            {
                case TradePhantomsIOF.OFEntryLevel.Front: urgency = "aggressive"; break;
                case TradePhantomsIOF.OFEntryLevel.Of0:   urgency = "normal";     break;
                case TradePhantomsIOF.OFEntryLevel.Of25:  urgency = "normal";     break;
                case TradePhantomsIOF.OFEntryLevel.Of50:  urgency = "passive";    break;
                case TradePhantomsIOF.OFEntryLevel.Of75:  urgency = "passive";    break;
                default:                                  urgency = "normal";     break;
            }
            // Bar-duration guess for expires_at: derive from chart period.
            int barSeconds = 60;
            try
            {
                var period = GetChartPeriod();
                if (period != null) barSeconds = Math.Max(60, (int)period.Value.Duration.TotalSeconds);
            }
            catch { }
            string expiresAtIso = DateTime.UtcNow.AddSeconds(barSeconds * 20)
                .ToString("o", System.Globalization.CultureInfo.InvariantCulture);

            EmitObserverEvent("zone_armed", new
            {
                zone_id                  = z.Id,
                is_long                  = isDemand,
                entry                    = entry,
                sl                       = sl,
                tps                      = tps,
                contracts                = contracts,
                dollar_risk              = this.DollarRiskPerTrade,
                score_at_arm             = z.Score,
                infractions_at_arm       = BuildInfractionCodes(z),
                trend_at_arm             = z.ItfTrend ?? "",
                mtfc_active              = z.MtfcBonus > 0,
                is_globex_trap           = z.IsGlobexTrap,
                of_entry                 = this.OFEntry.ToString(),
                of_entry_rrr             = z.EstimatedRrr,
                estimated_target         = z.EstimatedTargetPrice,
                zone_height              = zoneHeight,
                // Tier 1 enrichments (STRUCTURE-ONLY — no ATR-derived fields):
                invalidate_at_price      = sl,
                expires_at               = expiresAtIso,
                risk_per_contract_ticks  = slDistTicks,
                slippage_budget_ticks    = slippageBudgetTicks,  // = StopBufferTicks
                urgency                  = urgency,              // from OFEntry depth
                tick_size                = tickSizeArm,
                // 2026-05-12 CVD enrichment per Agent 5: order-flow context at
                // the moment the zone armed. A bot can require divergence/
                // absorption confirmation before dispatching.
                cvd_at_arm               = ResolveSessionCvd(this.HistoricalData.Count - 1),
                cvd_rolling_at_arm       = ResolveRollingCvd(this.HistoricalData.Count - 1),
                cvd_divergence_at_arm    = ComputeCvdDivergenceFlag(15),
                absorption_at_arm        = ComputeAbsorptionFlag(),
                cvd_feed_is_real         = _cvdFeedIsReal,
                // 2026-05-13: counter-trend tag. Derived from trend state at arm:
                //   demand zone in bear trend  → true
                //   supply zone in bull trend  → true
                //   any zone in flat trend     → false (no trend bias to violate)
                is_counter_trend         = (this.trendStateMachine != null) && (
                    ( isDemand && this.trendStateMachine.CurrentState == TradePhantomsIOF.Trend.TrendState.Bear) ||
                    (!isDemand && this.trendStateMachine.CurrentState == TradePhantomsIOF.Trend.TrendState.Bull)
                ),
            });

            // Mirror to Trading HQ dashboard (fire-and-forget).
            EmitToBridge(new
            {
                type = "intent",
                ts = DateTime.UtcNow.ToString("o"),
                symbolName = this.Symbol?.Name ?? "",
                accountId = "",
                isDemand = isDemand,
                isLong = isDemand,
                zoneId = z.Id,
                formationCode = z.Type.ToString(),
                score = (int)Math.Round(z.Score),
                proximal = isDemand ? z.BodyHi : z.BodyLo,
                distal = isDemand ? z.WickLo : z.WickHi,
                bodyHi = z.BodyHi,
                bodyLo = z.BodyLo,
                wickHi = z.WickHi,
                wickLo = z.WickLo,
                entry = entry,
                sl = sl,
                tp1 = tps.Length > 0 ? tps[0] : entry,
                tp2 = tps.Length > 1 ? tps[1] : entry,
                tp3 = tps.Length > 2 ? tps[2] : entry,
                contracts = contracts,
                dollarRisk = this.DollarRiskPerTrade,
                pointValue = pointVal,
                trafficLight = "RED"  // upgraded to YELLOW/GREEN by indicator state machine when implemented
            });
        }

        // ---------------------------------------------------------------------
        // Trading HQ bridge — fire-and-forget HTTP emitter
        // ---------------------------------------------------------------------
        // Posts a JSON-serialized event to the Trading HQ FastAPI backend.
        // Errors are swallowed — the bridge is non-essential and never blocks
        // the chart thread. If localhost:8000 is unavailable, the indicator
        // continues normally with no degradation. The dashboard's `useLiveBus`
        // hook backs off and reconnects when the backend returns.
        // ---------------------------------------------------------------------
        private void EmitToBridge(object payload)
        {
            if (payload == null) return;
            string json = null;
            try
            {
                json = JsonSerializer.Serialize(payload, _bridgeJsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                string capturedJson = json;  // closure-safe capture for retry log
                _bridgeHttp
                    .PostAsync(BridgeIntentUrl, content)
                    .ContinueWith(t =>
                    {
                        // Hub down / timeout / non-2xx → persist for drain.
                        // EmitToBridge feeds the legacy /api/intent endpoint
                        // (chart_state + bar emissions); the drain script
                        // routes intent-shaped envelopes back to /api/intent.
                        bool faulted = t.IsFaulted;
                        try
                        {
                            if (!faulted && t.Result != null &&
                                !t.Result.IsSuccessStatusCode)
                                faulted = true;
                        }
                        catch { faulted = true; }
                        if (faulted) AppendPendingEvent(capturedJson);
                        try { content.Dispose(); } catch { }
                        try { t.Result.Dispose(); } catch { }
                    }, TaskScheduler.Default);
            }
            catch
            {
                // Serialization or pre-POST failure: log to retry queue if we
                // managed to serialize, otherwise accept the loss.
                if (json != null) AppendPendingEvent(json);
            }
        }

        // ---------------------------------------------------------------------
        // 2026-05-11: typed observer event emitter. Wraps the typed payload in
        // the standard envelope and POSTs to /api/iof/event on the Trading HQ
        // hub. Fire-and-forget like EmitToBridge — never blocks the chart loop
        // or throws on network failure. The hub ingests into events + maintains
        // zones / virtual_trades projections.
        //
        // Envelope shape (hub-side ingest_event in iof_hub_db.py):
        //   {
        //     "ts":           "<ISO 8601 UTC>",
        //     "observer_id":  "<root>-<tf>-<machine>",
        //     "symbol_root":  "MNQ",
        //     "symbol_full":  "MNQM6.CME",
        //     "chart_tf":     "5m",
        //     "type":         "<event type>",
        //     "payload":      { ... }
        //   }
        // ---------------------------------------------------------------------
        private void EmitObserverEvent(string eventType, object payload)
        {
            if (string.IsNullOrEmpty(eventType)) return;
            try
            {
                if (string.IsNullOrEmpty(_observerId))
                    _observerId = BuildObserverId();

                string symbolFull = this.Symbol?.Name ?? "";
                string symbolRoot = ExtractSymbolRoot(symbolFull);
                string chartTf = GetChartPeriodString();
                string tsIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                    System.Globalization.CultureInfo.InvariantCulture);

                long seq = System.Threading.Interlocked.Increment(ref _observerEventSeq);
                var envelope = new
                {
                    schema_version    = IofObserverApiVersion,
                    code_version      = _indicatorVersion,  // for forward-test cohort segmentation
                    seq               = seq,
                    ts                = tsIso,
                    observer_id       = _observerId,
                    symbol_root       = symbolRoot,
                    symbol_full       = symbolFull,
                    chart_tf          = chartTf,
                    type              = eventType,
                    payload           = payload ?? new { },
                };

                string json = JsonSerializer.Serialize(envelope, _bridgeJsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                string capturedJson = json;
                _bridgeHttp
                    .PostAsync(BridgeIofEventUrl, content)
                    .ContinueWith(t =>
                    {
                        // 2026-05-13: hub-down / timeout / non-2xx → persist
                        // envelope for the drain script to replay later.
                        // Without this we silently lost every event during
                        // every hub restart, creating the snapshot gaps the
                        // audit then flagged as outcome corruption.
                        bool faulted = t.IsFaulted;
                        try
                        {
                            if (!faulted && t.Result != null &&
                                !t.Result.IsSuccessStatusCode)
                                faulted = true;
                        }
                        catch { faulted = true; }
                        if (faulted) AppendPendingEvent(capturedJson);
                        try { content.Dispose(); } catch { }
                        try { t.Result.Dispose(); } catch { }
                    }, TaskScheduler.Default);
            }
            catch
            {
                // Never throw from the bridge.
            }
        }

        // 2026-05-11 (Tier 1): heartbeat for executor reliability. Fires
        // every N closed bars (capped to ~15 s minimum cadence) so the hub
        // can detect a stalled indicator. Lightweight payload — uptime,
        // last bar timestamp, current zone count. Failure-swallowed.
        private DateTime _indicatorStartUtc = DateTime.UtcNow;
        private DateTime _lastHeartbeatUtc  = DateTime.MinValue;

        private void EmitHeartbeat()
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastHeartbeatUtc).TotalSeconds < 15) return;
                _lastHeartbeatUtc = now;
                int activeZoneCount = 0;
                int tradeableZoneCount = 0;
                for (int i = 0; i < this.zones.Count; i++)
                {
                    var z = this.zones[i];
                    if (z == null || z.Invalidated) continue;
                    activeZoneCount++;
                    if (z.Score >= this.MinScore) tradeableZoneCount++;
                }
                string lastBarIso = "";
                try
                {
                    if (this.HistoricalData != null && this.HistoricalData.Count >= 2)
                    {
                        var b = this.HistoricalData[1] as HistoryItemBar;
                        if (b != null)
                            lastBarIso = b.TimeLeft.ToString("o",
                                System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                catch { }
                EmitObserverEvent("heartbeat", new
                {
                    uptime_seconds       = (long)(now - _indicatorStartUtc).TotalSeconds,
                    last_bar_ts          = lastBarIso,
                    active_zone_count    = activeZoneCount,
                    tradeable_zone_count = tradeableZoneCount,
                    itf_zone_count       = this.itfZones?.Count ?? 0,
                    htf_zone_count       = this.htfZones?.Count ?? 0,
                    mtfc_overlap_count   = this.mtfcOverlaps?.Count ?? 0,
                });
            }
            catch { /* never throw */ }
        }

        // 2026-05-11 (Tier 1): compute Prior Day High / Low and Initial Balance
        // for the market_snapshot. Cheap one-pass walk through HistoricalData.
        // IB = first 60 minutes of RTH; useful for "trapped breakout" rules.
        private void ComputePriorDayAndIB(DateTime nowLocalEt,
                                          out double pdh, out double pdl,
                                          out double ibHi, out double ibLo,
                                          out bool ibComplete)
        {
            pdh = double.NaN; pdl = double.NaN;
            ibHi = double.NaN; ibLo = double.NaN;
            ibComplete = false;
            if (this.HistoricalData == null) return;
            try
            {
                var tz = ResolveEasternTz();
                // PRIOR day's RTH window.
                var prevDay = nowLocalEt.Date.AddDays(-1);
                var prevRthStart = prevDay.AddHours(9).AddMinutes(30);
                var prevRthEnd   = prevDay.AddHours(16).AddMinutes(15);
                // TODAY's first 60 minutes of RTH (initial balance).
                var todayRthStart = nowLocalEt.Date.AddHours(9).AddMinutes(30);
                var ibEnd         = todayRthStart.AddHours(1);

                var prevRthStartUtc = TimeZoneInfo.ConvertTimeToUtc(prevRthStart, tz);
                var prevRthEndUtc   = TimeZoneInfo.ConvertTimeToUtc(prevRthEnd,   tz);
                var todayRthStartUtc = TimeZoneInfo.ConvertTimeToUtc(todayRthStart, tz);
                var ibEndUtc        = TimeZoneInfo.ConvertTimeToUtc(ibEnd,        tz);

                int total = this.HistoricalData.Count;
                for (int i = 0; i < total; i++)
                {
                    var b = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (b == null) continue;
                    // Prior day RTH range.
                    if (b.TimeLeft >= prevRthStartUtc && b.TimeLeft < prevRthEndUtc)
                    {
                        if (double.IsNaN(pdh) || b.High > pdh) pdh = b.High;
                        if (double.IsNaN(pdl) || b.Low  < pdl) pdl = b.Low;
                    }
                    // Initial balance (today's first hour RTH).
                    if (b.TimeLeft >= todayRthStartUtc && b.TimeLeft < ibEndUtc)
                    {
                        if (double.IsNaN(ibHi) || b.High > ibHi) ibHi = b.High;
                        if (double.IsNaN(ibLo) || b.Low  < ibLo) ibLo = b.Low;
                    }
                }
                ibComplete = nowLocalEt >= ibEnd;
            }
            catch { /* leave as NaN */ }
        }

        // 2026-05-11: periodic market_snapshot event. Bundles everything an
        // observer bot needs to evaluate trade rules on a per-bar cadence:
        //   • current price + ATR + EMA
        //   • session classification (RTH / ETH) + time-of-day
        //   • trend state machine snapshot (state, controlling pivot, last CP)
        //   • zone counts by tier + state
        //   • nearest 5 zones to current price (any tier, any direction)
        //   • lifecycle stats (active/closed counts, total R, win rate)
        //   • volume profile context if available (current-bar POC + delta)
        // Emitted once per closed bar; failure-safe.
        private void EmitMarketSnapshot()
        {
            if (this.Symbol == null) return;
            if (this.HistoricalData == null || this.HistoricalData.Count < 2) return;

            var bar = this.HistoricalData[1] as HistoryItemBar;
            if (bar == null) return;

            double currentPrice = bar.Close;
            double atrNow = 0;
            try { atrNow = ResolveAtr(1); } catch { }
            double emaNow = 0;
            try
            {
                if (this.emaCache != null && this.emaCache.Length > 1)
                    emaNow = this.emaCache[1];
            }
            catch { }

            // Session classification — uses Eastern time. ETH = previous day
            // 16:15 EST → today 09:30 EST; RTH = 09:30 → 16:15.
            string session;
            DateTime sessionLocal;
            try
            {
                sessionLocal = TimeZoneInfo.ConvertTimeFromUtc(
                    bar.TimeLeft.ToUniversalTime(),
                    ResolveEasternTz());
                session = ClassifySession(sessionLocal);
            }
            catch
            {
                sessionLocal = bar.TimeLeft;
                session = "UNKNOWN";
            }

            // Trend snapshot.
            object trendBlock;
            try
            {
                var snap = this.trendStateMachine?.GetSnapshot();
                if (snap != null)
                {
                    trendBlock = new
                    {
                        state                  = snap.State.ToString().ToUpperInvariant(),
                        controlling_pivot      = double.IsNaN(snap.ControllingPivotPrice)
                                                  ? (double?)null
                                                  : (double?)snap.ControllingPivotPrice,
                        current_cp_price       = snap.CurrentControlPoint?.Price,
                        last_break_price       = snap.LastBreak?.BrokenControlPoint,
                        last_break_ts          = snap.LastBreak?.Time.ToString("o",
                            System.Globalization.CultureInfo.InvariantCulture),
                    };
                }
                else
                {
                    trendBlock = new { state = "UNKNOWN" };
                }
            }
            catch { trendBlock = new { state = "UNKNOWN" }; }

            // Zone counts by tier + state.
            int ltfActive = 0, ltfTradeable = 0, ltfInvalid = 0;
            for (int i = 0; i < this.zones.Count; i++)
            {
                var z = this.zones[i];
                if (z == null) continue;
                if (z.Invalidated) { ltfInvalid++; continue; }
                ltfActive++;
                if (z.Score >= this.MinScore) ltfTradeable++;
            }
            int itfCount = (this.itfZones?.Count ?? 0);
            int htfCount = (this.htfZones?.Count ?? 0);

            // Nearest 5 zones to current price (any tier, any direction).
            var nearby = new List<object>(6);
            try
            {
                var ranked = new List<(double dist, IofZone z)>(this.zones.Count);
                for (int i = 0; i < this.zones.Count; i++)
                {
                    var z = this.zones[i];
                    if (z == null || z.Invalidated) continue;
                    double zMid = (z.Top + z.Bottom) / 2.0;
                    ranked.Add((Math.Abs(currentPrice - zMid), z));
                }
                ranked.Sort((a, b) => a.dist.CompareTo(b.dist));
                int take = Math.Min(5, ranked.Count);
                for (int i = 0; i < take; i++)
                {
                    var z = ranked[i].z;
                    nearby.Add(new
                    {
                        zone_id        = z.Id,
                        tier           = "LTF",
                        formation      = z.Type.ToString(),
                        is_demand      = (z.Type == ZoneType.RBR || z.Type == ZoneType.DBR),
                        score          = z.Score,
                        top            = z.Top,
                        bottom         = z.Bottom,
                        distance_pts   = ranked[i].dist,
                        is_tradeable   = z.Score >= this.MinScore,
                        is_globex_trap = z.IsGlobexTrap,
                        infractions    = BuildInfractionCodes(z),
                    });
                }
            }
            catch { /* nearby stays whatever we built */ }

            // Lifecycle stats.
            object lifecycleBlock;
            try
            {
                if (this.lifecycleManager != null)
                {
                    var s = this.lifecycleManager.ComputeStats();
                    int armedNow = 0, activeNow = 0;
                    foreach (var t in this.lifecycleManager.GetActive())
                    {
                        if (t == null) continue;
                        if (t.State == TPLifecycle.TradeState.Armed)       armedNow++;
                        else if (t.State == TPLifecycle.TradeState.Active) activeNow++;
                    }
                    lifecycleBlock = new
                    {
                        armed_count   = armedNow,
                        active_count  = activeNow,
                        closed_count  = s.Closed,
                        wins          = s.Wins,
                        losses        = s.Losses,
                        breakevens    = s.BreakEvens,
                        win_rate      = s.WinRate,
                        profit_factor = double.IsInfinity(s.ProfitFactor) ? (double?)null : s.ProfitFactor,
                        total_r       = s.TotalR,
                        total_dollar  = s.TotalDollar,
                        avg_win_r     = s.AvgWinR,
                        avg_loss_r    = s.AvgLossR,
                    };
                }
                else
                {
                    lifecycleBlock = new { armed_count = 0, active_count = 0 };
                }
            }
            catch { lifecycleBlock = new { }; }

            // Volume profile + 2026-05-12 CVD enrichment per Agent 5
            // (CVD methodology synthesis). Adds session_cvd, rolling_cvd,
            // divergence flag, absorption flag, and a feed-quality flag so
            // a bot can decide whether to trust the order-flow data at all.
            object vpBlock = new { };
            try
            {
                var va = bar.VolumeAnalysisData;
                double pocPrice = double.NaN;
                if (va != null)
                {
                    double maxVol = 0;
                    if (va.PriceLevels != null)
                    {
                        foreach (var kv in va.PriceLevels)
                        {
                            if (kv.Value != null && kv.Value.Volume > maxVol)
                            {
                                maxVol = kv.Value.Volume;
                                pocPrice = kv.Key;
                            }
                        }
                    }
                }
                int divFlag = 0;
                int absFlag = 0;
                try { divFlag = ComputeCvdDivergenceFlag(15); } catch { }
                try { absFlag = ComputeAbsorptionFlag(); } catch { }

                vpBlock = new
                {
                    bar_poc          = double.IsNaN(pocPrice) ? (double?)null : (double?)pocPrice,
                    bar_volume       = bar.Volume,
                    bar_delta        = va?.Total?.Delta,
                    session_cvd      = ResolveSessionCvd(this.HistoricalData.Count - 1),
                    rolling_cvd_20   = ResolveRollingCvd(this.HistoricalData.Count - 1),
                    cvd_divergence   = divFlag,    // +1 bullish, -1 bearish, 0 none
                    absorption       = absFlag,    // +1 bullish, -1 bearish, 0 none
                    cvd_feed_is_real = _cvdFeedIsReal,
                };
            }
            catch { /* leave as empty */ }

            // Globex high/low for the current ETH window — useful for the
            // upcoming Globex Trap rules. Computed cheaply by walking the
            // recent bars in the active ETH window.
            object globexBlock;
            try
            {
                ComputeGlobexHighLow(sessionLocal, out double gHi, out double gLo,
                                     out DateTime gStart, out DateTime gEnd);
                globexBlock = new
                {
                    high          = double.IsNaN(gHi) ? (double?)null : (double?)gHi,
                    low           = double.IsNaN(gLo) ? (double?)null : (double?)gLo,
                    window_start  = gStart == DateTime.MinValue ? null :
                        gStart.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    window_end    = gEnd == DateTime.MinValue ? null :
                        gEnd.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    in_progress   = sessionLocal >= gStart && sessionLocal < gEnd,
                };
            }
            catch { globexBlock = new { }; }

            // 2026-05-11 (Tier 1): Prior Day High/Low + Initial Balance.
            // PDH/PDL are universal key levels; IB is the first hour of RTH
            // and the basis for many trapped-breakout setups.
            object keyLevelsBlock;
            try
            {
                ComputePriorDayAndIB(sessionLocal, out double pdh, out double pdl,
                                     out double ibHi, out double ibLo, out bool ibComplete);
                keyLevelsBlock = new
                {
                    pdh         = double.IsNaN(pdh)  ? (double?)null : (double?)pdh,
                    pdl         = double.IsNaN(pdl)  ? (double?)null : (double?)pdl,
                    ib_high     = double.IsNaN(ibHi) ? (double?)null : (double?)ibHi,
                    ib_low      = double.IsNaN(ibLo) ? (double?)null : (double?)ibLo,
                    ib_complete = ibComplete,
                };
            }
            catch { keyLevelsBlock = new { }; }

            EmitObserverEvent("market_snapshot", new
            {
                price              = currentPrice,
                bar_open           = bar.Open,
                bar_high           = bar.High,
                bar_low            = bar.Low,
                bar_volume         = bar.Volume,
                bar_ts             = bar.TimeLeft.ToString("o",
                    System.Globalization.CultureInfo.InvariantCulture),
                atr                = atrNow,
                atr_percentile     = ComputeDailyAtrPercentile(),  // 0..1 daily ATR rank (60-day rolling); null when warming up
                ema                = emaNow,
                session            = session,
                local_time_eastern = sessionLocal.ToString("HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture),
                trend              = trendBlock,
                zone_counts        = new
                {
                    ltf_active    = ltfActive,
                    ltf_tradeable = ltfTradeable,
                    ltf_invalid   = ltfInvalid,
                    itf_count     = itfCount,
                    htf_count     = htfCount,
                    mtfc_overlaps = (this.mtfcOverlaps?.Count ?? 0),
                },
                nearby_zones       = nearby,
                lifecycle          = lifecycleBlock,
                volume_profile     = vpBlock,
                globex             = globexBlock,
                key_levels         = keyLevelsBlock,
            });
        }

        // ── Session classification helpers ──────────────────────────────
        // ETH = 16:15 ET previous day → 09:30 ET today (per Globex doctrine).
        // RTH = 09:30 ET → 16:15 ET same day.
        // PRE / POST split optional — for v1 we use binary RTH/ETH.
        private static string ClassifySession(DateTime localEt)
        {
            var t = localEt.TimeOfDay;
            // RTH window
            var rthStart = new TimeSpan(9, 30, 0);
            var rthEnd   = new TimeSpan(16, 15, 0);
            if (t >= rthStart && t < rthEnd) return "RTH";
            // Everything else is overnight / electronic.
            return "ETH";
        }

        private static TimeZoneInfo ResolveEasternTz()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { }
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch { }
            return TimeZoneInfo.Local;
        }

        // Compute Globex (ETH) window high/low. The window containing or
        // ending on `localEt`. Walks recent HistoricalData bars whose local
        // time falls inside the window.
        private void ComputeGlobexHighLow(DateTime localEt,
                                          out double high, out double low,
                                          out DateTime windowStart, out DateTime windowEnd)
        {
            high = double.NaN;
            low  = double.NaN;
            windowStart = DateTime.MinValue;
            windowEnd   = DateTime.MinValue;

            // Resolve the relevant ETH window's start and end in local ET.
            var dayDate = localEt.Date;
            var rthOpen = dayDate.AddHours(9).AddMinutes(30);
            var ethStart = dayDate.AddDays(-1).AddHours(16).AddMinutes(15);
            var ethEnd   = rthOpen;
            if (localEt >= dayDate.AddHours(16).AddMinutes(15))
            {
                // We're in the start of a new ETH window. Use it.
                ethStart = dayDate.AddHours(16).AddMinutes(15);
                ethEnd   = dayDate.AddDays(1).AddHours(9).AddMinutes(30);
            }
            windowStart = ethStart;
            windowEnd   = ethEnd;

            // Convert window to UTC for bar timestamp comparison.
            DateTime ethStartUtc, ethEndUtc;
            try
            {
                var tz = ResolveEasternTz();
                ethStartUtc = TimeZoneInfo.ConvertTimeToUtc(ethStart, tz);
                ethEndUtc   = TimeZoneInfo.ConvertTimeToUtc(ethEnd,   tz);
            }
            catch
            {
                // Fall back to naive UTC compare.
                ethStartUtc = ethStart;
                ethEndUtc   = ethEnd;
            }

            int total = this.HistoricalData.Count;
            for (int i = 0; i < total; i++)
            {
                var b = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (b == null) continue;
                if (b.TimeLeft < ethStartUtc) continue;
                if (b.TimeLeft >= ethEndUtc) continue;
                if (double.IsNaN(high) || b.High > high) high = b.High;
                if (double.IsNaN(low)  || b.Low  < low)  low  = b.Low;
            }
        }

        // Build a stable per-indicator-instance observer id.
        //   Convention: "{root}-{tf}-{machinename}", lowercased.
        // E.g., "mnq-5m-trading-workstation". Used by the hub to tell
        // observers apart across prop-firm-account Quantower workspaces.
        private string BuildObserverId()
        {
            try
            {
                string root = ExtractSymbolRoot(this.Symbol?.Name ?? "");
                string tf   = GetChartPeriodString();
                string host = "";
                try { host = Environment.MachineName ?? ""; } catch { }
                if (string.IsNullOrEmpty(host)) host = "host";
                string combined = (root + "-" + tf + "-" + host)
                    .ToLowerInvariant()
                    .Replace(" ", "-");
                return combined;
            }
            catch
            {
                return "iof-observer";
            }
        }

        // ---------------------------------------------------------------------
        // ComputeSizing — shared helper that derives (contracts, dollarRisk)
        // for a zone using the same EntryTPMath path that TryPublishIntent uses
        // for live armed-zone publication. Keeps the chart_state snapshot, the
        // QT zone label, and the intent path numerically aligned.
        //
        // Returns (0, 0) when:
        //   - tickSize or pointValue is non-positive (symbol not loaded)
        //   - SL distance is non-positive (degenerate zone geometry)
        //   - ComputeContracts returns <= 0 (DollarRiskPerTrade too small for
        //     this zone's stop distance)
        // ---------------------------------------------------------------------
        private (int Contracts, double DollarRisk) ComputeSizing(IofZone z, double tickSize, double pointValue)
        {
            if (z == null) return (0, 0);
            if (tickSize <= 0 || pointValue <= 0) return (0, 0);
            try
            {
                bool isDemand = (z.Type == ZoneType.RBR || z.Type == ZoneType.DBR);
                var ofLevel = (TradePhantomsIOF.OFEntryLevel)this.OFEntry;
                double entry = TradePhantomsIOF.EntryTPMath.ComputeEntry(
                    ofLevel, isDemand, z.BodyHi, z.BodyLo, z.WickHi, z.WickLo);
                double sl = TradePhantomsIOF.EntryTPMath.ComputeOrigSL(
                    isDemand, z.WickHi, z.WickLo, tickSize, this.StopBufferTicks);
                double slDist = TradePhantomsIOF.EntryTPMath.ComputeSlDistance(entry, sl);
                if (slDist <= 0) return (0, 0);

                int contracts = TradePhantomsIOF.EntryTPMath.ComputeContracts(
                    this.DollarRiskPerTrade, slDist, pointValue, this.MaxContracts);
                if (contracts <= 0) return (0, 0);
                return (contracts, this.DollarRiskPerTrade);
            }
            catch
            {
                return (0, 0);
            }
        }

        // ---------------------------------------------------------------------
        // EmitChartStateToBridge — publishes a full snapshot of EVERYTHING the
        // indicator currently draws on the chart to the Trading HQ bridge so
        // the Next.js dashboard can render exact visual parity. Called once per
        // closed bar from OnUpdate, AFTER ScanZones() has applied MTFC bonus.
        //
        // For each visible zone (Invalidated == false AND Score >= MinDrawScore)
        // we duplicate the EntryTPMath path the live intent emission uses, so
        // refEntry/refSL/refTP1-3 match what the lifecycle would publish if the
        // zone became armed. The dashboard renders these as the orange "ref
        // lines" overlay when settings.showReferenceLines is true.
        //
        // isArmed is derived by intersecting zone IDs against the lifecycle
        // manager's currently-Armed trades. trafficLight is "RED" for armed
        // (mirroring TryPublishIntent's hardcoded value pending the upgraded
        // YELLOW/GREEN state machine) and "NONE" otherwise.
        //
        // Errors are swallowed — the bridge must never throw into the chart
        // thread. controlPointMarkers / trendBreakMarkers are read under
        // tradesLock per existing convention.
        // ---------------------------------------------------------------------
        private void EmitChartStateToBridge()
        {
            try
            {
                int barTfSec = 0;
                try
                {
                    var period = GetChartPeriod();
                    if (period != null)
                        barTfSec = (int)period.Value.Duration.TotalSeconds;
                }
                catch { /* tick/range aggregations have no duration */ }

                // Build a set of currently-armed zone IDs from the lifecycle so
                // each zone in the snapshot can be tagged with isArmed.
                var armedZoneIds = new HashSet<string>(StringComparer.Ordinal);
                if (this.UseLifecycle && this.lifecycleManager != null)
                {
                    try
                    {
                        foreach (var t in this.lifecycleManager.GetActive())
                        {
                            if (t != null
                                && t.State == TPLifecycle.TradeState.Armed
                                && !string.IsNullOrEmpty(t.ZoneId))
                            {
                                armedZoneIds.Add(t.ZoneId);
                            }
                        }
                    }
                    catch { /* defensive — armed set is best-effort */ }
                }

                // Pre-resolve params used by EntryTPMath so we don't redo the
                // lookup per zone. tickSize can be 0 on unloaded symbols —
                // in that case we emit zone geometry but leave the ref*
                // fields equal to entry as a safe fallback. pointValue is
                // resolved here for per-zone contracts/dollarRisk sizing in
                // the snapshot (matches the math TryPublishIntent uses).
                double tickSize = 0;
                try { tickSize = this.Symbol?.TickSize ?? 0; } catch { }
                double pointValue = 0;
                try { pointValue = ResolvePointValue(); } catch { }
                var ofLevel = (TradePhantomsIOF.OFEntryLevel)this.OFEntry;

                // Per-emission map of the zones we actually emit. Keyed by ZoneId
                // and used immediately after for violation diffing AND to replace
                // _previousZoneSnapshots once the emission is complete.
                var currentZoneSnapshots = new Dictionary<string, ZoneSnapshot>(StringComparer.Ordinal);

                var zonesPayload = new List<object>(this.zones.Count);
                foreach (var z in this.zones)
                {
                    if (z == null) continue;
                    if (z.Invalidated) continue;
                    if (z.Score < this.MinDrawScore) continue;

                    bool isDemand = (z.Type == ZoneType.RBR || z.Type == ZoneType.DBR);

                    // Reference plan via the same math path TryPublishIntent uses.
                    double refEntry = isDemand ? z.BodyHi : z.BodyLo;
                    double refSL    = refEntry;
                    double refTp1   = refEntry, refTp2 = refEntry, refTp3 = refEntry;
                    try
                    {
                        if (tickSize > 0)
                        {
                            refEntry = TradePhantomsIOF.EntryTPMath.ComputeEntry(
                                ofLevel, isDemand, z.BodyHi, z.BodyLo, z.WickHi, z.WickLo);
                            refSL = TradePhantomsIOF.EntryTPMath.ComputeOrigSL(
                                isDemand, z.WickHi, z.WickLo, tickSize, this.StopBufferTicks);
                            double zoneHeight = Math.Abs(z.Top - z.Bottom);
                            var tps = TradePhantomsIOF.EntryTPMath.ComputeTPs(
                                isDemand, refEntry, zoneHeight, this.TpCount, this.TpStep);
                            refTp1 = tps.Length > 0 ? tps[0] : refEntry;
                            refTp2 = tps.Length > 1 ? tps[1] : refEntry;
                            refTp3 = tps.Length > 2 ? tps[2] : refEntry;
                        }
                    }
                    catch { /* fall back to refEntry placeholders set above */ }

                    bool isArmed = armedZoneIds.Contains(z.Id ?? "");
                    string trafficLight = isArmed ? "RED" : "NONE";

                    // Per-zone position sizing — same math as TryPublishIntent.
                    // ComputeSizing is internally try/catch'd and returns (0, 0)
                    // on any failure (unloaded symbol, degenerate geometry,
                    // dollar risk too small for stop distance). We still emit
                    // the zone with zero sizing so the dashboard sees it.
                    int zContracts = 0;
                    double zDollarRisk = 0;
                    try
                    {
                        var sizing = ComputeSizing(z, tickSize, pointValue);
                        zContracts = sizing.Contracts;
                        zDollarRisk = sizing.DollarRisk;
                    }
                    catch { /* fall through with zeroes */ }

                    bool zTradeable = z.Score >= this.MinScore;

                    zonesPayload.Add(new
                    {
                        id           = z.Id,
                        formation    = z.Type.ToString(),
                        score        = z.Score,
                        maxScore     = 21,
                        drawScore    = this.MinDrawScore,
                        basesCount   = z.BaseCandleCount,
                        trendFactor  = z.ItfTrend ?? "-",
                        isDemand     = isDemand,
                        proximal     = isDemand ? z.BodyHi : z.BodyLo,
                        distal       = isDemand ? z.WickLo : z.WickHi,
                        bodyHi       = z.BodyHi,
                        bodyLo       = z.BodyLo,
                        wickHi       = z.WickHi,
                        wickLo       = z.WickLo,
                        startTime    = z.StartTime.ToString("o"),
                        endTime      = z.EndTime.ToString("o"),
                        refEntry     = refEntry,
                        refSL        = refSL,
                        refTP1       = refTp1,
                        refTP2       = refTp2,
                        refTP3       = refTp3,
                        isArmed      = isArmed,
                        trafficLight = trafficLight,
                        tradeable    = zTradeable,
                        mtfcBonus    = z.MtfcBonus,
                        touchCount   = z.TouchCount,
                        maxPenetrationPct = z.MaxPenetrationPct,
                        invalidated  = z.Invalidated,
                        gradeBreakdown = new
                        {
                            range     = z.RangeScore,
                            time      = z.TimeScore,
                            purity    = z.PurityScore,
                            strength  = z.StrengthScore,
                            trend     = z.TrendScore,
                            rrr       = z.RrrScore,
                            juice     = z.JuiceScore,
                            mtfcBonus = z.MtfcBonus
                        },
                        contracts    = zContracts,
                        dollarRisk   = zDollarRisk
                    });

                    if (!string.IsNullOrEmpty(z.Id))
                    {
                        currentZoneSnapshots[z.Id] = new ZoneSnapshot
                        {
                            Score       = z.Score,
                            TouchCount  = z.TouchCount,
                            Tradeable   = zTradeable,
                            IsArmed     = isArmed
                        };
                    }
                }

                // Snapshot marker lists under tradesLock — the trend-break and
                // control-point handlers mutate these on background threads.
                var trendBreaksPayload = new List<object>();
                var controlPointsPayload = new List<object>();
                lock (this.tradesLock)
                {
                    if (this.ShowTrendBreaks && this.trendBreakMarkers != null)
                    {
                        foreach (var m in this.trendBreakMarkers)
                        {
                            if (m == null) continue;
                            trendBreaksPayload.Add(new
                            {
                                time          = m.Time.ToString("o"),
                                barIndex      = m.BarIndex,
                                wasBull       = m.WasBull,
                                brokenPrice   = m.BrokenPrice,
                                breakBarClose = m.BreakBarClose
                            });
                        }
                    }
                    if (this.EnableTrendDetection && this.controlPointMarkers != null)
                    {
                        foreach (var m in this.controlPointMarkers)
                        {
                            if (m == null) continue;
                            controlPointsPayload.Add(new
                            {
                                time               = m.Time.ToString("o"),
                                barIndex           = m.BarIndex,
                                price              = m.Price,
                                isBull             = m.IsBull,
                                isControllingPivot = m.IsControllingPivot
                            });
                        }
                    }
                }

                // ---------------------------------------------------------
                // Violations diff: compare currentZoneSnapshots against the
                // _previousZoneSnapshots captured at the end of the prior
                // emission. Each transition (score change / tradeable flip /
                // touch / arm flip) becomes a violation entry. Zones present
                // in the prior snapshot but not in the current one are
                // emitted as "invalidated" (they may have been filtered out
                // because Invalidated=true OR because Score dropped below
                // MinDrawScore — either way the consumer treats them as gone).
                // Wrapped in try/catch so a bug in the diff logic can never
                // block the chart_state emit itself.
                // ---------------------------------------------------------
                var violationsPayload = new List<object>();
                try
                {
                    string vts = DateTime.UtcNow.ToString("o");

                    foreach (var kv in currentZoneSnapshots)
                    {
                        string zid = kv.Key;
                        var cur = kv.Value;
                        if (!_previousZoneSnapshots.TryGetValue(zid, out var prev))
                            continue;   // newly seen zone — not a violation

                        if (cur.Score < prev.Score)
                        {
                            violationsPayload.Add(new
                            {
                                zoneId   = zid,
                                ts       = vts,
                                type     = "score_drop",
                                oldValue = (object)prev.Score,
                                newValue = (object)cur.Score,
                                delta    = (object)(cur.Score - prev.Score),
                                reason   = ""
                            });
                            // 2026-05-11: also emit as a typed hub event so
                            // bots don't have to diff chart_state snapshots.
                            EmitObserverEvent("zone_score_changed", new
                            {
                                zone_id = zid,
                                from    = prev.Score,
                                to      = cur.Score,
                                delta   = cur.Score - prev.Score,
                                reason  = "drop",
                                new_infractions = BuildInfractionCodesById(zid),
                            });
                        }
                        else if (cur.Score > prev.Score)
                        {
                            violationsPayload.Add(new
                            {
                                zoneId   = zid,
                                ts       = vts,
                                type     = "score_increase",
                                oldValue = (object)prev.Score,
                                newValue = (object)cur.Score,
                                delta    = (object)(cur.Score - prev.Score),
                                reason   = ""
                            });
                            EmitObserverEvent("zone_score_changed", new
                            {
                                zone_id = zid,
                                from    = prev.Score,
                                to      = cur.Score,
                                delta   = cur.Score - prev.Score,
                                reason  = "increase",
                                new_infractions = BuildInfractionCodesById(zid),
                            });
                        }

                        if (prev.Tradeable && !cur.Tradeable)
                        {
                            violationsPayload.Add(new
                            {
                                zoneId   = zid,
                                ts       = vts,
                                type     = "lost_tradeable",
                                oldValue = (object)true,
                                newValue = (object)false,
                                delta    = (object)null,
                                reason   = ""
                            });
                        }
                        else if (!prev.Tradeable && cur.Tradeable)
                        {
                            violationsPayload.Add(new
                            {
                                zoneId   = zid,
                                ts       = vts,
                                type     = "gained_tradeable",
                                oldValue = (object)false,
                                newValue = (object)true,
                                delta    = (object)null,
                                reason   = ""
                            });
                        }

                        if (cur.TouchCount > prev.TouchCount)
                        {
                            violationsPayload.Add(new
                            {
                                zoneId   = zid,
                                ts       = vts,
                                type     = "touch",
                                oldValue = (object)prev.TouchCount,
                                newValue = (object)cur.TouchCount,
                                delta    = (object)(cur.TouchCount - prev.TouchCount),
                                reason   = ""
                            });
                            // 2026-05-11: typed hub event with max-penetration
                            // so a bot can decide if the touch is shallow or deep.
                            EmitObserverEvent("zone_touched", new
                            {
                                zone_id     = zid,
                                touch_count = cur.TouchCount,
                                max_pen_pct = LookupMaxPenById(zid),
                            });
                        }

                        if (!prev.IsArmed && cur.IsArmed)
                        {
                            violationsPayload.Add(new
                            {
                                zoneId   = zid,
                                ts       = vts,
                                type     = "armed",
                                oldValue = (object)false,
                                newValue = (object)true,
                                delta    = (object)null,
                                reason   = ""
                            });
                        }
                        else if (prev.IsArmed && !cur.IsArmed)
                        {
                            violationsPayload.Add(new
                            {
                                zoneId   = zid,
                                ts       = vts,
                                type     = "disarmed",
                                oldValue = (object)true,
                                newValue = (object)false,
                                delta    = (object)null,
                                reason   = ""
                            });
                        }
                    }

                    // Zones that were present last time but are absent now:
                    // emit as "invalidated" (filter step removed them, either
                    // because Invalidated=true was set or score fell below
                    // MinDrawScore).
                    foreach (var kv in _previousZoneSnapshots)
                    {
                        if (currentZoneSnapshots.ContainsKey(kv.Key)) continue;
                        violationsPayload.Add(new
                        {
                            zoneId   = kv.Key,
                            ts       = vts,
                            type     = "invalidated",
                            oldValue = (object)kv.Value.Score,
                            newValue = (object)null,
                            delta    = (object)null,
                            reason   = ""
                        });
                        // 2026-05-11: typed hub event. Forget tracking this
                        // zone — next zone_detected will be a fresh emission.
                        _emittedZoneDetectedIds.Remove(kv.Key);
                        EmitObserverEvent("zone_invalidated", new
                        {
                            zone_id            = kv.Key,
                            last_score         = kv.Value.Score,
                            bars_alive_unknown = true,   // bar-count derivation deferred
                        });
                    }
                }
                catch
                {
                    // Defensive: never let the diff break the chart_state emit.
                    violationsPayload = new List<object>();
                }

                EmitToBridge(new
                {
                    type             = "chart_state",
                    ts               = DateTime.UtcNow.ToString("o"),
                    symbolName       = this.Symbol?.Name ?? "",
                    barTimeframeSec  = barTfSec,
                    settings = new
                    {
                        minScore             = this.MinScore,
                        minDrawScore         = this.MinDrawScore,
                        showReferenceLines   = this.ShowReferenceLines,
                        useMTFZones          = this.UseMTFZones,
                        showTrendBreaks      = this.ShowTrendBreaks,
                        enableTrendDetection = this.EnableTrendDetection
                    },
                    zones         = zonesPayload,
                    trendBreaks   = trendBreaksPayload,
                    controlPoints = controlPointsPayload,
                    violations    = violationsPayload
                });

                // Replace previous snapshot with the just-emitted current set
                // so the next emission can diff against it. We only swap AFTER
                // a successful EmitToBridge — if emit throws, _previousZoneSnapshots
                // stays as-is and the next pass diffs against the same prior
                // baseline (acceptable; avoids losing a delta to a transient
                // network blip).
                _previousZoneSnapshots = currentZoneSnapshots;
            }
            catch { /* never throw from the bridge */ }
        }

        // Heartbeat — fires every 5 seconds while indicator is loaded so
        // the dashboard's connection status pip stays accurate.
        private void StartBridgeHeartbeat()
        {
            try
            {
                _bridgeHeartbeatTimer = new System.Threading.Timer(
                    _ =>
                    {
                        try
                        {
                            EmitToBridge(new
                            {
                                type = "heartbeat",
                                ts = DateTime.UtcNow.ToString("o"),
                                symbolsActive = new[] { this.Symbol?.Name ?? "" },
                                indicatorVersion = _indicatorVersion
                            });
                        }
                        catch { /* swallow */ }
                    },
                    state: null,
                    dueTime: 1500,        // first heartbeat after 1.5s
                    period: 5000          // every 5s thereafter
                );
            }
            catch { /* swallow — heartbeat is best-effort */ }
        }

        private void StopBridgeHeartbeat()
        {
            try
            {
                _bridgeHeartbeatTimer?.Dispose();
                _bridgeHeartbeatTimer = null;
            }
            catch { /* swallow */ }
        }

        // ---------------------------------------------------------------------
        // Backfill recent closed bars to the Trading HQ bridge so the
        // dashboard chart pane has real Quantower OHLC immediately, without
        // waiting for the next bar to close. Runs once at OnInit per chart.
        // Iterates oldest→newest (HistoricalData[count]..[1]) so the buffer
        // ends up time-ordered, matching how lightweight-charts wants its
        // setData input. Errors swallowed — backfill is best-effort.
        // ---------------------------------------------------------------------
        private void BackfillBarsToBridge(int maxBars)
        {
            try
            {
                if (this.HistoricalData == null || this.Symbol == null) return;
                int total = this.HistoricalData.Count;
                if (total < 2) return;
                // Bar at offset 0 is the forming bar; offsets 1..N-1 are closed.
                int n = Math.Min(maxBars, total - 1);

                int barTfSec = 0;
                try
                {
                    var period = GetChartPeriod();
                    if (period != null)
                        barTfSec = (int)period.Value.Duration.TotalSeconds;
                }
                catch { /* tick/range aggregations have no duration */ }

                string symbolName = this.Symbol.Name ?? "";

                // Iterate oldest first (offset n down to 1) so receipt order
                // at the backend matches chronological order.
                for (int offset = n; offset >= 1; offset--)
                {
                    var bar = this.HistoricalData[offset] as HistoryItemBar;
                    if (bar == null) continue;
                    EmitToBridge(new
                    {
                        type             = "bar",
                        ts               = bar.TimeLeft.ToString("o"),
                        symbolName       = symbolName,
                        open             = bar.Open,
                        high             = bar.High,
                        low              = bar.Low,
                        close            = bar.Close,
                        volume           = bar.Volume,
                        barTimeframeSec  = barTfSec
                    });
                }
            }
            catch { /* never throw from the bridge */ }
        }

        // ---------------------------------------------------------------------
        // Auto-spawn the Trading HQ FastAPI backend if it isn't running.
        // Called once per AppDomain at indicator load. Quick localhost health
        // probe; on failure, fire-and-forget launch of the hidden VBS wrapper
        // (start_backend_hidden.vbs) which boots uvicorn with no console
        // window. Logs land in C:\TradingHQ\data\logs\backend.log.
        // ---------------------------------------------------------------------
        private static void EnsureBridgeBackendRunning()
        {
            if (Interlocked.Exchange(ref _bridgeSpawnAttempted, 1) != 0) return;

            // Health probe — 500ms is plenty for localhost. If it answers,
            // the backend is already up and we have nothing to do.
            try
            {
                using (var probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) })
                {
                    var resp = probe.GetAsync(BridgeHealthUrl).GetAwaiter().GetResult();
                    if (resp.IsSuccessStatusCode) return;
                }
            }
            catch { /* unreachable → fall through and spawn */ }

            // Spawn the launcher. Errors swallowed — indicator must never
            // be derailed by bridge plumbing.
            try
            {
                if (!System.IO.File.Exists(BridgeLauncherVbs)) return;
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "wscript.exe",
                    Arguments       = "\"" + BridgeLauncherVbs + "\"",
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }

        // ---------------------------------------------------------------------
        // ATR cache + lookup
        // ---------------------------------------------------------------------
        private void UpdateAtrCache()
        {
            int n = this.HistoricalData.Count;
            if (n < 2) return;

            if (this.atrCache == null || this.atrCache.Length != n)
            {
                this.atrCache = new double[n];
                double sum = 0;
                int filled = 0;
                for (int i = n - 1; i >= 0; i--)
                {
                    var b  = this.HistoricalData[i] as HistoryItemBar;
                    if (b == null) { this.atrCache[i] = filled > 0 ? this.atrCache[i + 1] : 0; continue; }
                    double prevClose = (i + 1 < n)
                        ? ((this.HistoricalData[i + 1] as HistoryItemBar)?.Close ?? b.Close)
                        : b.Close;
                    double tr = Math.Max(b.High - b.Low,
                                Math.Max(Math.Abs(b.High - prevClose),
                                         Math.Abs(b.Low  - prevClose)));
                    if (filled < AtrPeriod)
                    {
                        sum += tr;
                        filled++;
                        this.atrCache[i] = sum / filled;
                    }
                    else
                    {
                        // Wilder smoothing.
                        double prevAtr = this.atrCache[i + 1];
                        this.atrCache[i] = (prevAtr * (AtrPeriod - 1) + tr) / AtrPeriod;
                    }
                }
            }
        }

        private double ResolveAtr(int historyOffset)
        {
            if (this.atrCache == null || this.atrCache.Length <= historyOffset) return 0.0;
            return this.atrCache[historyOffset];
        }

        // ---------------------------------------------------------------------
        // 2026-05-12 Phase 1b — Daily ATR cache + percentile rank.
        // ---------------------------------------------------------------------
        // dailyAtrCache mirrors atrCache but runs on the Period.DAY1 feed.
        // Wilder smoothing matches the chart-TF cache. Rebuilds wholesale when
        // dailyHistory.Count changes (a new daily bar printed); otherwise
        // refreshes only index 0 (the still-in-progress day).
        // ---------------------------------------------------------------------
        private void UpdateDailyAtrCache()
        {
            if (this.dailyHistory == null) return;
            int n = this.dailyHistory.Count;
            if (n < 2) { this.dailyAtrCache = null; return; }

            if (this.dailyAtrCache == null || this.dailyAtrCache.Length != n)
            {
                this.dailyAtrCache = new double[n];
                double sum = 0;
                int filled = 0;
                for (int i = n - 1; i >= 0; i--)
                {
                    var b = this.dailyHistory[i] as HistoryItemBar;
                    if (b == null)
                    {
                        this.dailyAtrCache[i] = (i + 1 < n) ? this.dailyAtrCache[i + 1] : 0;
                        continue;
                    }
                    double prevClose = (i + 1 < n)
                        ? ((this.dailyHistory[i + 1] as HistoryItemBar)?.Close ?? b.Close)
                        : b.Close;
                    double tr = Math.Max(b.High - b.Low,
                                Math.Max(Math.Abs(b.High - prevClose),
                                         Math.Abs(b.Low  - prevClose)));
                    if (filled < AtrPeriod)
                    {
                        sum += tr;
                        filled++;
                        this.dailyAtrCache[i] = sum / filled;
                    }
                    else
                    {
                        double prevAtr = this.dailyAtrCache[i + 1];
                        this.dailyAtrCache[i] = (prevAtr * (AtrPeriod - 1) + tr) / AtrPeriod;
                    }
                }
            }
            else
            {
                // Incremental refresh of the in-progress day. Index 0's range
                // mutates as price ticks through the session, so we recompute
                // its ATR from the previous closed daily ATR.
                var b = this.dailyHistory[0] as HistoryItemBar;
                if (b != null && this.dailyAtrCache.Length > 1)
                {
                    double prevClose = (this.dailyHistory[1] as HistoryItemBar)?.Close ?? b.Close;
                    double tr = Math.Max(b.High - b.Low,
                                Math.Max(Math.Abs(b.High - prevClose),
                                         Math.Abs(b.Low  - prevClose)));
                    double prevAtr = this.dailyAtrCache[1];
                    this.dailyAtrCache[0] = (prevAtr * (AtrPeriod - 1) + tr) / AtrPeriod;
                }
            }
        }

        /// <summary>
        /// Rolling percentile rank of the latest CLOSED daily ATR within the
        /// prior AtrPercentileWindow closed daily ATRs. Returns 0.0–1.0 (a
        /// proportion: count strictly below ÷ window). Returns null when the
        /// daily feed hasn't loaded enough history yet (need 1 + window
        /// samples beyond the in-progress day).
        ///
        /// Stable within a session by design — we rank index 1 (yesterday's
        /// fully-closed daily ATR), NOT index 0 (today's in-progress ATR
        /// which grows as the session prints). A trade filled at 09:35 and
        /// another at 15:45 on the same day therefore get the same
        /// regime_vol tag in the hub's variant evaluator.
        ///
        /// Hub-side bucketing (Phase 2): Low <0.25, Mid 0.25–0.75, High >0.75.
        /// </summary>
        private double? ComputeDailyAtrPercentile()
        {
            if (this.dailyAtrCache == null) return null;
            int n = this.dailyAtrCache.Length;
            if (n < 1 + AtrPercentileWindow) return null;

            double current = this.dailyAtrCache[1];
            if (current <= 0 || double.IsNaN(current)) return null;

            int below = 0;
            for (int i = 1; i <= AtrPercentileWindow; i++)
            {
                double a = this.dailyAtrCache[i];
                if (a > 0 && a < current) below++;
            }
            return below / (double)AtrPercentileWindow;
        }

        // ---------------------------------------------------------------------
        // EMA cache (ported from v1)
        // ---------------------------------------------------------------------
        private void UpdateEmaCache()
        {
            int n = this.HistoricalData.Count;
            if (n < 2) return;

            if (this.emaCache == null || this.emaCache.Length != n)
            {
                this.emaCache = new double[n];
                double k = 2.0 / (EmaPeriod + 1);
                double seed = ((HistoryItemBar)this.HistoricalData[0, SeekOriginHistory.Begin]).Close;
                this.emaCache[0] = seed;
                for (int i = 1; i < n; i++)
                {
                    var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (bar == null) { this.emaCache[i] = this.emaCache[i - 1]; continue; }
                    this.emaCache[i] = bar.Close * k + this.emaCache[i - 1] * (1 - k);
                }
            }
            else
            {
                int i = n - 1;
                var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar != null)
                {
                    double k = 2.0 / (EmaPeriod + 1);
                    this.emaCache[i] = bar.Close * k + this.emaCache[i - 1] * (1 - k);
                }
            }
        }

        // 2026-05-12 CVD cache builder per Agent 5 (CVD methodology) synthesis.
        //   _cvdSessionCache[i] = cumulative delta from the bar's RTH session
        //                         start (resets at 09:30 ET). Indexing is
        //                         Begin-relative (oldest = 0, newest = n-1).
        //   _cvdRollingCache[i] = sum of last 20 closed bars' deltas.
        //
        // Real-vs-synthetic feed detection: if every bar's
        // VolumeAnalysisData.Total.Delta is exactly zero across the whole
        // history, the feed is using tick-rule estimation (or has no VA at
        // all). Set _cvdFeedIsReal accordingly so downstream consumers can
        // gate CVD-based filters on data quality.
        private void UpdateCvdCache()
        {
            int n = this.HistoricalData.Count;
            if (n < 2) return;
            // Allocate (or grow) parallel arrays.
            if (this._cvdSessionCache == null || this._cvdSessionCache.Length != n)
            {
                this._cvdSessionCache = new double[n];
                this._cvdRollingCache = new double[n];
            }

            var tz = ResolveEasternTz();
            bool anyNonZero = false;
            double sessionAccum = 0;
            DateTime currentSessionDay = DateTime.MinValue;

            // Walk oldest → newest. Cheap full rebuild on every call; n is
            // bounded by LookbackBars (default 500) so this is fine.
            for (int i = 0; i < n; i++)
            {
                var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null)
                {
                    this._cvdSessionCache[i] = (i > 0) ? this._cvdSessionCache[i - 1] : 0;
                    continue;
                }

                // Session-anchored reset: when a bar's date in ET advances
                // past RTH open, reset the accumulator. ETH bars (before
                // 9:30 ET) accumulate into the same session as the upcoming
                // RTH day; this is intentional per Agent 5 — overnight delta
                // is part of the RTH session context.
                DateTime localEt;
                try { localEt = TimeZoneInfo.ConvertTimeFromUtc(bar.TimeLeft.ToUniversalTime(), tz); }
                catch { localEt = bar.TimeLeft; }
                var sessionDay = localEt.Hour < 9 || (localEt.Hour == 9 && localEt.Minute < 30)
                    ? localEt.Date              // pre-open ETH attributes to today's RTH
                    : localEt.Date;             // RTH and post-RTH attribute to today
                if (sessionDay != currentSessionDay)
                {
                    sessionAccum = 0;
                    currentSessionDay = sessionDay;
                }

                double delta = 0;
                try
                {
                    if (bar.VolumeAnalysisData?.Total != null)
                        delta = bar.VolumeAnalysisData.Total.Delta;
                }
                catch { delta = 0; }

                if (delta != 0) anyNonZero = true;
                sessionAccum += delta;
                this._cvdSessionCache[i] = sessionAccum;
            }
            this._cvdFeedIsReal = anyNonZero;

            // Compute 20-bar rolling sum in a second pass (cheap, O(n)).
            const int rollWindow = 20;
            for (int i = 0; i < n; i++)
            {
                if (i == 0)
                {
                    this._cvdRollingCache[i] = this._cvdSessionCache[i];
                    continue;
                }
                int start = Math.Max(0, i - rollWindow + 1);
                // Use cumulative-delta diff for cheap window sum: rolling
                // window sum = sessionCum[i] - sessionCum[start-1] when
                // both points are in the SAME session; otherwise fall back
                // to a direct walk (handles cross-session boundaries).
                if (start > 0)
                {
                    // Crude correctness check — if session reset between
                    // start-1 and i, just walk the bars. Otherwise diff.
                    this._cvdRollingCache[i] = this._cvdSessionCache[i] - this._cvdSessionCache[start - 1];
                }
                else
                {
                    this._cvdRollingCache[i] = this._cvdSessionCache[i];
                }
            }
        }

        // Resolve CVD value at history offset (1 = last closed bar). 0 if
        // unavailable. Used by market_snapshot + zone events.
        private double ResolveSessionCvd(int begOffset)
        {
            if (this._cvdSessionCache == null) return 0;
            if (begOffset < 0 || begOffset >= this._cvdSessionCache.Length) return 0;
            return this._cvdSessionCache[begOffset];
        }
        private double ResolveRollingCvd(int begOffset)
        {
            if (this._cvdRollingCache == null) return 0;
            if (begOffset < 0 || begOffset >= this._cvdRollingCache.Length) return 0;
            return this._cvdRollingCache[begOffset];
        }

        // 2026-05-12 Agent 5: simple CVD-vs-price divergence flag for the
        // most recent N closed bars. Returns -1 / 0 / +1:
        //   +1 = bullish divergence (price made lower-low but CVD made
        //        higher-low — selling pressure absorbed)
        //   -1 = bearish divergence (price higher-high, CVD lower-high)
        //    0 = no divergence
        // Note: this is the simplified slope-comparison variant. Agent 5's
        // recommended pivot-comparison (lbL=7/lbR=3 + 15% swing filter) is
        // a Tier-3 enhancement; this version covers 80% of the signal at
        // 10% of the implementation cost.
        private int ComputeCvdDivergenceFlag(int lookback = 15)
        {
            int n = this.HistoricalData.Count;
            if (n < lookback + 2) return 0;
            int last = n - 1;       // freshly closed bar's Begin index
            int first = last - lookback;
            var b0 = this.HistoricalData[first, SeekOriginHistory.Begin] as HistoryItemBar;
            var b1 = this.HistoricalData[last,  SeekOriginHistory.Begin] as HistoryItemBar;
            if (b0 == null || b1 == null) return 0;
            double priceDelta = b1.Close - b0.Close;
            double cvdDelta = ResolveRollingCvd(last) - ResolveRollingCvd(first);
            // Only signal when both moves are meaningful (avoid noise on a
            // flat bar). Threshold: price moved > 1 tick.
            double tickSz = 0;
            try { tickSz = this.Symbol?.TickSize ?? 0; } catch { }
            if (tickSz > 0 && Math.Abs(priceDelta) < tickSz) return 0;
            if (cvdDelta == 0) return 0;
            // Bullish div: price down, CVD up.
            if (priceDelta < 0 && cvdDelta > 0) return +1;
            // Bearish div: price up, CVD down.
            if (priceDelta > 0 && cvdDelta < 0) return -1;
            return 0;
        }

        // 2026-05-12 Agent 5: absorption flag (current closed bar). Per the
        // synthesis: rangeStalled (< 0.6 × ATR) AND heavyDelta (|delta| > 1.5
        // × 20-bar avg) AND oppositeSign (delta direction opposite close-vs-
        // open direction). Returns -1/0/+1 with the same convention as
        // divergence: +1 = bullish absorption (sell-side absorbed at
        // support), -1 = bearish absorption (buy-side absorbed at
        // resistance), 0 = none.
        private int ComputeAbsorptionFlag()
        {
            int n = this.HistoricalData.Count;
            if (n < 22) return 0;
            int last = n - 1;
            var bar = this.HistoricalData[last, SeekOriginHistory.Begin] as HistoryItemBar;
            if (bar == null) return 0;
            double barRange = bar.High - bar.Low;
            if (barRange <= 0) return 0;
            double atr = ResolveAtr(0);   // ATR at newest bar
            if (atr <= 0) return 0;
            double delta = 0;
            try { if (bar.VolumeAnalysisData?.Total != null) delta = bar.VolumeAnalysisData.Total.Delta; }
            catch { }
            if (delta == 0) return 0;

            // 20-bar average |delta|.
            double sumAbsDelta = 0;
            int counted = 0;
            for (int j = Math.Max(0, last - 20); j < last; j++)
            {
                var bj = this.HistoricalData[j, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bj?.VolumeAnalysisData?.Total == null) continue;
                sumAbsDelta += Math.Abs(bj.VolumeAnalysisData.Total.Delta);
                counted++;
            }
            double avgAbsDelta = (counted > 0) ? (sumAbsDelta / counted) : 0;
            if (avgAbsDelta <= 0) return 0;

            bool rangeStalled = barRange < 0.6 * atr;
            bool heavyDelta   = Math.Abs(delta) > 1.5 * avgAbsDelta;
            // oppositeSign: negative delta but close didn't fall (no down
            // close) = absorption of sells at support. Mirrored for buys.
            bool oppositeSign =
                (delta < 0 && bar.Close >= bar.Open)
                || (delta > 0 && bar.Close <= bar.Open);
            if (rangeStalled && heavyDelta && oppositeSign)
                return delta < 0 ? +1 : -1;
            return 0;
        }

        // Cache so we only log the resolved value once per session.
        private double _resolvedPointValueCache = 0;
        private bool   _resolvedPointValueLogged = false;

        private double ResolvePointValue()
        {
            if (_resolvedPointValueCache > 0) return _resolvedPointValueCache;

            double pv = 0;
            string source = "unknown";

            try
            {
                if (this.Symbol != null)
                {
                    double tickSize = this.Symbol.TickSize;

                    // Path A: reflection probe for $/tick. Quantower's SDK varies
                    // by version — try common property names, in priority order.
                    // Convert.ToDouble handles decimal/float/int boxing.
                    if (tickSize > 0)
                    {
                        string[] candidates = new[] { "TickCost", "TickValue", "PointValue", "ContractMultiplier", "Multiplier", "LotSize" };
                        foreach (var propName in candidates)
                        {
                            try
                            {
                                var prop = this.Symbol.GetType().GetProperty(propName);
                                if (prop == null) continue;
                                object val = prop.GetValue(this.Symbol);
                                if (val == null) continue;
                                double num = Convert.ToDouble(val);
                                if (num <= 0) continue;
                                // TickCost / TickValue is $/tick → divide by tickSize for $/pt.
                                // PointValue / ContractMultiplier / Multiplier / LotSize are
                                // typically already $/pt — use directly.
                                if (propName == "TickCost" || propName == "TickValue")
                                    pv = num / tickSize;
                                else
                                    pv = num;
                                source = "reflection " + propName + "=" + num.ToString("0.####");
                                if (pv > 0) break;
                            }
                            catch { /* try next candidate */ }
                        }
                    }

                    // Path C: symbol-root lookup table for common futures. Used
                    // as a sanity check AND fallback if both reflection paths
                    // fail or return suspicious values.
                    string root = ExtractSymbolRoot(this.Symbol.Name);
                    double rootPv = LookupKnownPointValue(root);
                    if (pv <= 0 && rootPv > 0)
                    {
                        pv = rootPv;
                        source = "root lookup (" + root + ")";
                    }
                    else if (pv > 0 && rootPv > 0 && Math.Abs(pv - rootPv) / rootPv > 0.20)
                    {
                        // Detected mismatch (>20% off from known table). Trust the
                        // table — protects against ES $50 leaking onto MNQ.
                        try { Core.Instance.Loggers.Log("[IOFv2] PointValue mismatch: SDK=" + pv.ToString("0.##") + " table=" + rootPv.ToString("0.##") + " for " + root + "; using table.", LoggingLevel.System); } catch {}
                        pv = rootPv;
                        source = "root lookup override (" + root + ")";
                    }
                }
            }
            catch { /* swallow */ }

            // No safe default — return 0 so ComputeContracts refuses rather than
            // sizing on a wrong assumption. (Old code defaulted to $50 = ES which
            // silently 25x-overstated risk on micros and blocked all trades.)
            if (pv <= 0)
            {
                if (!_resolvedPointValueLogged)
                {
                    try { Core.Instance.Loggers.Log("[IOFv2] PointValue unresolved for " + (this.Symbol?.Name ?? "?") + " — sizing disabled. Add symbol to LookupKnownPointValue.", LoggingLevel.Error); } catch {}
                    _resolvedPointValueLogged = true;
                }
                return 0;
            }

            _resolvedPointValueCache = pv;
            if (!_resolvedPointValueLogged)
            {
                try { Core.Instance.Loggers.Log("[IOFv2] PointValue resolved: $" + pv.ToString("0.##") + "/pt for " + this.Symbol.Name + " via " + source, LoggingLevel.System); } catch {}
                _resolvedPointValueLogged = true;
            }
            return pv;
        }

        // Strip month/year suffix (e.g. "MNQM6", "MNQ-6", "MNQ.U24") to root.
        private static string ExtractSymbolRoot(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string s = name.ToUpperInvariant();

            // Step 1: trim exchange suffix (".CME", "-EUREX", " GLOBEX", etc.).
            // Anything non-alphanumeric ends the contract spec.
            int specEnd = s.Length;
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsLetterOrDigit(s[i])) { specEnd = i; break; }
            }
            s = s.Substring(0, specEnd);
            // s is now letters-then-digits: "MNQ", "MNQM6", "ESU24", "M2K", etc.

            // Step 2: detect trailing year digits. ONLY strip the month-code
            // letter when there ARE trailing year digits — otherwise the
            // "trailing letter" is part of the root (e.g. plain "MNQ" must NOT
            // become "MN" because "Q" is in the month-code set).
            int digitsStart = s.Length;
            while (digitsStart > 0 && char.IsDigit(s[digitsStart - 1])) digitsStart--;

            if (digitsStart < s.Length)
            {
                // Year present → strip year, then strip month code if applicable.
                s = s.Substring(0, digitsStart);
                if (s.Length > 2 && "FGHJKMNQUVXZ".IndexOf(s[s.Length - 1]) >= 0)
                    s = s.Substring(0, s.Length - 1);
            }
            // ELSE: no year → s is already the root (e.g. continuous "MNQ").

            return s;
        }

        // $/point for common CME/CBOT/COMEX futures. Values are widely published;
        // any mismatch with broker math is a contract-spec issue, not a code bug.
        private static double LookupKnownPointValue(string root)
        {
            switch (root)
            {
                // Equity index — full size
                case "ES":  return 50.0;   // E-mini S&P 500
                case "NQ":  return 20.0;   // E-mini Nasdaq 100
                case "RTY": return 50.0;   // E-mini Russell 2000
                case "YM":  return 5.0;    // E-mini Dow ($5/pt)
                case "NKD": return 5.0;    // Nikkei 225 USD
                case "EMD": return 100.0;  // E-mini S&P MidCap 400
                // Equity index — micros (1/10 of full)
                case "MES": return 5.0;
                case "MNQ": return 2.0;
                case "M2K": return 5.0;
                case "MYM": return 0.5;
                // Energy
                case "CL":  return 1000.0; // Crude oil (1000 bbl × $1)
                case "MCL": return 100.0;  // Micro crude
                case "NG":  return 10000.0;
                case "QM":  return 500.0;
                // Metals
                case "GC":  return 100.0;  // Gold (100oz)
                case "MGC": return 10.0;   // Micro gold
                case "SI":  return 5000.0; // Silver (5000oz)
                case "SIL": return 1000.0; // Mini silver
                case "HG":  return 25000.0;// Copper
                // Treasuries (not commonly traded by user but here for completeness)
                case "ZB":  return 1000.0;
                case "ZN":  return 1000.0;
                case "ZF":  return 1000.0;
                case "ZT":  return 2000.0;
                // FX (CME)
                case "6E":  return 125000.0;
                case "6B":  return 62500.0;
                case "6J":  return 12500000.0;
                case "M6E": return 12500.0;
                default: return 0;
            }
        }

        // Quantower SDK fix: HistoricalData has no .Period; the period lives on
        // the Aggregation object (only on time-based aggregations). Returns null
        // if the chart uses tick / range / non-time aggregation.
        private Period? GetChartPeriod()
        {
            try
            {
                var agg = this.HistoricalData?.Aggregation;
                if (agg is HistoryAggregationTime t) return t.Period;
                return null;
            }
            catch { return null; }
        }

        private string GetChartPeriodString()
        {
            var p = GetChartPeriod();
            return p?.ToString() ?? "";
        }

        private static Period? ChooseHigherTimeframe(Period? chartPeriod)
        {
            if (chartPeriod == null) return Period.HOUR1;
            var p = chartPeriod.Value;
            if (p == Period.MIN1)   return Period.MIN15;
            if (p == Period.MIN5)   return Period.HOUR1;
            if (p == Period.MIN15)  return Period.HOUR4;
            if (p == Period.MIN30)  return Period.HOUR4;
            if (p == Period.HOUR1)  return Period.DAY1;
            if (p == Period.HOUR4)  return Period.DAY1;
            if (p == Period.DAY1)   return Period.WEEK1;
            if (p == Period.WEEK1)  return Period.MONTH1;
            return Period.HOUR1;
        }

        // =====================================================================
        // MULTI-TIMEFRAME ZONE PIPELINE
        //
        // Per-bar rescan of ITF + HTF zones (using cached HistoricalData feeds
        // fetched in OnInit), followed by MTFC overlap detection and a
        // score bonus on chart-TF zones that overlap a higher-tier zone of
        // the same direction.
        // =====================================================================
        private void RescanMTFZones()
        {
            // ITF rescan.
            try
            {
                this.itfZones = TPMTF.MultiTFZoneScanner.ScanTimeframe(
                    this.itfData, TPMTF.ZoneTimeframe.ITF,
                    this.MTFLookbackBars,
                    this.BaseCandleMaxBodyPct,
                    this.MinImpulseRatio,
                    this.MaxBaseCandles);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.RescanMTFZones.ITF");
                this.itfZones = new List<TPMTF.TimeframeZone>();
            }

            // HTF rescan.
            try
            {
                this.htfZones = TPMTF.MultiTFZoneScanner.ScanTimeframe(
                    this.htfData, TPMTF.ZoneTimeframe.HTF,
                    this.MTFLookbackBars,
                    this.BaseCandleMaxBodyPct,
                    this.MinImpulseRatio,
                    this.MaxBaseCandles);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.RescanMTFZones.HTF");
                this.htfZones = new List<TPMTF.TimeframeZone>();
            }
        }

        // IM7: Compute the set of chart-zone IDs that overlap a same-direction
        // higher-tier (HTF preferred, else ITF) zone. Refreshes
        // this.mtfcOverlaps as a side effect (consumed by paint + dashboard).
        // Returns an empty set when MTF is off or chartZones is empty.
        //
        // Renamed from ApplyMTFCBonus and split: the bonus mutation itself
        // now happens INSIDE ScanZones, before the MinScore filter, so a
        // chart-TF zone scoring a hair under MinScore can be lifted across
        // by an MTFC overlap (the original documented design intent).
        private HashSet<string> ComputeMTFCBonusedIds()
        {
            this.mtfcOverlaps.Clear();
            this.itfHtfOverlaps.Clear();
            var bonusedZoneIds = new HashSet<string>();

            if (!this.UseMTFZones) return bonusedZoneIds;
            if (this.chartZones == null || this.chartZones.Count == 0) return bonusedZoneIds;

            // LTF-in-ITF / LTF-in-HTF overlaps — drive the +MTFC bonus on
            // chart-TF zones AND the YELLOW visual highlight.
            var htfOver = TPMTF.MultiTFZoneScanner.FindOverlaps(this.htfZones, this.chartZones);
            var itfOver = TPMTF.MultiTFZoneScanner.FindOverlaps(this.itfZones, this.chartZones);
            this.mtfcOverlaps.AddRange(htfOver);
            this.mtfcOverlaps.AddRange(itfOver);

            // ITF-in-HTF overlaps — purely visual (RED dashed highlight).
            // These are NOT used to bonus the chart-TF zone score; they
            // exist so the trader can see when the bigger-picture tiers
            // themselves agree on a level.
            var itfInHtf = TPMTF.MultiTFZoneScanner.FindOverlaps(this.htfZones, this.itfZones);
            this.itfHtfOverlaps.AddRange(itfInHtf);

            for (int i = 0; i < this.mtfcOverlaps.Count; i++)
            {
                var ov = this.mtfcOverlaps[i];
                if (ov?.Lower == null) continue;
                bonusedZoneIds.Add(ov.Lower.Id);
            }
            return bonusedZoneIds;
        }

        // =====================================================================
        // ALERTS — lifecycle state-transition diff detector.
        //
        // The TradeLifecycleManager doesn't expose events natively. We snapshot
        // every trade's State / HighestTpHit / CurSL into per-trade dicts after
        // each RunPerBar; on the next pass we compare against the snapshot and
        // fire an alert per detected transition. This isolates the master from
        // any future internal changes inside the lifecycle layer.
        // =====================================================================
        private void DetectAndFireLifecycleAlerts()
        {
            // 2026-05-11: this method now ALSO emits virtual lifecycle events
            // to the IOF hub on close transitions, so we no longer early-return
            // when alerts are disabled — hub emissions are critical
            // infrastructure (forward-test journal), not user-facing alerts.
            // Per-call AlertsManager invocations are gated on alertsOn below.
            if (this.lifecycleManager == null) return;
            bool alertsOn = this.alertsConfig != null && this.EnableAlerts;

            string sym = this.Symbol?.Name ?? "";
            var trades = this.lifecycleManager.Trades;
            if (trades == null || trades.Count == 0) return;

            // Track which IDs we saw this pass (for dict pruning).
            var seenIds = new HashSet<int>();

            for (int i = 0; i < trades.Count; i++)
            {
                var t = trades[i];
                if (t == null) continue;
                seenIds.Add(t.Id);

                TPLifecycle.TradeState prevState;
                bool hadPrev = this.lastTickState.TryGetValue(t.Id, out prevState);
                int prevTp = 0;
                this.lastTickHighestTpHit.TryGetValue(t.Id, out prevTp);
                double prevSL = double.NaN;
                this.lastTickCurSL.TryGetValue(t.Id, out prevSL);

                try
                {
                    // ARMED transition (first observation in Armed state).
                    if (t.State == TPLifecycle.TradeState.Armed && (!hadPrev ||
                        prevState != TPLifecycle.TradeState.Armed))
                    {
                        if (alertsOn)
                            TPAlerts.AlertsManager.FireArmed(this.alertsConfig, sym,
                                t.Id, t.Entry, t.IsLong);
                    }

                    // ARMED → ACTIVE = filled.
                    if (t.State == TPLifecycle.TradeState.Active && hadPrev &&
                        prevState != TPLifecycle.TradeState.Active &&
                        prevState != TPLifecycle.TradeState.Win &&
                        prevState != TPLifecycle.TradeState.Loss &&
                        prevState != TPLifecycle.TradeState.BreakEven)
                    {
                        if (alertsOn)
                            TPAlerts.AlertsManager.FireFilled(this.alertsConfig, sym,
                                t.Id, t.FillPrice, t.Contracts);
                        // 2026-05-11: emit virtual_fill to the IOF hub.
                        EmitObserverEvent("virtual_fill", new
                        {
                            virtual_trade_id = t.Id,
                            zone_id          = t.ZoneId,
                            fill_price       = t.FillPrice,
                            fill_bar_ts      = t.FillTime.ToString("o",
                                System.Globalization.CultureInfo.InvariantCulture),
                            contracts        = t.Contracts,
                        });
                    }

                    // TP hit transition — HighestTpHit increased since last tick.
                    if (t.State == TPLifecycle.TradeState.Active &&
                        t.HighestTpHit > prevTp && t.TPs != null)
                    {
                        for (int k = prevTp; k < t.HighestTpHit && k < t.TPs.Length; k++)
                        {
                            double tpPrice = t.TPs[k];
                            if (tpPrice == 0.0) continue;
                            if (alertsOn)
                                TPAlerts.AlertsManager.FireTpHit(this.alertsConfig, sym,
                                    t.Id, k + 1, tpPrice, t.R);
                            // 2026-05-11: emit virtual_tp_hit to the IOF hub.
                            EmitObserverEvent("virtual_tp_hit", new
                            {
                                virtual_trade_id = t.Id,
                                zone_id          = t.ZoneId,
                                tp_num           = k + 1,
                                tp_price         = tpPrice,
                                current_r        = t.R,
                            });
                        }
                    }

                    // Break-even: CurSL crossed entry. Fire once per trade.
                    bool atBE = Math.Abs(t.CurSL - t.Entry) < 1e-9;
                    bool alreadyAlertedBE;
                    this.beAlertedTrades.TryGetValue(t.Id, out alreadyAlertedBE);
                    if (atBE && !alreadyAlertedBE && t.State == TPLifecycle.TradeState.Active)
                    {
                        if (alertsOn)
                            TPAlerts.AlertsManager.FireBreakEven(this.alertsConfig, sym, t.Id);
                        this.beAlertedTrades[t.Id] = true;
                    }

                    // Closed transition — pick the right channel based on
                    // ExitReason (SL hit gets the dedicated FireSLHit; other
                    // closes go through the generic FireClosed).
                    bool nowClosed = t.IsClosed;
                    bool wasClosed = hadPrev && (
                        prevState == TPLifecycle.TradeState.Win ||
                        prevState == TPLifecycle.TradeState.Loss ||
                        prevState == TPLifecycle.TradeState.BreakEven);
                    if (nowClosed && !wasClosed)
                    {
                        if (alertsOn)
                        {
                            // ExitReason.SL = stop hit; everything else (TP*, TRL*,
                            // BE, Force) goes through the generic FireClosed path.
                            if (t.ExitReason == TPLifecycle.ExitReason.SL)
                            {
                                TPAlerts.AlertsManager.FireSLHit(this.alertsConfig, sym,
                                    t.Id, t.CurSL, t.R, t.DollarPnL);
                            }
                            else
                            {
                                TPAlerts.AlertsManager.FireClosed(this.alertsConfig, sym,
                                    t.Id, t.State.ToString(), t.R, t.DollarPnL);
                            }
                        }
                        // 2026-05-11: emit virtual_trade_closed to the IOF hub.
                        // This is the gold for forward-test correlation — every
                        // zone that armed gets a closed row with full MFE/MAE
                        // excursion + outcome. The hub's correlation queries
                        // (/api/iof/correlation/by-score, by-trend, by-mtfc,
                        // by-infraction) build their reports off these rows.
                        EmitObserverEvent("virtual_trade_closed", new
                        {
                            virtual_trade_id     = t.Id,
                            zone_id              = t.ZoneId,
                            outcome              = t.State.ToString().ToUpperInvariant(),
                            exit_reason          = TPLifecycle.ExitReasonLabel.Short(t.ExitReason),
                            final_r              = t.R,
                            final_dollar_at_100usd_risk = t.DollarPnL,
                            highest_tp_hit       = t.HighestTpHit,
                            highest_unrealized_r = t.MfeR,
                            max_drawdown_r       = t.MaeR,
                            duration_bars        = t.BarsSinceFill,
                            close_price          = t.CurSL,  // closed at trailed SL by convention
                        });
                    }
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.DetectAndFireLifecycleAlerts");
                }

                // Update snapshot.
                this.lastTickState[t.Id] = t.State;
                this.lastTickHighestTpHit[t.Id] = t.HighestTpHit;
                this.lastTickCurSL[t.Id] = t.CurSL;
            }

            // Prune any stale IDs (trade was dropped from Trades list, e.g. by
            // the FIFO close cap). Cheap; runs once per bar.
            PruneAlertSnapshots(seenIds);
        }

        private void PruneAlertSnapshots(HashSet<int> seenIds)
        {
            if (this.lastTickState.Count <= seenIds.Count) return;

            var stale = new List<int>();
            foreach (var kv in this.lastTickState)
            {
                if (!seenIds.Contains(kv.Key)) stale.Add(kv.Key);
            }
            foreach (var id in stale)
            {
                this.lastTickState.Remove(id);
                this.lastTickHighestTpHit.Remove(id);
                this.lastTickCurSL.Remove(id);
                this.beAlertedTrades.Remove(id);
            }
        }

        // =====================================================================
        // ZONE DETECTION + 20-POINT SCORING
        //
        // CR1: chart-TF zone detection is delegated to MultiTFZoneScanner —
        // this fixes the supply-zone wick-mapping bug at the source (the
        // scanner's BuildZoneRect populates WickHi as the highest base wick
        // for supply zones, NOT the highest body). We then translate each
        // TimeframeZone into an IofZone to preserve the existing 20-pt
        // scoring + dedupe + draw paths.
        //
        // CR2: zone invalidation is also handled by ScanTimeframe (sets
        // Active = false after price closes through the far wick); we
        // propagate that into IofZone.Invalidated below.
        // =====================================================================
        private void ScanZones()
        {
            this.zones.Clear();

            // LTF feed selection — execution layer:
            //   * If LTFOverrideEnabled and ltfHistory loaded → use override feed
            //   * Otherwise fall back to the chart's HistoricalData (legacy)
            HistoricalData ltfFeed = (this.LTFOverrideEnabled && this.ltfHistory != null && this.ltfHistory.Count > 0)
                ? this.ltfHistory
                : this.HistoricalData;

            this.chartZones = TPMTF.MultiTFZoneScanner.ScanTimeframe(
                ltfFeed,
                TPMTF.ZoneTimeframe.LTF,
                this.LookbackBars,
                this.BaseCandleMaxBodyPct,
                this.MinImpulseRatio,
                this.MaxBaseCandles);

            // IM7: compute MTFC bonus eligibility BEFORE the MinScore filter
            // so a chart-TF zone whose raw score lands a hair under MinScore
            // can still be admitted once the higher-tier overlap bonus is
            // folded in. ITF/HTF zones never receive the bonus themselves —
            // only the chart-TF (Lower) side of each overlap pair does.
            var bonusedZoneIds = ComputeMTFCBonusedIds();

            // Translate each TimeframeZone to IofZone, score, dedupe.
            for (int i = 0; i < this.chartZones.Count; i++)
            {
                var z = this.chartZones[i];
                if (z == null) continue;

                ZoneType type = ParseFormation(z.FormationCode);
                bool isDemand = z.IsLong;
                if (isDemand && !ShowDemandZones) continue;
                if (!isDemand && !ShowSupplyZones) continue;

                var io = new IofZone
                {
                    Id              = z.Id,
                    Type            = type,
                    StartIndex      = z.BaseStartIndex,
                    EndIndex        = z.BaseEndIndex,
                    StartTime       = z.BaseStartTime,
                    EndTime         = z.BaseEndTime,
                    Top             = isDemand ? z.BodyHi : z.WickHi,
                    Bottom          = isDemand ? z.WickLo : z.BodyLo,
                    BodyHi          = z.BodyHi,
                    BodyLo          = z.BodyLo,
                    WickHi          = z.WickHi,
                    WickLo          = z.WickLo,
                    BaseCandleCount = (z.BaseEndIndex - z.BaseStartIndex + 1),
                    Invalidated     = !z.Active
                };

                double baseHeight = io.Top - io.Bottom;
                if (baseHeight <= 0) continue;

                double moveOut = MeasureMoveOut(io.EndIndex, this.HistoricalData.Count, isDemand);
                if (moveOut < MinImpulseRatio * baseHeight) continue;

                io.MoveOut    = moveOut;
                io.BaseHeight = baseHeight;

                ScoreZone(io);

                // IM7: fold MTFC bonus in BEFORE MinScore so a 13.x raw
                // score lifted to 14.x by the bonus is admitted. Bonus only
                // applies to chart-TF zones whose box overlaps a same-
                // direction higher-tier (HTF/ITF) zone — see
                // ComputeMTFCBonusedIds. MTFCBonusScore is a double; full
                // precision is preserved now that IofZone.Score is double.
                if (bonusedZoneIds.Contains(io.Id))
                {
                    io.MtfcBonus = this.MTFCBonusScore;
                    io.Score    += this.MTFCBonusScore;
                }
                else
                {
                    io.MtfcBonus = 0;
                }

                // Stash score back onto the TimeframeZone for downstream MTF
                // helpers / paint that work off chartZones.
                z.Score = io.Score;

                // Visibility filter (was MinScore — caused "zone disappears
                // on touch" surprise when Purity degraded a borderline zone).
                // Now: keep zones >= MinDrawScore visible; lifecycle's
                // BuildZoneInfoSnapshot filters to >= MinScore for trade
                // eligibility separately.
                if (io.Score < MinDrawScore) continue;
                if (TryDeduplicate(io)) continue;

                this.zones.Add(io);

                // 2026-05-11: emit zone_detected on first sight of this zone
                // id. Same dedup set persists across scans so we never emit
                // duplicates for the same zone (its score/touches/state
                // changes flow via zone_score_changed / zone_touched / etc).
                if (!string.IsNullOrEmpty(io.Id) && _emittedZoneDetectedIds.Add(io.Id))
                {
                    bool isDemandEvt = (io.Type == ZoneType.RBR || io.Type == ZoneType.DBR);
                    string mtfcTagEvt = io.MtfcBonus > 0 ? $" +M{io.MtfcBonus:0.#}" : "";
                    string infractionLabel = BuildInfractionLabel(io, mtfcTagEvt);
                    // Extract just the infraction codes (everything after the
                    // "{score}/21 " prefix and before the contracts suffix).
                    // Keep it simple — the hub stores the full label as-is.
                    EmitObserverEvent("zone_detected", new
                    {
                        zone_id    = io.Id,
                        tier       = "LTF",
                        formation  = io.Type.ToString(),
                        is_demand  = isDemandEvt,
                        geometry   = new
                        {
                            body_hi = io.BodyHi,
                            body_lo = io.BodyLo,
                            wick_hi = io.WickHi,
                            wick_lo = io.WickLo,
                        },
                        score              = io.Score,
                        score_breakdown    = new
                        {
                            range    = io.RangeScore,
                            time     = io.TimeScore,
                            purity   = io.PurityScore,
                            strength = io.StrengthScore,
                            trend    = io.TrendScore,
                            rrr      = io.RrrScore,
                            juice    = io.JuiceScore,
                        },
                        infractions             = BuildInfractionCodes(io),
                        trend_at_detection      = io.ItfTrend ?? "",
                        mtfc                    = new
                        {
                            bonus = io.MtfcBonus,
                        },
                        displayed_rrr_at_OFEntry = io.EstimatedRrr,
                        estimated_target         = io.EstimatedTargetPrice,
                        bars_in_base             = io.BaseCandleCount,
                        base_height              = io.BaseHeight,
                        move_out                 = io.MoveOut,
                        is_globex_trap           = io.IsGlobexTrap,
                    });
                }
            }

            // 2026-05-11: Globex Trap flagging per the Globex Traps 101 PDF.
            // Compute current ETH (Globex) window high/low, then for each
            // active zone in this.zones decide:
            //   supply (RBD/DBD) zone with BodyLo > GlobexHigh → bull trap
            //   demand (RBR/DBR) zone with BodyHi < GlobexLow → bear trap
            // The "trap" is the qualified IBI zone JUST beyond the Globex
            // range that catches breakout chasers. The flag is purely
            // structural — no ATR or volatility involved.
            try
            {
                var nowUtcGt = (this.HistoricalData != null && this.HistoricalData.Count >= 2)
                    ? ((this.HistoricalData[1] as HistoryItemBar)?.TimeLeft ?? DateTime.UtcNow)
                    : DateTime.UtcNow;
                var tzGt = ResolveEasternTz();
                var nowLocalGt = TimeZoneInfo.ConvertTimeFromUtc(nowUtcGt.ToUniversalTime(), tzGt);
                ComputeGlobexHighLow(nowLocalGt, out double gHi, out double gLo,
                                     out DateTime _, out DateTime _);
                if (!double.IsNaN(gHi) && !double.IsNaN(gLo))
                {
                    for (int gz = 0; gz < this.zones.Count; gz++)
                    {
                        var zz = this.zones[gz];
                        if (zz == null) continue;
                        bool isDemandZ = (zz.Type == ZoneType.RBR || zz.Type == ZoneType.DBR);
                        if (isDemandZ)
                        {
                            // Bear trap candidate: demand zone wholly below GL.
                            zz.IsGlobexTrap = zz.BodyHi < gLo;
                        }
                        else
                        {
                            // Bull trap candidate: supply zone wholly above GH.
                            zz.IsGlobexTrap = zz.BodyLo > gHi;
                        }
                    }
                }
            }
            catch { /* never block scan on Globex compute */ }

            // 2026-05-10 BUGFIX: mtfcOverlaps was computed against the FULL
            // chartZones list (pre-MinDrawScore-filter) so the yellow MTFC
            // highlight could draw boxes for chart-TF zones that were never
            // actually drawn (score below MinDrawScore, or deduped). That
            // looked like "ghost" yellow boxes with no visible LTF zone to
            // anchor them. Prune the overlap list now that this.zones is
            // finalized — only keep overlaps whose Lower zone made it to
            // the drawn set.
            if (this.mtfcOverlaps != null && this.mtfcOverlaps.Count > 0)
            {
                var visibleLtfIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < this.zones.Count; i++)
                {
                    var dz = this.zones[i];
                    if (dz != null && !dz.Invalidated && !string.IsNullOrEmpty(dz.Id))
                        visibleLtfIds.Add(dz.Id);
                }
                this.mtfcOverlaps.RemoveAll(ov =>
                    ov?.Lower == null
                    || string.IsNullOrEmpty(ov.Lower.Id)
                    || !visibleLtfIds.Contains(ov.Lower.Id));
            }

            // Fire NEW-zone alerts for any zone IDs we haven't seen before.
            FireZoneDetectedAlerts();
        }

        // Translate "RBR" / "DBR" / "RBD" / "DBD" → ZoneType. Falls back to RBR.
        private static ZoneType ParseFormation(string code)
        {
            if (code == "RBR") return ZoneType.RBR;
            if (code == "DBR") return ZoneType.DBR;
            if (code == "RBD") return ZoneType.RBD;
            if (code == "DBD") return ZoneType.DBD;
            return ZoneType.RBR;
        }

        // Fire one zone-detected alert per never-before-seen zone ID. Bounded
        // by alertedZoneIds so the same zone isn't re-fired on every rescan.
        private void FireZoneDetectedAlerts()
        {
            if (this.alertsConfig == null || !this.EnableAlerts) return;
            string sym = this.Symbol?.Name ?? "";
            double curPrice = double.NaN;
            var bar = this.HistoricalData?[0] as HistoryItemBar;
            if (bar != null) curPrice = bar.Close;

            for (int i = 0; i < this.zones.Count; i++)
            {
                var z = this.zones[i];
                if (z == null) continue;
                if (this.alertedZoneIds.Contains(z.Id)) continue;
                this.alertedZoneIds.Add(z.Id);

                try
                {
                    TPAlerts.AlertsManager.FireZoneDetected(
                        this.alertsConfig, sym, z.Type.ToString(),
                        z.Score, curPrice, z.Top, z.Bottom);
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.FireZoneDetectedAlerts");
                }
            }
        }

        // CR1 cleanup: IsValidBase / ClassifyLeg / BuildZoneBox removed —
        // their work is now done by MultiTFZoneScanner.ScanTimeframe (which
        // also fixes the supply-zone wick bug at the source). MeasureMoveOut
        // is still used by ScanZones (above) for the per-IofZone strength
        // score, so it survives.

        private double MeasureMoveOut(int endOfBase, int total, bool isDemand)
        {
            int scanLimit = Math.Min(total - 1, endOfBase + Math.Max(20, LookbackBars / 4));
            double extreme = double.NaN;
            var baseBar = this.HistoricalData[endOfBase, SeekOriginHistory.Begin] as HistoryItemBar;
            if (baseBar == null) return 0.0;

            for (int i = endOfBase + 1; i <= scanLimit; i++)
            {
                var b = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (b == null) break;

                if (isDemand)
                {
                    if (double.IsNaN(extreme) || b.High > extreme) extreme = b.High;
                    if (b.Close < baseBar.Close - (baseBar.High - baseBar.Low)) break;
                }
                else
                {
                    if (double.IsNaN(extreme) || b.Low < extreme) extreme = b.Low;
                    if (b.Close > baseBar.Close + (baseBar.High - baseBar.Low)) break;
                }
            }
            if (double.IsNaN(extreme)) return 0.0;
            return isDemand ? (extreme - baseBar.High) : (baseBar.Low - extreme);
        }

        private bool TryDeduplicate(IofZone candidate)
        {
            for (int i = 0; i < this.zones.Count; i++)
            {
                var existing = this.zones[i];
                if (existing.Type != candidate.Type) continue;
                if (Math.Abs(existing.StartIndex - candidate.StartIndex) > 2) continue;
                bool overlap = !(candidate.Top < existing.Bottom || candidate.Bottom > existing.Top);
                if (!overlap) continue;
                if (candidate.Score > existing.Score) this.zones[i] = candidate;
                return true;
            }
            return false;
        }

        // ---- 20-point rubric (ported verbatim from v1) ---------------------
        private void ScoreZone(IofZone z)
        {
            z.RangeScore    = ScoreRange(z);
            z.TimeScore     = ScoreTime(z);
            z.PurityScore   = ScorePurity(z);
            z.StrengthScore = ScoreStrength(z);
            z.TrendScore    = ScoreTrend(z);
            z.RrrScore      = ScoreRrr(z);
            z.JuiceScore    = ScoreJuice(z);
            z.Score = z.RangeScore + z.TimeScore + z.PurityScore +
                      z.StrengthScore + z.TrendScore + z.RrrScore + z.JuiceScore;
        }

        private int ScoreRange(IofZone z)
        {
            if (this.htfHistory == null || this.htfHistory.Count < 5) return 1;
            int htfIdx = (int)this.htfHistory.GetIndexByTime(z.StartTime.Ticks);
            if (htfIdx < 0) return 1;

            int back = Math.Min(8, htfIdx);
            double hi = double.MinValue, lo = double.MaxValue;
            for (int i = htfIdx - back; i <= htfIdx; i++)
            {
                if (i < 0 || i >= this.htfHistory.Count) continue;
                var bar = this.htfHistory[i] as HistoryItemBar;
                if (bar == null) continue;
                if (bar.High > hi) hi = bar.High;
                if (bar.Low  < lo) lo = bar.Low;
            }
            if (hi <= lo) return 1;

            double range = hi - lo;
            double zoneMid = (z.Top + z.Bottom) / 2.0;
            double pos = (zoneMid - lo) / range;
            bool isDemand = z.Type == ZoneType.RBR || z.Type == ZoneType.DBR;
            if (isDemand && pos <= 0.25) return 2;
            if (!isDemand && pos >= 0.75) return 2;
            if (pos > 0.25 && pos < 0.75) return 1;
            return 0;
        }

        private int ScoreTime(IofZone z)
        {
            if (z.BaseCandleCount <= 3) return 2;
            if (z.BaseCandleCount <= 6) return 1;
            return 0;
        }

        private int ScorePurity(IofZone z)
        {
            int touches = 0;
            double maxPenetrationPct = 0;
            int scanStart = z.EndIndex + 2;
            for (int i = scanStart; i < this.HistoricalData.Count; i++)
            {
                var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null) continue;
                bool isDemand = z.Type == ZoneType.RBR || z.Type == ZoneType.DBR;
                bool entered = isDemand
                    ? (bar.Low  <= z.Top    && bar.High >= z.Bottom)
                    : (bar.High >= z.Bottom && bar.Low  <= z.Top);
                if (!entered) continue;
                touches++;
                double zoneHeight = z.Top - z.Bottom;
                if (zoneHeight <= 0) continue;
                double penetration = isDemand
                    ? (z.Top - Math.Max(bar.Low, z.Bottom)) / zoneHeight
                    : (Math.Min(bar.High, z.Top) - z.Bottom) / zoneHeight;
                if (penetration > maxPenetrationPct) maxPenetrationPct = penetration;
            }
            z.TouchCount = touches;
            z.MaxPenetrationPct = maxPenetrationPct;

            if (touches == 0) return 4;
            if (touches == 1 && maxPenetrationPct < 0.5) return 2;
            return 0;
        }

        private int ScoreStrength(IofZone z)
        {
            if (z.BaseHeight <= 0) return 0;
            double ratio = z.MoveOut / z.BaseHeight;
            bool brokeOpposing = ImpulseBrokeOpposingSwing(z);
            if (ratio >= MinImpulseRatio && brokeOpposing) return 4;
            if (ratio >= MinImpulseRatio) return 3;
            if (ratio >= 1.5) return 2;
            return 0;
        }

        private bool ImpulseBrokeOpposingSwing(IofZone z)
        {
            bool isDemand = z.Type == ZoneType.RBR || z.Type == ZoneType.DBR;
            int lookback = Math.Min(40, z.StartIndex);
            double opposingHigh = double.MinValue, opposingLow = double.MaxValue;
            for (int i = z.StartIndex - lookback; i < z.StartIndex; i++)
            {
                if (i < 0) continue;
                var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null) continue;
                if (bar.High > opposingHigh) opposingHigh = bar.High;
                if (bar.Low  < opposingLow)  opposingLow  = bar.Low;
            }
            int scanLimit = Math.Min(this.HistoricalData.Count - 1, z.EndIndex + 30);
            for (int i = z.EndIndex + 1; i <= scanLimit; i++)
            {
                var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null) continue;
                if (isDemand && bar.Close > opposingHigh) return true;
                if (!isDemand && bar.Close < opposingLow) return true;
            }
            return false;
        }

        // 2026-05-10: ScoreTrend uses a two-layer reading.
        //   1. The live TrendStateMachine is the strict doctrine reading
        //      (3-segment HH-HL-HH with strictly monotonic highs AND lows).
        //      When it confirms Bull/Bear, that's authoritative → score 4 if
        //      aligned with zone, 0 if counter.
        //   2. When the state machine is FLAT (or disabled), fall through to
        //      the fractal-vote fallback. The fallback uses 1-bar fractals
        //      and majority voting — much more permissive, surfaces a trend
        //      reading on clearly trending charts that the doctrine hasn't
        //      yet structurally confirmed. Soft-aligned scores 2 (vs strict
        //      4), so the rubric still rewards full doctrinal confirmation
        //      without leaving the user staring at FLAT on a screaming
        //      uptrend.
        // The label printed on the zone (z.ItfTrend) reflects whichever path
        // produced the verdict — the user reads the trend score (4 vs 2) to
        // know if it was strict or soft.
        private int ScoreTrend(IofZone z)
        {
            bool isDemand = z.Type == ZoneType.RBR || z.Type == ZoneType.DBR;
            bool liveActive = this.EnableTrendDetection && this.trendStateMachine != null;

            // Layer 1: live state machine — authoritative when it confirms.
            if (liveActive)
            {
                var snap = this.trendStateMachine.GetSnapshot();
                if (snap.State == TradePhantomsIOF.Trend.TrendState.Bull)
                {
                    z.ItfTrend = "UP";
                    return isDemand ? 4 : 0;
                }
                if (snap.State == TradePhantomsIOF.Trend.TrendState.Bear)
                {
                    z.ItfTrend = "DOWN";
                    return !isDemand ? 4 : 0;
                }
                // Flat → fall through to fractal vote below.
            }

            // Layer 2: fractal-vote fallback (1-bar fractals, majority voting).
            //
            // 2026-05-10: per TP doctrine "ITF determines trend state." When
            // TrendFromITF is on and ITF data is loaded, the fractal vote
            // MUST be computed on ITF bars — not chart-TF — otherwise the
            // soft reading would let chart-TF pivots override the ITF
            // verdict. Map the zone's start TIME (not chart-TF index) into
            // the ITF series so the vote window is doctrinally correct.
            HistoricalData trendBars;
            int zoneStartIdxInTrend;
            bool usingItfBars = false;
            if (this.TrendFromITF && this.itfTrendHistory != null && this.itfTrendHistory.Count >= 10)
            {
                trendBars = this.itfTrendHistory;
                usingItfBars = true;
                long ticks = z.StartTime.Ticks;
                // 2026-05-10 INDEXING BUG: GetIndexByTime returns an
                // END-relative offset (newest = 0), per the Quantower SDK
                // example pattern (TestIndicatorWithOneMoreHistoricalData
                // uses [offset] default indexer, which is End-relative).
                // The loop below uses SeekOriginHistory.Begin (oldest = 0),
                // so we MUST convert here or we'd scan ancient bars from
                // 30 days ago instead of recent ones. Convert by:
                //     beginIdx = Count - 1 - endRelativeIdx
                int endRelIdx = (int)trendBars.GetIndexByTime(ticks);
                int beginIdx;
                if (endRelIdx < 0)
                {
                    // Time not found in ITF series — anchor to newest.
                    beginIdx = trendBars.Count - 1;
                }
                else
                {
                    beginIdx = trendBars.Count - 1 - endRelIdx;
                    if (beginIdx < 0) beginIdx = 0;
                    if (beginIdx >= trendBars.Count) beginIdx = trendBars.Count - 1;
                }
                zoneStartIdxInTrend = beginIdx;
            }
            else
            {
                // ITF not loaded yet (or TrendFromITF=false) — last-resort
                // chart-TF vote. Keeps the score from collapsing to FLAT
                // during the brief async-load window. z.StartIndex is
                // already Begin-relative on HistoricalData, so no conversion.
                trendBars = this.HistoricalData;
                zoneStartIdxInTrend = z.StartIndex;
            }

            int window = 4 * Math.Max(20, MaxBaseCandles * 4);
            int start = Math.Max(0, zoneStartIdxInTrend - window);
            int scanEnd = Math.Min(trendBars.Count - 1, zoneStartIdxInTrend) - 1;
            var pivots = new List<(int idx, double price, bool isHigh)>();
            for (int i = start + 2; i < scanEnd; i++)
            {
                var prev = trendBars[i - 1, SeekOriginHistory.Begin] as HistoryItemBar;
                var cur  = trendBars[i,     SeekOriginHistory.Begin] as HistoryItemBar;
                var next = trendBars[i + 1, SeekOriginHistory.Begin] as HistoryItemBar;
                if (prev == null || cur == null || next == null) continue;
                if (cur.High > prev.High && cur.High > next.High) pivots.Add((i, cur.High, true));
                if (cur.Low  < prev.Low  && cur.Low  < next.Low)  pivots.Add((i, cur.Low,  false));
            }
            if (pivots.Count < 2)
            {
                z.ItfTrend = "FLAT";
                return 1;
            }
            // Tag z.ItfTrend prefix when the soft reading is ITF-derived vs
            // chart-TF — purely diagnostic via the upcoming label append.
            // (No-op here; trendLabel below already gets stamped.)
            _ = usingItfBars;

            int bullSignal = 0, bearSignal = 0;
            var highs = pivots.Where(p => p.isHigh).ToList();
            var lows  = pivots.Where(p => !p.isHigh).ToList();
            for (int i = 1; i < highs.Count; i++)
            {
                if (highs[i].price > highs[i - 1].price) bullSignal++;
                else if (highs[i].price < highs[i - 1].price) bearSignal++;
            }
            for (int i = 1; i < lows.Count; i++)
            {
                if (lows[i].price > lows[i - 1].price) bullSignal++;
                else if (lows[i].price < lows[i - 1].price) bearSignal++;
            }
            string trendLabel;
            if (bullSignal > bearSignal + 1) trendLabel = "UP";
            else if (bearSignal > bullSignal + 1) trendLabel = "DOWN";
            else trendLabel = "FLAT";
            z.ItfTrend = trendLabel;

            if (trendLabel == "FLAT") return 1;
            // Soft (state machine fell through) → 2; legacy (live disabled) → 4.
            int alignedScore = liveActive ? 2 : 4;
            if (isDemand  && trendLabel == "UP")   return alignedScore;
            if (!isDemand && trendLabel == "DOWN") return alignedScore;
            return 0;
        }

        // ScoreRrr is split into two computations:
        //
        //   1. The SCORE BAND (0/2/3/4) is invariant to OFEntry. It uses the
        //      WORST-case (front-of-zone) entry vs far-wick SL, so the same
        //      zone always grades the same regardless of entry-depth setting.
        //      Doctrine: "even if you took the front, is there enough room
        //      for a real target?"
        //
        //   2. The DISPLAYED R:R (z.EstimatedRrr) reflects the user's
        //      configured OFEntry depth — that's the R:R the published
        //      intent / size label / dashboard show, because the user wants
        //      to see the R:R for the trade they'd actually take.
        //
        // Keeping these decoupled means: changing OFEntry rotates the
        // displayed R:R (and the trade plan), but never moves the zone's
        // score band — so journaling and zone-to-zone comparison stay stable.
        private int ScoreRrr(IofZone z)
        {
            bool isDemand = z.Type == ZoneType.RBR || z.Type == ZoneType.DBR;
            double tick = this.Symbol?.TickSize ?? 0.01;

            double targetPrice = EstimateTargetPrice(z, isDemand);
            if (double.IsNaN(targetPrice))
            {
                z.EstimatedRrr = 0;
                z.EstimatedTargetPrice = double.NaN;
                return 0;
            }
            z.EstimatedTargetPrice = targetPrice;

            // (1) SCORE BAND — front-of-zone entry, fixed reference.
            double scoreEntry    = isDemand ? z.Top : z.Bottom;
            double scoreStopWick = isDemand ? z.Bottom : z.Top;
            double scoreRisk     = Math.Abs(scoreEntry - scoreStopWick) + StopBufferTicks * tick;
            double scoreRatio    = 0;
            if (scoreRisk > 0)
                scoreRatio = Math.Abs(targetPrice - scoreEntry) / scoreRisk;

            // (2) DISPLAYED R:R — uses configured OFEntry depth so it matches
            // the trade plan the user sees on the label and the intent.
            var ofLevel = (TradePhantomsIOF.OFEntryLevel)this.OFEntry;
            double tradeEntry = TradePhantomsIOF.EntryTPMath.ComputeEntry(
                ofLevel, isDemand, z.BodyHi, z.BodyLo, z.WickHi, z.WickLo);
            double tradeSl = TradePhantomsIOF.EntryTPMath.ComputeOrigSL(
                isDemand, z.WickHi, z.WickLo, tick, this.StopBufferTicks);
            double tradeRisk = TradePhantomsIOF.EntryTPMath.ComputeSlDistance(tradeEntry, tradeSl);
            z.EstimatedRrr = tradeRisk > 0
                ? Math.Abs(targetPrice - tradeEntry) / tradeRisk
                : 0;

            // Score band uses the FIXED reference, not the displayed R:R.
            if (scoreRatio >= 5) return 4;
            if (scoreRatio >= 3) return 3;
            if (scoreRatio >= 2) return 2;
            return 0;
        }

        private double EstimateTargetPrice(IofZone z, bool isDemand)
        {
            // 2026-05-11: dispatch on RrrTarget mode. FixedRR is purely a
            // function of zone risk; Structural prefers next opposing zone
            // (HTF -> ITF -> chart-TF), then falls back to VA HVN, then to
            // the 60-bar pre-zone extreme.
            if (this.RrrTarget == RrrTargetMode.FixedRR)
            {
                double tick = this.Symbol?.TickSize ?? 0.01;
                double scoreEntry = isDemand ? z.Top : z.Bottom;
                double scoreStop  = isDemand ? z.Bottom : z.Top;
                double scoreRisk  = Math.Abs(scoreEntry - scoreStop) + StopBufferTicks * tick;
                if (scoreRisk <= 0) return double.NaN;
                double offset = Math.Max(0.5, this.RrrFixedMultiple) * scoreRisk;
                return isDemand ? scoreEntry + offset : scoreEntry - offset;
            }

            // Structural mode (default) — next opposing zone, tier-priority.
            double structuralTarget = FindNextOpposingZoneTarget(z, isDemand);
            if (!double.IsNaN(structuralTarget)) return structuralTarget;

            // Fallback 1: VA HVN past the zone edge.
            if (this.volumeAnalysisLoaded && this.HistoricalData != null)
            {
                try
                {
                    int scanStart = Math.Max(0, z.EndIndex - 30);
                    int scanEnd   = Math.Min(this.HistoricalData.Count - 1, z.EndIndex + 60);
                    var levels = new Dictionary<double, double>();
                    for (int i = scanStart; i <= scanEnd; i++)
                    {
                        var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                        if (bar?.VolumeAnalysisData?.PriceLevels == null) continue;
                        foreach (var kv in bar.VolumeAnalysisData.PriceLevels)
                        {
                            if (!levels.ContainsKey(kv.Key)) levels[kv.Key] = 0;
                            levels[kv.Key] += kv.Value.Volume;
                        }
                    }
                    if (levels.Count >= 5)
                    {
                        double edge = isDemand ? z.Top : z.Bottom;
                        var candidates = levels
                            .Where(kv => isDemand ? kv.Key > edge : kv.Key < edge)
                            .OrderByDescending(kv => kv.Value)
                            .Take(3).ToList();
                        if (candidates.Count > 0) return candidates[0].Key;
                    }
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.EstimateTargetPrice.VA");
                }
            }

            // Fallback 2: 60-bar pre-zone extreme.
            int lookback = Math.Min(60, z.StartIndex);
            double extreme = isDemand ? double.MinValue : double.MaxValue;
            for (int i = z.StartIndex - lookback; i < z.StartIndex; i++)
            {
                if (i < 0) continue;
                var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null) continue;
                if (isDemand  && bar.High > extreme) extreme = bar.High;
                if (!isDemand && bar.Low  < extreme) extreme = bar.Low;
            }
            if (extreme == double.MinValue || extreme == double.MaxValue) return double.NaN;
            return extreme;
        }

        // 2026-05-11: structural R:R target = nearest active opposing-direction
        // HTF zone past the entry edge. HTF ONLY — ITF and chart-TF zones
        // intentionally NOT considered, per user doctrine: only HTF opposing
        // zones are "structural" in this sense. Returns NaN when no HTF
        // opposing zone exists; EstimateTargetPrice then falls through to its
        // VA HVN / 60-bar extreme fallbacks for grading continuity, rather
        // than producing a 0 R:R score (which would conflate "bad R:R" with
        // "no HTF target").
        private double FindNextOpposingZoneTarget(IofZone z, bool isDemand)
        {
            return FindNearestOpposing(this.htfZones, z, isDemand);
        }

        // For a LONG (demand) zone: opposing = supply zone above. Use its
        // BodyLo (the bottom edge of the supply body — frontside facing the
        // approach from below).
        // For a SHORT (supply) zone: opposing = demand zone below. Use its
        // BodyHi (the top edge of the demand body — frontside facing the
        // approach from above).
        // Returns NaN when no qualifying opposing zone exists in the list.
        private static double FindNearestOpposing(
            List<TPMTF.TimeframeZone> zones,
            IofZone z,
            bool isDemand)
        {
            if (zones == null || zones.Count == 0) return double.NaN;
            double bestTarget   = double.NaN;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < zones.Count; i++)
            {
                var oz = zones[i];
                if (oz == null || !oz.Active) continue;
                if (oz.IsLong == isDemand) continue;   // same-direction, not a target

                double candidate;
                double distance;
                if (isDemand)
                {
                    candidate = oz.BodyLo;            // supply frontside (lower edge of body)
                    distance  = candidate - z.Top;
                }
                else
                {
                    candidate = oz.BodyHi;            // demand frontside (upper edge of body)
                    distance  = z.Bottom - candidate;
                }
                if (distance <= 0) continue;          // not past our entry edge
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestTarget   = candidate;
                }
            }
            return bestTarget;
        }

        private int ScoreJuice(IofZone z)
        {
            if (z.TouchCount == 0) return 1;
            if (this.emaCache != null && z.EndIndex < this.emaCache.Length)
            {
                double ema = this.emaCache[z.EndIndex];
                if (ema >= z.Bottom && ema <= z.Top) return 1;
            }
            if (this.htfHistory != null && this.htfHistory.Count > 0)
            {
                int htfIdx = (int)this.htfHistory.GetIndexByTime(z.StartTime.Ticks);
                if (htfIdx >= 0 && htfIdx < this.htfHistory.Count)
                {
                    var hb = this.htfHistory[htfIdx] as HistoryItemBar;
                    if (hb != null)
                    {
                        double bodyHi = Math.Max(hb.Open, hb.Close);
                        double bodyLo = Math.Min(hb.Open, hb.Close);
                        bool overlap = !(bodyLo > z.Top || bodyHi < z.Bottom);
                        if (overlap) return 1;
                    }
                }
            }
            return 0;
        }

        // =====================================================================
        // PAINT — zones, lines, panels.
        // =====================================================================
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);
            if (this.CurrentChart == null || this.HistoricalData == null) return;

            var mainWindow = this.CurrentChart.MainWindow;
            Graphics gr = args.Graphics;
            var prevClip = gr.ClipBounds;
            gr.SetClip(mainWindow.ClientRectangle);

            try
            {
                gr.SmoothingMode = SmoothingMode.AntiAlias;

                int fontPt = MapFontPt(this.TextSize);
                // 2026-05-10: zone labels use a smaller, non-bold font so they
                // sit lightly on the chart instead of dominating it. Trade
                // lines (entry/SL/TP) keep the bold labelFont because those
                // are operationally important and benefit from emphasis.
                // Zone label font is ~2pt smaller than the user's TextSize
                // (clamped to a minimum of 8pt) and uses Regular weight.
                int zoneFontPt = Math.Max(8, fontPt - 2);
                using (var labelFont     = new Font("Segoe UI", fontPt,    FontStyle.Bold))
                using (var zoneLabelFont = new Font("Segoe UI", zoneFontPt, FontStyle.Regular))
                using (var labelBg       = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                using (var labelFg       = new SolidBrush(Color.White))
                {
                    DrawZones(gr, mainWindow, zoneLabelFont, labelBg, labelFg);

                    // MTF zones (if enabled) — drawn beneath the chart-TF
                    // zones so the LTF labels stay on top and readable.
                    // MTF labels also use the lighter zone font for consistency.
                    if (this.UseMTFZones)
                    {
                        DrawMTFZones(gr, mainWindow, zoneLabelFont, labelBg, labelFg);
                        DrawMTFCOverlapHighlights(gr, mainWindow);
                    }

                    // 2026-05-06: control-point and trend-break markers,
                    // drawn under the trade-line layer so entry/SL/TP labels
                    // remain readable on top.
                    DrawTrendOverlays(gr, mainWindow);

                    // CR4: lock around iteration of the lifecycle's Trades list
                    // so OnNewLast can't tear it down mid-render.
                    // Trade lines KEEP the bold labelFont — these are the
                    // operationally important entry/SL/TP labels for active
                    // trades and should stay visually emphatic.
                    lock (this.tradesLock)
                    {
                        DrawTradeLines(gr, mainWindow, labelFont, labelFg);
                    }
                }

                // Stats strip — built from the current ACTIVE trade + lifecycle stats.
                if (this.ShowStrip)
                {
                    TPUI.StripData strip;
                    lock (this.tradesLock)
                    {
                        strip = BuildStripData();
                    }
                    TPUI.StatsStripRenderer.DrawStrip(
                        gr, mainWindow.ClientRectangle, strip, this.StripPos, this.TextSize);
                }

                // Closed-trades table — fed in newest-first order.
                if (this.ShowClosedTradesTable && this.UseLifecycle && this.lifecycleManager != null)
                {
                    var rows = new List<TPUI.ClosedTradeRow>();
                    lock (this.tradesLock)
                    {
                        foreach (var t in this.lifecycleManager.GetClosed(this.MaxClosedVisible))
                            rows.Add(ToClosedRow(t));
                    }
                    TPUI.DashboardRenderer.DrawClosedTradesTable(
                        gr, mainWindow.ClientRectangle, rows,
                        this.MaxClosedVisible, this.ClosedTablePos, this.TextSize);
                }

                // Status dashboard.
                if (this.ShowStatusDashboard)
                {
                    TPUI.DashboardSettings settings;
                    TPUI.DashboardZones zonesB;
                    List<TPUI.ActiveTradeRow> actives;
                    TPUI.DashboardStats stats;
                    TPUI.DashboardTrend dashTrend;            // 2026-05-06: optional TREND section
                    lock (this.tradesLock)
                    {
                        settings  = BuildDashboardSettings();
                        zonesB    = BuildDashboardZones();
                        actives   = BuildActiveTradeRows();
                        stats     = BuildDashboardStats();
                        dashTrend = BuildDashboardTrend();    // null when feature is off
                    }
                    TPUI.DashboardRenderer.DrawStatusDashboard(
                        gr, mainWindow.ClientRectangle,
                        settings, zonesB, actives, stats,
                        this.DashboardPos, this.TextSize,
                        dashTrend);
                }

                // Next-trades preview panel (2 demand + 2 supply closest to
                // price, filtered by structural 3R+). Standalone panel — sits
                // wherever the user puts it; defaults to BottomLeft (opposite
                // the status dashboard's default TopRight).
                if (this.ShowNextTradesPanel)
                {
                    var lastBarN = this.HistoricalData?[1] as HistoryItemBar;
                    double curPriceN = lastBarN?.Close ?? double.NaN;
                    if (!double.IsNaN(curPriceN))
                    {
                        List<TPUI.NextTradeRow> nextRows;
                        lock (this.tradesLock)
                        {
                            nextRows = BuildNextTradeRows(curPriceN);
                        }
                        TPUI.DashboardRenderer.DrawNextTradesPanel(
                            gr, mainWindow.ClientRectangle,
                            nextRows, curPriceN,
                            this.NextTradesPanelPos, this.TextSize);
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log(ex, "TradePhantoms_IOF_v2.OnPaintChart");
            }
            finally
            {
                gr.SetClip(prevClip);
            }
        }

        private static int MapFontPt(int textSize)
        {
            switch (textSize)
            {
                case 1: return 8;
                case 2: return 10;
                case 3: return 12;
                case 4: return 14;
                case 5: return 16;
                default: return 12;
            }
        }

        // ── 2026-05-10: zone label with INFRACTION CODES ──
        // Format: "{score}/21 {direction} {infractions...}{mtfcTag}"
        // No infraction emits a code — a 21/21 zone reads "21/21 UP".
        // Code reference:
        //   R1 = mid-range  (lost 1) | R2 = wrong-end (lost 2)
        //   B{n} = n-bar base, only when n >= 4 (no code for 1-3b)
        //   P{n} = touched n times. Append "!" when MaxPenetrationPct >= 0.5
        //          (deep penetration — that's the "lost 4 pts" tier).
        //   S! = impulse didn't break opposing 40-bar swing (lost 1 pt)
        //   $<2 / $2 / $3 = R:R below 2 / 2-3 / 3-5 (>= 5 emits no code)
        //   J! = touched but no EMA / HTF-body confluence (lost 1 pt)
        //   UP / DOWN = trend reading; "°" appended = soft (state machine
        //          flat, fractal vote aligned with zone direction).
        //   FLAT = trend genuinely flat.
        //   mtfcTag = "+M1" appended when the chart-TF zone overlaps a
        //          same-direction ITF or HTF zone (existing bonus).
        // Type (RBR/DBR/RBD/DBD) intentionally NOT shown — green box = demand,
        // red box = supply, so the type letter prefix is redundant.
        // 2026-05-11: lookup helpers used by the typed-event emission path
        // inside the chart_state diff loop. Both are O(N) over this.zones
        // which is bounded by LookbackBars dedup; cheap enough to call on
        // every violation emission.
        private List<string> BuildInfractionCodesById(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return new List<string>();
            for (int i = 0; i < this.zones.Count; i++)
            {
                if (this.zones[i] != null && this.zones[i].Id == zoneId)
                    return BuildInfractionCodes(this.zones[i]);
            }
            return new List<string>();
        }

        private double LookupMaxPenById(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return 0.0;
            for (int i = 0; i < this.zones.Count; i++)
            {
                if (this.zones[i] != null && this.zones[i].Id == zoneId)
                    return this.zones[i].MaxPenetrationPct;
            }
            return 0.0;
        }

        // 2026-05-11: structured infractions list for the hub. Returns the
        // same set of codes BuildInfractionLabel emits, but as discrete
        // strings rather than a single space-joined label. Keeps observer
        // ingestion machine-parseable on the hub side.
        private static List<string> BuildInfractionCodes(IofZone z)
        {
            var codes = new List<string>(8);
            if (z.RangeScore == 0)         codes.Add("R2");
            else if (z.RangeScore == 1)    codes.Add("R1");
            if (z.BaseCandleCount >= 4)    codes.Add("B" + z.BaseCandleCount);
            if (z.TouchCount >= 1)
                codes.Add("P" + z.TouchCount + (z.MaxPenetrationPct >= 0.5 ? "!" : ""));
            if (z.StrengthScore == 3)      codes.Add("S!");
            if (z.RrrScore == 0)
                codes.Add(double.IsNaN(z.EstimatedTargetPrice) ? "$?" : "$<2");
            else if (z.RrrScore == 2)      codes.Add("$2");
            else if (z.RrrScore == 3)      codes.Add("$3");
            if (z.JuiceScore == 0)         codes.Add("J!");
            return codes;
        }

        // Cheap pass-through used at the zone_detected emission site. The
        // master already has a built infraction label string in scope; we
        // walk the IofZone directly so we don't have to re-tokenize the
        // formatted label.
        private static List<string> ParseInfractionsFromLabel(string label)
        {
            // Stub: this helper exists so the zone_detected emission can
            // pass a list rather than the full label. Callers route through
            // BuildInfractionCodes(zone) instead — the label arg is unused.
            return new List<string>();
        }

        private static string BuildInfractionLabel(IofZone z, string mtfcTag)
        {
            var sb = new System.Text.StringBuilder(48);
            sb.Append(z.Score.ToString("0.#"));
            sb.Append("/21 ");

            // Direction (always shown). "°" = soft (TrendScore == 2 means
            // state-machine was flat but fractal fallback voted aligned).
            string dir = z.ItfTrend ?? "?";
            if (z.TrendScore == 2) dir += "°";
            sb.Append(dir);

            // Range deficit.
            if (z.RangeScore == 0)      sb.Append(" R2");
            else if (z.RangeScore == 1) sb.Append(" R1");

            // Base length (no code for 1-3 base candles — those are the
            // ScoreTime=2 tier, no infraction).
            if (z.BaseCandleCount >= 4)
                sb.Append(" B").Append(z.BaseCandleCount);

            // Purity touches. Append "!" for deep penetration tier.
            if (z.TouchCount >= 1)
            {
                sb.Append(" P").Append(z.TouchCount);
                if (z.MaxPenetrationPct >= 0.5) sb.Append('!');
            }

            // Strength — only one reachable deficit (didn't break opposing
            // swing). The "ratio < 2" branch of ScoreStrength is dead code
            // because ScanZones gates ratio >= MinImpulseRatio (2.0).
            if (z.StrengthScore == 3) sb.Append(" S!");

            // R:R tier (any non-perfect emits a code).
            // 2026-05-11: distinguish "no target found" (NaN
            // EstimatedTargetPrice) from "R:R below 2" — both score 0
            // structurally, but they mean different things. "$?" signals
            // "no readable structural target," typically when htfZones is
            // empty AND VA / extreme fallbacks also failed.
            if (z.RrrScore == 0)
                sb.Append(double.IsNaN(z.EstimatedTargetPrice) ? " $?" : " $<2");
            else if (z.RrrScore == 2) sb.Append(" $2");
            else if (z.RrrScore == 3) sb.Append(" $3");

            // Juice — only one tier (got the point or didn't).
            if (z.JuiceScore == 0) sb.Append(" J!");

            // MTFC bonus tag (already formatted by caller).
            sb.Append(mtfcTag);

            return sb.ToString();
        }

        // ── Draw zones (newest-first, capped at MaxClosedVisible analog) ──
        private void DrawZones(Graphics gr, object mainWindowObj, Font labelFont,
                               Brush labelBg, Brush labelFg)
        {
            var win = this.CurrentChart.MainWindow;
            int rightPx = win.ClientRectangle.Right;

            // 2026-05-13: pre-arm preview — visually flag the closest tradeable
            // demand and the closest tradeable supply (the zones the lifecycle's
            // PASS C will arm next per the OnlyArmClosestPerDirection rule).
            // Mirrors PASS C's selection but skips the proximity gate so the
            // operator can see "what's on deck" even when price hasn't
            // retraced yet.
            string preArmDemandId = null, preArmSupplyId = null;
            double preArmDemandDist = double.MaxValue, preArmSupplyDist = double.MaxValue;
            double curPrice = 0;
            if (this.HistoricalData != null && this.HistoricalData.Count > 1)
            {
                var lastBar = this.HistoricalData[1] as HistoryItemBar;
                if (lastBar != null) curPrice = lastBar.Close;
            }
            if (curPrice > 0)
            {
                foreach (var zz in this.zones)
                {
                    if (zz.Invalidated) continue;
                    if (zz.Score < MinScore) continue;
                    bool zzIsDemand = (zz.Type == ZoneType.RBR || zz.Type == ZoneType.DBR);
                    double zEntry = TradePhantomsIOF.EntryTPMath.ComputeEntry(
                        (TradePhantomsIOF.OFEntryLevel)this.OFEntry,
                        zzIsDemand, zz.BodyHi, zz.BodyLo, zz.WickHi, zz.WickLo);
                    // Price-side guard: long candidates must sit BELOW current
                    // price (price retraces DOWN into demand); short candidates
                    // must sit ABOVE (price retraces UP into supply). Same
                    // guard PASS C uses.
                    bool priceOk = zzIsDemand ? (curPrice > zEntry) : (curPrice < zEntry);
                    if (!priceOk) continue;
                    double d = Math.Abs(curPrice - zEntry);
                    if (zzIsDemand)
                    {
                        if (d < preArmDemandDist) { preArmDemandDist = d; preArmDemandId = zz.Id; }
                    }
                    else
                    {
                        if (d < preArmSupplyDist) { preArmSupplyDist = d; preArmSupplyId = zz.Id; }
                    }
                }
            }

            // Newest-first iteration. We don't separately cap zones (the
            // detection layer is already bounded by LookbackBars + dedupe).
            for (int zi = this.zones.Count - 1; zi >= 0; zi--)
            {
                var z = this.zones[zi];

                // FIX (2026-05-06): skip zones that have been broken by a candle
                // close through the far wick. The MTF drawer at DrawMTFZones
                // already filters by !z.Active; chart-TF DrawZones forgot to
                // filter by z.Invalidated, leaving broken zones drawn forever.
                // Per TP / PDF doctrine: a body close past the far wick
                // invalidates the zone. (Sister filter ShowBrokenZones could
                // be added later to expose them dimmed for chart-history audit.)
                if (z.Invalidated) continue;

                bool isDemand = z.Type == ZoneType.RBR || z.Type == ZoneType.DBR;
                Color baseColor = isDemand ? DemandColor : SupplyColor;
                if (z.MtfcBonus > 0)
                    baseColor = Color.FromArgb(baseColor.A, 148, 0, 211); // purple for MTFC-overlapping zones

                // Tradeable: score >= MinScore. Below = informational only
                // (touched/degraded but not invalidated — visible so the
                // trader can see what got touched, but visually dimmed and
                // does NOT trigger ARM/fill in the lifecycle).
                bool tradeable = z.Score >= MinScore;

                // 2026-05-11 BUGFIX: previously the alpha was hardcoded
                // (60..200 for tradeable, 30 for non-tradeable) regardless of
                // the user's DemandColor/SupplyColor alpha channel — so
                // changing the color's transparency in settings did nothing.
                // Now the user's alpha is the FULLY-OPAQUE ceiling; score
                // modulates it DOWNWARD. Set baseColor.A = 255 for opaque
                // top-score zones, lower it for more see-through zones.
                double scoreNorm = Math.Min(1.0, Math.Max(0.0,
                    (z.Score - MinScore) / (double)Math.Max(1, 21 - MinScore)));
                int userAlpha = baseColor.A;
                if (userAlpha <= 0) userAlpha = 1;             // never fully invisible
                int alpha = tradeable
                    ? (int)Math.Round(userAlpha * (0.30 + 0.70 * scoreNorm))   // 30%..100% of user alpha
                    : (int)Math.Round(userAlpha * 0.15);                       // 15% for informational
                if (alpha < 1) alpha = 1;
                if (alpha > 255) alpha = 255;
                Color fill = tradeable
                    ? Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B)
                    : Color.FromArgb(alpha, 140, 140, 140);     // gray for non-tradeable

                int xLeft   = (int)Math.Round(win.CoordinatesConverter.GetChartX(z.StartTime));
                int yTop    = (int)Math.Round(win.CoordinatesConverter.GetChartY(z.Top));
                int yBottom = (int)Math.Round(win.CoordinatesConverter.GetChartY(z.Bottom));
                if (yTop > yBottom) { var t = yTop; yTop = yBottom; yBottom = t; }
                int width  = rightPx - xLeft;
                int height = Math.Max(1, yBottom - yTop);
                if (width <= 0) continue;

                // 2026-05-13: pre-arm flag for visual emphasis below.
                bool isPreArm = (z.Id == preArmDemandId || z.Id == preArmSupplyId);

                using (var brush = new SolidBrush(fill))
                using (var pen = new Pen(Color.FromArgb(220, baseColor.R, baseColor.G, baseColor.B), 1))
                {
                    gr.FillRectangle(brush, xLeft, yTop, width, height);
                    gr.DrawRectangle(pen,   xLeft, yTop, width, height);
                }

                if (isPreArm)
                {
                    // Bright accent border + corner triangle for the pre-arm
                    // candidate. Picks up the eye instantly even on a chart
                    // crowded with overlapping zones.
                    Color accent = Color.FromArgb(230, 100, 200, 255); // bright cyan
                    using (var preArmPen = new Pen(accent, 2.5f))
                    {
                        gr.DrawRectangle(preArmPen, xLeft, yTop, width, height);
                    }
                    // Small "▶" triangle marker at the zone's right edge to
                    // strongly distinguish from the rest of the candidates.
                    using (var triBrush = new SolidBrush(accent))
                    {
                        int tx = rightPx - 12;
                        int ty = (yTop + yBottom) / 2;
                        var pts = new System.Drawing.Point[]
                        {
                            new System.Drawing.Point(tx,     ty - 6),
                            new System.Drawing.Point(tx + 10, ty),
                            new System.Drawing.Point(tx,     ty + 6),
                        };
                        gr.FillPolygon(triBrush, pts);
                    }
                }

                if (this.ShowZoneScoreLabels)
                {
                    // 2026-05-10: replaced verbose "RBR 14/21 [1b/UP]" prefix
                    // with an INFRACTION-CODE label. RBR/DBR/RBD/RBD is
                    // redundant with the zone's color (green/red). Base count
                    // and other deficits are now encoded only when they cost
                    // points — see BuildInfractionLabel below for the scheme.
                    string mtfcTag = z.MtfcBonus > 0 ? $" +M{z.MtfcBonus:0.#}" : "";
                    string label = BuildInfractionLabel(z, mtfcTag);
                    // 2026-05-13: pre-arm tag — clearest text confirmation
                    // that this is the zone PASS C will arm next when price
                    // retraces (per OnlyArmClosestPerDirection rule).
                    if (isPreArm) label = "▶ PRE-ARM " + label;

                    // 2026-05-09: append per-zone position sizing to every
                    // tradeable zone's label using the shared ComputeSizing
                    // helper (same math TryPublishIntent uses for armed zones).
                    // Format: " · {contracts}c · ${dollarRisk:0}".
                    // Replaces the prior verbose ComputeZoneSizeLabel suffix
                    // (which added "| Nc @ $X | 1:R RR") so labels stay compact
                    // alongside the dashboard's matching format.
                    if (tradeable && this.Symbol != null)
                    {
                        try
                        {
                            double tickSizeLbl = 0;
                            try { tickSizeLbl = this.Symbol.TickSize; } catch { }
                            double pointValueLbl = 0;
                            try { pointValueLbl = ResolvePointValue(); } catch { }
                            var sizing = ComputeSizing(z, tickSizeLbl, pointValueLbl);
                            label += $" · {sizing.Contracts}c · ${sizing.DollarRisk:0}";

                            // 2026-05-11: append the OFEntry-based R:R the
                            // trader would actually realize at their entry
                            // depth — distinct from the front-of-zone R:R
                            // the score band uses. Format: " · 1:5.2"
                            // Skipped when NaN target or zero risk.
                            if (!double.IsNaN(z.EstimatedRrr) && z.EstimatedRrr > 0)
                                label += $" · 1:{z.EstimatedRrr:0.#}";
                        }
                        catch { /* defensive — never block drawing */ }
                    }

                    // 2026-05-12: dropped label background (user pref).
                    // Text now sits directly on the zone fill.
                    int labelX = xLeft + 4;
                    int labelY = yTop  + 2;
                    gr.DrawString(label, labelFont, labelFg, labelX, labelY);
                }

                // 2026-05-07: Draw faint reference entry/SL/TP lines on every
                // tradeable zone (so the user can see the plan in advance,
                // before price ARMs into proximity). Lifecycle's bold lines
                // (DrawTradeLines) overlay these when the zone actually arms.
                if (tradeable && this.ShowReferenceLines && this.Symbol != null)
                {
                    try
                    {
                        DrawReferenceLines(gr, win, z, isDemand, xLeft, rightPx);
                    }
                    catch { /* never block draw */ }
                }
            }
        }

        /// <summary>
        /// Compute a human-readable contracts + risk + RR label for a tradeable
        /// zone, based on user's DollarRiskPerTrade and the zone's SL distance.
        /// Result format: " | 3 cons @ $100 | 1:5 RR" (returned with leading
        /// separator so it cleanly appends to the score label).
        /// </summary>
        private string ComputeZoneSizeLabel(IofZone z, bool isDemand)
        {
            double tickSize = this.Symbol.TickSize;
            double pointVal = ResolvePointValue();
            if (tickSize <= 0 || pointVal <= 0) return "";

            var ofLevel = (TradePhantomsIOF.OFEntryLevel)this.OFEntry;
            double entry = TradePhantomsIOF.EntryTPMath.ComputeEntry(
                ofLevel, isDemand, z.BodyHi, z.BodyLo, z.WickHi, z.WickLo);
            double sl = TradePhantomsIOF.EntryTPMath.ComputeOrigSL(
                isDemand, z.WickHi, z.WickLo, tickSize, this.StopBufferTicks);
            double slDist = TradePhantomsIOF.EntryTPMath.ComputeSlDistance(entry, sl);
            if (slDist <= 0) return "";

            int contracts = TradePhantomsIOF.EntryTPMath.ComputeContracts(
                this.DollarRiskPerTrade, slDist, pointVal, this.MaxContracts);

            // Compute RR to TP3 for quick read.
            double zoneHeight = Math.Abs(z.Top - z.Bottom);
            double tp3Distance = (this.TpCount >= 3 ? 3 : this.TpCount) * this.TpStep * zoneHeight;
            double rrTo3 = (slDist > 0 && tp3Distance > 0) ? (tp3Distance / slDist) : 0;

            if (contracts <= 0)
            {
                return $" | TOO WIDE: ${this.DollarRiskPerTrade:N0}<{slDist*pointVal:N0}";
            }

            return $" | {contracts}c @ ${this.DollarRiskPerTrade:N0} | 1:{rrTo3:0.#}";
        }

        /// <summary>
        /// Draw faint horizontal preview lines for entry, SL, TP1, TP2, TP3
        /// inside the zone's drawing region (right edge to left visible edge).
        /// Uses dashed pen + low alpha so they don't compete with active-trade
        /// lifecycle lines.
        /// </summary>
        private void DrawReferenceLines(Graphics gr, dynamic win, IofZone z, bool isDemand, int xLeft, int rightPx)
        {
            double tickSize = this.Symbol.TickSize;
            if (tickSize <= 0) return;

            var ofLevel = (TradePhantomsIOF.OFEntryLevel)this.OFEntry;
            double entry = TradePhantomsIOF.EntryTPMath.ComputeEntry(
                ofLevel, isDemand, z.BodyHi, z.BodyLo, z.WickHi, z.WickLo);
            double sl = TradePhantomsIOF.EntryTPMath.ComputeOrigSL(
                isDemand, z.WickHi, z.WickLo, tickSize, this.StopBufferTicks);
            double zoneHeight = Math.Abs(z.Top - z.Bottom);
            var tps = TradePhantomsIOF.EntryTPMath.ComputeTPs(
                isDemand, entry, zoneHeight, this.TpCount, this.TpStep);

            // Faint dashed lines from the zone's start to the right edge.
            using (var entryPenPreview = new Pen(Color.FromArgb(120, 0, 200, 0), 1f) { DashStyle = DashStyle.Dash })
            using (var slPenPreview    = new Pen(Color.FromArgb(120, 200, 0, 0), 1f) { DashStyle = DashStyle.Dash })
            using (var tpPenPreview    = new Pen(Color.FromArgb(120, 0, 180, 200), 1f) { DashStyle = DashStyle.Dot })
            {
                int yEntry = (int)Math.Round(win.CoordinatesConverter.GetChartY(entry));
                int ySL    = (int)Math.Round(win.CoordinatesConverter.GetChartY(sl));
                gr.DrawLine(entryPenPreview, xLeft, yEntry, rightPx, yEntry);
                gr.DrawLine(slPenPreview,    xLeft, ySL,    rightPx, ySL);

                if (tps != null)
                {
                    for (int k = 0; k < tps.Length; k++)
                    {
                        if (tps[k] == 0.0) continue;
                        int yTp = (int)Math.Round(win.CoordinatesConverter.GetChartY(tps[k]));
                        gr.DrawLine(tpPenPreview, xLeft, yTp, rightPx, yTp);
                    }
                }
            }
        }

        // ── Draw MTF (ITF + HTF) zones beneath the chart-TF layer.
        // ITF gets a medium-intensity tint; HTF gets a thicker outline + a
        // brighter fill so they stand out as "the bigger context."
        private void DrawMTFZones(Graphics gr, object mainWindowObj, Font labelFont,
                                  Brush labelBg, Brush labelFg)
        {
            var win = this.CurrentChart.MainWindow;
            int rightPx = win.ClientRectangle.Right;

            // Render ITF first (darker / smaller layer), then HTF on top.
            DrawMTFLayer(gr, win, rightPx, this.itfZones, labelFont, labelBg, labelFg,
                isHtf: false);
            DrawMTFLayer(gr, win, rightPx, this.htfZones, labelFont, labelBg, labelFg,
                isHtf: true);
        }

        private void DrawMTFLayer(Graphics gr, object winObj, int rightPx,
                                  List<TPMTF.TimeframeZone> layer,
                                  Font labelFont, Brush labelBg, Brush labelFg,
                                  bool isHtf)
        {
            if (layer == null || layer.Count == 0) return;
            var win = this.CurrentChart.MainWindow;

            for (int i = 0; i < layer.Count; i++)
            {
                var z = layer[i];
                if (z == null || !z.Active) continue;

                bool isDemand = z.IsLong;
                Color baseColor = isDemand ? this.DemandColor : this.SupplyColor;

                // 2026-05-11 BUGFIX: MTF layer alpha also respects the user's
                // color alpha now. HTF gets a higher fraction (50% of user
                // alpha) than ITF (30%) so HTF still reads as "the bigger
                // context" relative to ITF. Outline alpha follows the same
                // proportional logic.
                int userAlpha = baseColor.A;
                if (userAlpha <= 0) userAlpha = 1;
                int alpha = isHtf
                    ? (int)Math.Round(userAlpha * 0.50)
                    : (int)Math.Round(userAlpha * 0.30);
                if (alpha < 1) alpha = 1;
                if (alpha > 255) alpha = 255;
                Color fill = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
                float outlineWidth = isHtf ? 2.5f : 1.0f;
                int outlineAlpha = Math.Min(255, isHtf
                    ? (int)Math.Round(userAlpha * 1.10) // ~110% so outline reads against fill
                    : (int)Math.Round(userAlpha * 0.85));
                if (outlineAlpha < 60) outlineAlpha = 60;  // floor — keep outline visible

                double top    = isDemand ? z.BodyHi : z.WickHi;
                double bottom = isDemand ? z.WickLo : z.BodyLo;

                int xLeft   = (int)Math.Round(win.CoordinatesConverter.GetChartX(z.BaseStartTime));
                int yTop    = (int)Math.Round(win.CoordinatesConverter.GetChartY(top));
                int yBottom = (int)Math.Round(win.CoordinatesConverter.GetChartY(bottom));
                if (yTop > yBottom) { var t = yTop; yTop = yBottom; yBottom = t; }
                int width  = rightPx - xLeft;
                int height = Math.Max(1, yBottom - yTop);
                if (width <= 0) continue;

                using (var brush = new SolidBrush(fill))
                using (var pen = new Pen(Color.FromArgb(outlineAlpha, baseColor.R, baseColor.G, baseColor.B), outlineWidth))
                {
                    gr.FillRectangle(brush, xLeft, yTop, width, height);
                    gr.DrawRectangle(pen,   xLeft, yTop, width, height);
                }

                if (this.ShowZoneScoreLabels)
                {
                    string tierTag = isHtf ? "HTF" : "ITF";
                    string label = $"{tierTag} {z.FormationCode}";
                    var sz = gr.MeasureString(label, labelFont);
                    int labelX = xLeft + 4;
                    int labelY = yBottom - (int)sz.Height - 2;
                    // 2026-05-12: dropped label background (user pref).
                    gr.DrawString(label, labelFont, labelFg, labelX, labelY);
                }
            }
        }

        // ── MTFC overlap highlight: yellow border around the intersection
        // box of any chart-TF zone that overlaps a higher-tier zone.
        private void DrawMTFCOverlapHighlights(Graphics gr, object mainWindowObj)
        {
            bool hasLtfOverlaps = this.mtfcOverlaps != null  && this.mtfcOverlaps.Count > 0;
            bool hasItfOverlaps = this.itfHtfOverlaps != null && this.itfHtfOverlaps.Count > 0;
            if (!hasLtfOverlaps && !hasItfOverlaps) return;

            var win = this.CurrentChart.MainWindow;
            int rightPx = win.ClientRectangle.Right;

            // YELLOW dashed: LTF-in-ITF / LTF-in-HTF.
            if (hasLtfOverlaps)
            {
                using (var pen = new Pen(this.MTFCOverlapColor, 2.5f) { DashStyle = DashStyle.Dash })
                {
                    for (int i = 0; i < this.mtfcOverlaps.Count; i++)
                    {
                        var ov = this.mtfcOverlaps[i];
                        if (ov?.Lower == null) continue;

                        int xLeft   = (int)Math.Round(win.CoordinatesConverter.GetChartX(ov.Lower.BaseStartTime));
                        int yTop    = (int)Math.Round(win.CoordinatesConverter.GetChartY(ov.OverlapTop));
                        int yBottom = (int)Math.Round(win.CoordinatesConverter.GetChartY(ov.OverlapBottom));
                        if (yTop > yBottom) { var t = yTop; yTop = yBottom; yBottom = t; }
                        int width  = rightPx - xLeft;
                        int height = Math.Max(1, yBottom - yTop);
                        if (width <= 0) continue;

                        gr.DrawRectangle(pen, xLeft, yTop, width, height);
                    }
                }
            }

            // RED dashed: ITF-in-HTF (bigger-picture tier confluence).
            // Anchored to the ITF zone's start time (ov.Lower for this list
            // is the ITF zone, since we called FindOverlaps(htf, itf)).
            if (hasItfOverlaps)
            {
                using (var pen = new Pen(this.MTFCOverlapItfHtfColor, 2.5f) { DashStyle = DashStyle.Dot })
                {
                    for (int i = 0; i < this.itfHtfOverlaps.Count; i++)
                    {
                        var ov = this.itfHtfOverlaps[i];
                        if (ov?.Lower == null) continue;

                        int xLeft   = (int)Math.Round(win.CoordinatesConverter.GetChartX(ov.Lower.BaseStartTime));
                        int yTop    = (int)Math.Round(win.CoordinatesConverter.GetChartY(ov.OverlapTop));
                        int yBottom = (int)Math.Round(win.CoordinatesConverter.GetChartY(ov.OverlapBottom));
                        if (yTop > yBottom) { var t = yTop; yTop = yBottom; yBottom = t; }
                        int width  = rightPx - xLeft;
                        int height = Math.Max(1, yBottom - yTop);
                        if (width <= 0) continue;

                        gr.DrawRectangle(pen, xLeft, yTop, width, height);
                    }
                }
            }
        }

        // ── 2026-05-06: control-point + trend-break overlays ──────────────
        //
        // Both renderers expect priceToY / barIndexToX lambdas. Quantower's
        // CoordinatesConverter exposes GetChartY(double price) directly, so
        // the priceToY lambda is a thin wrapper. For barIndexToX, the
        // converter takes a DateTime — we resolve that by looking the bar
        // index up in HistoricalData (offset = Count-1 - barIndex), then
        // calling GetChartX(time). Out-of-range indexes fall through to the
        // marker's stored Time as a safe fallback so a stale marker doesn't
        // crash paint.
        //
        // The CTRL flag on each marker is rebuilt fresh from the latest
        // snapshot so it tracks the *current* controlling pivot rather than
        // whatever was active at append time.
        private void DrawTrendOverlays(Graphics gr, object mainWindowObj)
        {
            if (this.trendStateMachine == null) return;
            if (this.HistoricalData == null) return;
            if (!this.ShowControlPoints && !this.ShowTrendBreaks) return;

            var win = this.CurrentChart.MainWindow;
            var rect = win.ClientRectangle;

            // priceToY — thin wrapper around the chart converter.
            Func<double, int> priceToY = p =>
                (int)Math.Round(win.CoordinatesConverter.GetChartY(p));

            // 2026-05-12 BUGFIX: switched from a BarIndex-based resolver to
            // a Time-based one. The old code translated marker.BarIndex
            // through HistoricalData[offset].TimeLeft to get the screen X,
            // which BROKE when TrendFromITF=true: markers stored their
            // BarIndex relative to the ITF feed (1H bars), but the lookup
            // resolved against chart-TF HistoricalData (5m on a 5m chart).
            // Wrong feed → wrong bar → wrong time → "markers in no man's
            // land." Time is feed-agnostic — convert it directly through
            // the chart's coordinate converter.
            Func<DateTime, int> timeToX = t =>
            {
                try
                {
                    return (int)Math.Round(win.CoordinatesConverter.GetChartX(t));
                }
                catch { return -10000; }
            };

            // Snapshot the marker lists under the lock so concurrent appends
            // from OnBarClose-driven event handlers don't tear iteration.
            // Also stamp IsControllingPivot against the live snapshot.
            List<TradePhantomsIOF.UI.ControlPointMarker> cps = null;
            List<TradePhantomsIOF.UI.TrendBreakMarker> brs = null;
            TradePhantomsIOF.Trend.ControlPoint curCp;
            lock (this.tradesLock)
            {
                if (this.ShowControlPoints && this.controlPointMarkers.Count > 0)
                    cps = this.controlPointMarkers.ToList();
                if (this.ShowTrendBreaks && this.trendBreakMarkers.Count > 0)
                    brs = this.trendBreakMarkers.ToList();

                curCp = this.trendStateMachine.GetSnapshot()?.CurrentControlPoint;
            }

            if (cps != null && cps.Count > 0)
            {
                if (curCp != null)
                {
                    // Re-stamp CTRL flag against current snapshot.
                    foreach (var m in cps)
                    {
                        m.IsControllingPivot =
                            m.BarIndex == curCp.BarIndex &&
                            Math.Abs(m.Price - curCp.Price) < 1e-9;
                    }
                }

                TradePhantomsIOF.UI.ControlPointMarkerRenderer.DrawControlPoints(
                    gr, rect, this.HistoricalData,
                    priceToY, timeToX,
                    cps,
                    Color.Lime,
                    Color.IndianRed,
                    8,
                    Math.Max(8, MapFontPt(this.TextSize)));
            }

            if (brs != null && brs.Count > 0)
            {
                TradePhantomsIOF.UI.ControlPointMarkerRenderer.DrawTrendBreaks(
                    gr, rect, this.HistoricalData,
                    priceToY, timeToX,
                    brs,
                    Color.Orange,
                    Color.Cyan,
                    Math.Max(8, MapFontPt(this.TextSize)));
            }
        }

        // ── Draw entry/SL/TP lines for ARMED + ACTIVE trades ──
        private void DrawTradeLines(Graphics gr, object mainWindowObj, Font labelFont, Brush labelFg)
        {
            if (!this.UseLifecycle || this.lifecycleManager == null) return;

            var win = this.CurrentChart.MainWindow;
            int leftPx  = win.ClientRectangle.Left;
            int rightPx = win.ClientRectangle.Right;

            foreach (var t in this.lifecycleManager.GetActive())
            {
                int yEntry = (int)Math.Round(win.CoordinatesConverter.GetChartY(t.Entry));
                int ySL    = (int)Math.Round(win.CoordinatesConverter.GetChartY(t.CurSL));

                // Use BE pen if curSL == entry within rounding (BE-on-TP1).
                Pen slPenToUse = (Math.Abs(t.CurSL - t.Entry) < 1e-9) ? this.bePen : this.slPen;

                gr.DrawLine(this.entryPen, leftPx, yEntry, rightPx, yEntry);
                gr.DrawLine(slPenToUse,    leftPx, ySL,    rightPx, ySL);

                // TPs
                if (t.TPs != null)
                {
                    for (int k = 0; k < t.TPs.Length; k++)
                    {
                        if (t.TPs[k] == 0.0) continue;
                        int yTp = (int)Math.Round(win.CoordinatesConverter.GetChartY(t.TPs[k]));
                        gr.DrawLine(this.tpPen, leftPx, yTp, rightPx, yTp);
                        gr.DrawString($"TP{k + 1} {Symbol?.FormatPrice(t.TPs[k])}",
                            labelFont, labelFg, leftPx + 6, yTp - labelFont.Height - 2);
                    }
                }

                gr.DrawString($"#{t.Id} {t.State.ToString().ToUpperInvariant()}  ENTRY {Symbol?.FormatPrice(t.Entry)}",
                    labelFont, labelFg, leftPx + 6, yEntry - labelFont.Height - 2);
                gr.DrawString($"SL {Symbol?.FormatPrice(t.CurSL)}",
                    labelFont, labelFg, leftPx + 6, ySL - labelFont.Height - 2);
            }
        }

        // =====================================================================
        // ADAPTERS — bridge TradeRecord → component DTOs
        // =====================================================================

        private TPUI.StripData BuildStripData()
        {
            var d = new TPUI.StripData
            {
                LifecycleOn  = this.UseLifecycle && this.lifecycleManager != null,
                HasActiveTrade = false
            };

            // Defaults if no active trade — populate stats only.
            if (this.lifecycleManager != null)
            {
                var stats = this.lifecycleManager.ComputeStats();
                d.Wins         = stats.Wins;
                d.Losses       = stats.Losses;
                d.BreakEvens   = stats.BreakEvens;
                d.WinRate      = stats.WinRate * 100.0;
                d.ProfitFactor = double.IsInfinity(stats.ProfitFactor) ? 0.0 : stats.ProfitFactor;
                d.AvgWinR      = stats.AvgWinR;
                d.AvgLossR     = stats.AvgLossR;
                d.TotalR       = stats.TotalR;
                d.TotalDollar  = stats.TotalDollar;
            }

            // Pull first ACTIVE trade for live readout.
            TPLifecycle.TradeRecord active = null;
            if (this.lifecycleManager != null)
            {
                foreach (var t in this.lifecycleManager.GetActiveOnly()) { active = t; break; }
            }

            // IM4: if no ACTIVE, fall back to the most recent ARMED trade so
            // the strip previews the staged entry/SL/TPs.
            bool isArmedPreview = false;
            if (active == null && this.lifecycleManager != null)
            {
                foreach (var t in this.lifecycleManager.GetActive())
                {
                    if (t.State == TPLifecycle.TradeState.Armed)
                    {
                        active = t;
                        isArmedPreview = true;
                        break;
                    }
                }
            }

            if (active != null)
            {
                d.HasActiveTrade = true;
                d.TradeId    = active.Id;
                d.IsLong     = active.IsLong;
                // For an ARMED preview, prepend "A" so the user sees this
                // is a staged entry waiting to fill — not a live position.
                string dirCore = (active.IsLong ? "L " : "S ") + TierAbbrev(active.Tier);
                d.DirText    = isArmedPreview ? ("ARMED " + dirCore) : dirCore;
                d.Contracts  = active.Contracts;
                d.DollarRisk = active.DollarRisk;
                d.HitTps     = active.HighestTpHit;
                int totTps = 0;
                if (active.TPs != null)
                    foreach (var v in active.TPs) if (v != 0.0) totTps++;
                d.TotTps     = totTps;
                d.Entry      = active.Entry;
                d.OrigSL     = active.OrigSL;
                d.CurSL      = active.CurSL;
                d.Tps        = active.TPs;
                d.TpHit      = active.TPHit;
                d.SlDist     = active.SLDist;
                d.SymbolPrefix = "$";

                // Current price = last bar close.
                var bar = this.HistoricalData?[0] as HistoryItemBar;
                d.Close = bar?.Close ?? active.Entry;

                // NEXT trigger description — simple heuristic. For ARMED
                // preview, advertise the upcoming fill instead of the next TP.
                if (isArmedPreview)
                {
                    d.NextTrigger = $"Armed #{active.Id} waiting at ENTRY {Symbol?.FormatPrice(active.Entry)}";
                }
                else
                {
                    d.NextTrigger = BuildNextTriggerHint(active);

                    // CR5 (optional): tag the next-trigger string with partial
                    // close history if any (Strat 5 Runner scale-out at TP1).
                    if (active.PartialCloses != null && active.PartialCloses.Count > 0)
                    {
                        double partR = 0;
                        for (int p = 0; p < active.PartialCloses.Count; p++)
                            partR += active.PartialCloses[p].R;
                        d.NextTrigger = $"{d.NextTrigger}  [PC×{active.PartialCloses.Count} {partR:+0.00;-0.00;0.00}R]";
                    }
                }
            }

            // 2026-05-06: Trend strip additions. Populated last so it's a
            // strict surface enrichment — strip behavior with EnableTrendDetection
            // off matches the pre-trend code (TrendState defaults to "FLAT",
            // TrendAligned defaults to true). The misalignment red bg in
            // StatsStripRenderer fires only when HasActiveTrade && !TrendAligned
            // && TrendState != "FLAT", so a Flat trend never colors the strip.
            if (this.trendStateMachine != null)
            {
                var snap = this.trendStateMachine.GetSnapshot();
                d.TrendState =
                    snap.State == TradePhantomsIOF.Trend.TrendState.Bull ? "BULL" :
                    snap.State == TradePhantomsIOF.Trend.TrendState.Bear ? "BEAR" : "FLAT";
                d.ControllingPivotPrice = snap.ControllingPivotPrice;
                d.LastBreakAgo = (snap.LastBreak != null)
                    ? $"{(int)(DateTime.UtcNow - snap.LastBreak.Time).TotalMinutes}m ago"
                    : "—";
                if (active != null)
                {
                    bool aligned =
                        ( active.IsLong && snap.State == TradePhantomsIOF.Trend.TrendState.Bull) ||
                        (!active.IsLong && snap.State == TradePhantomsIOF.Trend.TrendState.Bear);
                    d.TrendAligned = aligned;
                }
                else
                {
                    d.TrendAligned = true;
                }
            }

            return d;
        }

        private static string TierAbbrev(TPLifecycle.ZoneTier tier)
        {
            switch (tier)
            {
                case TPLifecycle.ZoneTier.HTF: return "H";
                case TPLifecycle.ZoneTier.ITF: return "I";
                default: return "L";
            }
        }

        private string BuildNextTriggerHint(TPLifecycle.TradeRecord t)
        {
            // Find next pending TP.
            if (t.TPs == null) return "—";
            for (int k = 0; k < t.TPs.Length; k++)
            {
                if (t.TPs[k] == 0.0) continue;
                bool hit = (t.TPHit != null && k < t.TPHit.Length && t.TPHit[k]);
                if (!hit)
                {
                    return $"TP{k + 1}@{Symbol?.FormatPrice(t.TPs[k])}";
                }
            }
            return "trail continues";
        }

        private TPUI.ClosedTradeRow ToClosedRow(TPLifecycle.TradeRecord t)
        {
            int totTps = 0;
            if (t.TPs != null)
                foreach (var v in t.TPs) if (v != 0.0) totTps++;

            string outcome;
            switch (t.State)
            {
                case TPLifecycle.TradeState.Win:       outcome = "WIN";  break;
                case TPLifecycle.TradeState.Loss:      outcome = "LOSS"; break;
                case TPLifecycle.TradeState.BreakEven: outcome = "BE";   break;
                default: outcome = "—"; break;
            }

            return new TPUI.ClosedTradeRow
            {
                TradeNum   = t.Id,
                Tier       = t.Tier.ToString(),
                Dir        = t.IsLong ? "L" : "S",
                Outcome    = outcome,
                HitTps     = t.HighestTpHit,
                TotTps     = totTps,
                // 2026-05-06: route through ExitReasonLabel.Short so new
                // long-named reasons (TrendBroken → "TBR", Force → "FRC")
                // get sane 4-char column-friendly labels without touching
                // DashboardRenderer's color map.
                ExitReason = TradePhantomsIOF.Lifecycle.ExitReasonLabel.Short(t.ExitReason),
                R          = t.R,
                DollarPnL  = t.DollarPnL,
                CloseTime  = t.CloseTime
            };
        }

        private TPUI.DashboardSettings BuildDashboardSettings()
        {
            return new TPUI.DashboardSettings
            {
                IndicatorVersion = "IOF QuickEntry v2.0",
                Ticker           = Symbol?.Name ?? "",
                Timeframe        = GetChartPeriodString(),
                LifecycleOn      = this.UseLifecycle,
                SLStrategyName   = this.ActiveTrailStrategy.ToString(),
                SeqGate          = this.UseSequentialGate,
                RiskPerTrade     = this.DollarRiskPerTrade,
                MaxContracts     = this.MaxContracts,
                TpCount          = this.TpCount,
                TpStep           = this.TpStep,
                ArmProx          = this.ArmProx,
                MaxPlans         = this.MaxRetainedClosed,
                OFEntry          = ((int)this.OFEntry).ToString() + "%",
                SLBufferTicks    = this.StopBufferTicks
            };
        }

        private TPUI.DashboardZones BuildDashboardZones()
        {
            var dz = new TPUI.DashboardZones();
            int dem = 0, sup = 0;
            double nearestDem = double.NaN, nearestSup = double.NaN;
            double curPrice = double.NaN;
            var bar = this.HistoricalData?[0] as HistoryItemBar;
            if (bar != null) curPrice = bar.Close;

            // LTF (chart-TF) counts. Skip invalidated — same fix as DrawZones.
            foreach (var z in this.zones)
            {
                if (z.Invalidated) continue;
                bool isDem = z.Type == ZoneType.RBR || z.Type == ZoneType.DBR;
                if (isDem) dem++;
                else       sup++;

                if (!double.IsNaN(curPrice))
                {
                    double mid = (z.Top + z.Bottom) / 2.0;
                    if (isDem && mid <= curPrice)
                    {
                        if (double.IsNaN(nearestDem) || mid > nearestDem) nearestDem = mid;
                    }
                    else if (!isDem && mid >= curPrice)
                    {
                        if (double.IsNaN(nearestSup) || mid < nearestSup) nearestSup = mid;
                    }
                }
            }
            dz.LtfDemandCount = dem;
            dz.LtfSupplyCount = sup;
            dz.LtfNearestDemand = nearestDem;
            dz.LtfNearestSupply = nearestSup;
            dz.NearestDemandPrice = nearestDem;
            dz.NearestSupplyPrice = nearestSup;

            // ITF / HTF counts (only when MTF feature is on).
            if (this.UseMTFZones)
            {
                CountTier(this.itfZones, curPrice,
                    out int idem, out int isup,
                    out double inDem, out double inSup);
                dz.ItfDemandCount = idem;
                dz.ItfSupplyCount = isup;
                dz.ItfNearestDemand = inDem;
                dz.ItfNearestSupply = inSup;

                CountTier(this.htfZones, curPrice,
                    out int hdem, out int hsup,
                    out double hnDem, out double hnSup);
                dz.HtfDemandCount = hdem;
                dz.HtfSupplyCount = hsup;
                dz.HtfNearestDemand = hnDem;
                dz.HtfNearestSupply = hnSup;
            }

            return dz;
        }

        // Count active demand/supply zones in an MTF tier and locate the
        // nearest in-range mid-price for each side relative to curPrice.
        private static void CountTier(
            List<TPMTF.TimeframeZone> layer, double curPrice,
            out int demCount, out int supCount,
            out double nearestDem, out double nearestSup)
        {
            demCount = 0;
            supCount = 0;
            nearestDem = double.NaN;
            nearestSup = double.NaN;
            if (layer == null) return;

            for (int i = 0; i < layer.Count; i++)
            {
                var z = layer[i];
                if (z == null || !z.Active) continue;

                if (z.IsLong) demCount++; else supCount++;

                if (!double.IsNaN(curPrice))
                {
                    double top    = z.IsLong ? z.BodyHi : z.WickHi;
                    double bottom = z.IsLong ? z.WickLo : z.BodyLo;
                    double mid    = (top + bottom) / 2.0;
                    if (z.IsLong && mid <= curPrice)
                    {
                        if (double.IsNaN(nearestDem) || mid > nearestDem) nearestDem = mid;
                    }
                    else if (!z.IsLong && mid >= curPrice)
                    {
                        if (double.IsNaN(nearestSup) || mid < nearestSup) nearestSup = mid;
                    }
                }
            }
        }

        private List<TPUI.ActiveTradeRow> BuildActiveTradeRows()
        {
            var list = new List<TPUI.ActiveTradeRow>();
            if (this.lifecycleManager == null) return list;
            foreach (var t in this.lifecycleManager.GetActive())
            {
                list.Add(new TPUI.ActiveTradeRow
                {
                    TradeNum   = t.Id,
                    State      = t.State.ToString().ToUpperInvariant(),
                    Dir        = (t.IsLong ? "L " : "S ") + TierAbbrev(t.Tier),
                    Contracts  = t.Contracts,
                    DollarRisk = t.DollarRisk,
                    Entry      = t.Entry,
                    CurSL      = t.CurSL
                });
            }
            return list;
        }

        // 2026-05-13: NEXT TRADES preview — closest tradeable zones to price.
        // Iterates the chart-TF zone list (this.zones), filters to non-invalidated
        // zones at or above MinScore whose structural R:R ≥ 3, computes per-zone
        // entry/SL/3R/qty using the same math the lifecycle uses (EntryTPMath),
        // and returns the 2 nearest demand + 2 nearest supply by distance to
        // current price. Returns empty list when no qualifiers exist.
        private List<TPUI.NextTradeRow> BuildNextTradeRows(double currentPrice)
        {
            var output = new List<TPUI.NextTradeRow>(4);
            if (this.zones == null || this.zones.Count == 0) return output;
            if (this.Symbol == null) return output;

            double tickSize = this.Symbol.TickSize;
            double pointVal = ResolvePointValue();
            if (tickSize <= 0 || pointVal <= 0) return output;

            var ofLevel = (TradePhantomsIOF.OFEntryLevel)this.OFEntry;

            var demand = new List<(double dist, TPUI.NextTradeRow row)>();
            var supply = new List<(double dist, TPUI.NextTradeRow row)>();

            foreach (var z in this.zones)
            {
                if (z == null || z.Invalidated) continue;
                if (z.Score < this.MinScore) continue;
                // Structural R:R must be ≥ 3. EstimatedRrr is the structural
                // estimate set at scan time (NaN when no opposing zone found,
                // which we treat as "no 3R target" → skip).
                if (double.IsNaN(z.EstimatedRrr) || z.EstimatedRrr < 3.0) continue;

                bool isDemand = z.Type == ZoneType.RBR || z.Type == ZoneType.DBR;

                double entry = TradePhantomsIOF.EntryTPMath.ComputeEntry(
                    ofLevel, isDemand, z.BodyHi, z.BodyLo, z.WickHi, z.WickLo);
                double sl = TradePhantomsIOF.EntryTPMath.ComputeOrigSL(
                    isDemand, z.WickHi, z.WickLo, tickSize, this.StopBufferTicks);

                entry = TradePhantomsIOF.EntryTPMath.SnapToTickNearest(entry, tickSize);
                sl    = TradePhantomsIOF.EntryTPMath.SnapAwayFromReference(sl, entry, tickSize);

                // Mirror PassC_ArmNew price-side gate (TradeLifecycle.cs:1041).
                // Demand: arm only when price still ABOVE entry (waiting to fall
                // INTO the long); Supply: arm only when price still BELOW entry
                // (waiting to rise INTO the short). If price has already crossed
                // entry, the chart-TF refuses to arm — so don't preview a trade
                // that can't fire.
                bool priceOk = isDemand ? (currentPrice > entry) : (currentPrice < entry);
                if (!priceOk) continue;

                double risk = Math.Abs(entry - sl);
                if (risk <= 0) continue;

                double target3R = isDemand ? entry + 3.0 * risk : entry - 3.0 * risk;
                target3R = TradePhantomsIOF.EntryTPMath.SnapAwayFromReference(target3R, entry, tickSize);

                double slDist = TradePhantomsIOF.EntryTPMath.ComputeSlDistance(entry, sl);
                int contracts = TradePhantomsIOF.EntryTPMath.ComputeContracts(
                    this.DollarRiskPerTrade, slDist, pointVal, this.MaxContracts);
                if (contracts <= 0) continue;

                var row = new TPUI.NextTradeRow
                {
                    IsLong    = isDemand,
                    Entry     = entry,
                    Sl        = sl,
                    Target3R  = target3R,
                    Contracts = contracts,
                    DistPts   = entry - currentPrice,  // signed; magnitude = how far to fill
                };
                double dist = Math.Abs(row.DistPts);
                if (isDemand) demand.Add((dist, row));
                else          supply.Add((dist, row));
            }

            demand.Sort((a, b) => a.dist.CompareTo(b.dist));
            supply.Sort((a, b) => a.dist.CompareTo(b.dist));

            for (int i = 0; i < Math.Min(2, demand.Count); i++) output.Add(demand[i].row);
            for (int i = 0; i < Math.Min(2, supply.Count); i++) output.Add(supply[i].row);
            return output;
        }

        // 2026-05-06: build the optional TREND section payload for the
        // dashboard. Returns null when the feature is disabled or the state
        // machine hasn't initialized yet (DashboardRenderer skips the section
        // entirely on null). Aligned/Opposed counts are derived against the
        // current chart-TF zones list.
        private TPUI.DashboardTrend BuildDashboardTrend()
        {
            if (!this.EnableTrendDetection) return null;
            if (this.trendStateMachine == null) return null;

            var snap = this.trendStateMachine.GetSnapshot();
            int aligned = CountZonesAlignedWithTrend(snap.State, isLong: true)
                        + CountZonesAlignedWithTrend(snap.State, isLong: false);
            int opposed = CountZonesOpposedToTrend(snap.State);

            return new TPUI.DashboardTrend
            {
                CurrentState           = snap.State.ToString().ToUpper(),
                LastStateChange        = this.lastTrendStateChangeAt,
                ControllingPivotPrice  = snap.ControllingPivotPrice,
                LastControlPointTime   = snap.CurrentControlPoint?.Time  ?? default(DateTime),
                LastControlPointPrice  = snap.CurrentControlPoint?.Price ?? double.NaN,
                LastBreakReason        = (snap.LastBreak != null)
                    ? $"{(snap.LastBreak.OldTrend == TradePhantomsIOF.Trend.TrendState.Bull ? "HL" : "LH")} {snap.LastBreak.BrokenControlPoint:F2} broken at {snap.LastBreak.Time:HH:mm}"
                    : "no recent",
                RecentControlPointCount = snap.RecentControlPoints?.Count ?? 0,
                AlignedZoneCount        = aligned,
                OpposedZoneCount        = opposed
            };
        }

        private TPUI.DashboardStats BuildDashboardStats()
        {
            var s = new TPUI.DashboardStats();
            if (this.lifecycleManager == null) return s;
            var lc = this.lifecycleManager.ComputeStats();
            s.Wins         = lc.Wins;
            s.Losses       = lc.Losses;
            s.BreakEvens   = lc.BreakEvens;
            s.WinRate      = lc.WinRate * 100.0;
            s.ProfitFactor = double.IsInfinity(lc.ProfitFactor) ? 0.0 : lc.ProfitFactor;
            s.TotalR       = lc.TotalR;
            s.TotalDollar  = lc.TotalDollar;
            s.AvgWinR      = lc.AvgWinR;
            s.AvgLossR     = lc.AvgLossR;
            // Streaks: walk closed trades chronologically.
            int curWin = 0, curLoss = 0, maxWin = 0, maxLoss = 0;
            // Build list in oldest-first order for streak calc.
            var closedSorted = new List<TPLifecycle.TradeRecord>();
            foreach (var t in this.lifecycleManager.Trades)
                if (t.IsClosed) closedSorted.Add(t);
            closedSorted.Sort((a, b) => a.CloseTime.CompareTo(b.CloseTime));
            foreach (var t in closedSorted)
            {
                if (t.State == TPLifecycle.TradeState.Win)
                {
                    curWin++; curLoss = 0;
                    if (curWin > maxWin) maxWin = curWin;
                }
                else if (t.State == TPLifecycle.TradeState.Loss)
                {
                    curLoss++; curWin = 0;
                    if (curLoss > maxLoss) maxLoss = curLoss;
                }
                else
                {
                    curWin = 0; curLoss = 0;
                }
            }
            s.MaxWinStreak  = maxWin;
            s.MaxLossStreak = maxLoss;
            return s;
        }

        // =====================================================================
        // SUPPORT TYPES — local to the master file (zones detection + scoring).
        // =====================================================================
        private enum ZoneType { RBR, DBR, RBD, DBD }
        // LegDir removed in CR1 — direction classification is now inside
        // MultiTFZoneScanner (ZoneTimeframe + FormationCode "RBR/DBR/RBD/DBD").

        private class IofZone
        {
            public string Id;
            public ZoneType Type;
            public int StartIndex, EndIndex;
            public DateTime StartTime, EndTime;
            public double Top, Bottom;
            // CR1: full body + wick geometry preserved at detection time so the
            // ZoneInfo mapping (BuildZoneInfoSnapshot) gets correct WickHi /
            // WickLo for BOTH demand and supply zones. Previously the supply
            // mapping copied z.Top into WickHi, which is correct here only
            // because we now actually populate Top with the highest wick for
            // supply (matches the asymmetric BuildZoneRect rule).
            public double BodyHi, BodyLo;
            public double WickHi, WickLo;
            public int BaseCandleCount;
            public double BaseHeight, MoveOut;
            public int TouchCount;
            public double MaxPenetrationPct;
            public string ItfTrend;
            public double EstimatedRrr;
            public double EstimatedTargetPrice;

            public bool Invalidated;            // true after price closes through the far wick

            // 2026-05-11: Globex Trap candidate flag (per the Globex Traps 101
            // PDF doctrine). Supply zone whose entire BODY sits above current
            // Globex High → "bull trap above GH". Demand zone whose body sits
            // below Globex Low → "bear trap below GL". Computed once per scan
            // after Globex window is resolved. Emitted in zone_detected /
            // market_snapshot.nearby_zones so the hub can build trap-only
            // routing rules and cohort the forward-test accordingly.
            public bool IsGlobexTrap;

            public int RangeScore, TimeScore, PurityScore;
            public int StrengthScore, TrendScore, RrrScore, JuiceScore;
            // IM7: was int. Now double so MTFC bonus (a double) can be
            // applied with full precision and so the bonused score still
            // makes sense against the int-typed MinScore filter.
            public double Score;

            // MTFC bonus: > 0 if this zone overlaps a higher-tier zone of
            // the same direction. Added on top of JuiceScore at scan time.
            public double MtfcBonus;
        }
    }
}
