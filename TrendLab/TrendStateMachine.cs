// TrendStateMachine.cs — Leg-based trend engine per Mr. Black's methodology.
//
// A "leg" = continuous run of same-direction closes until the opposite direction
// breaks it. Three alternating legs = one structure.
// Dual control points: ControllingHigh (HH from bull structures, only rises)
//                      ControllingLow  (LL from bear structures, only falls)
// FLAT when price is between both. No direct Bull→Bear flip.

using System;
using System.Collections.Generic;

namespace TradePhantomsIOF.Trend
{
    public enum TrendState { Flat = 0, Bull = 1, Bear = 2 }

    // ── Backward-compat stubs ─────────────────────────────────────────────────
    public enum CandleDirection { Bullish = 1, Bearish = -1, Doji = 0 }
    public class ControlPoint  { public double Price; public TrendState Direction; public DateTime Time; public int BarIndex; public double EngulfingHigh; public double EngulfingLow; public double EngulfingOpen; public double EngulfingClose; }
    public class TrendBreakEvent { public DateTime Time; public int BarIndex; public TrendState OldTrend; public TrendState NewTrend; public double BrokenControlPoint; public double BreakBarClose; }
    public class SwingPivot { public DateTime Time; public int BarIndex; public double Price; public bool IsHigh; public bool IsControllingPivot; }

    // ── Leg pivot exposed for chart drawing ───────────────────────────────────
    public class LegPivot
    {
        public DateTime Time;    // time when extreme close was set
        public double   Price;   // extreme close price
        public string   Label;   // "HH", "HL", "LH", "LL"
        public bool     IsBull;  // true = bull leg (HH or LH), false = bear leg (LL or HL)
        public double   BarHigh = double.NaN; // highest HIGH of the leg (wick) — for structural stops
        public double   BarLow  = double.NaN; // lowest LOW of the leg (wick)
    }

    // ── Trade entry / re-entry signal (continuation, Mr. Black) ────────────────
    public class TrendEntry
    {
        public DateTime   Time;
        public double     Price;   // ideal entry level
        public int        Side;    // +1 long, -1 short
        public string     Kind;    // "initial" (trend confirm) | "reentry" (continuation pivot)
        public double     Stop;    // invalidation level (beyond the pivot)
        public double     Target;  // first target (opposing control, else 3R projection)
        public double     RR;      // reward:risk to Target
        public string     Status = "pending"; // "pending" | "win" | "loss" | "scratch"
        public double     ResultR; // realized R when resolved (+TpRR win, 0 breakeven, -1 loss)
        public double     MfeR;    // peak favorable excursion (R) — drives the TP-at-TpRR resolution
        public TrendState Trend;   // trend state at time of signal
    }

    // An ARMED setup the bot is waiting on — a trend-aligned zone price hasn't retraced into
    // yet. Read-only (does not affect which trades get taken), so safe to expose live.
    public class PendingSetup
    {
        public int      Side;       // +1 long, -1 short
        public double   Entry;      // zone proximal edge (where it would enter)
        public double   Stop;       // far wick ± buffer
        public double   Target;     // next opposing zone
        public double   RR;
        public string   Formation;  // RBR / DBR / RBD / DBD
        public DateTime ZoneTime;   // when the zone formed
    }

    // One trade followed to its stop, recording max-favorable-excursion in R so any
    // take-profit R:R can be evaluated after the fact.
    public class SimTrade
    {
        public DateTime EntryTime;
        public double   Entry;
        public int      Side;      // +1 long, -1 short
        public double   Stop;
        public double   RiskPts;
        public double   TargetR;   // this trade's REAL target in R (zone-to-zone). 0 = use a fixed TpRR.
        public double   MfeR;      // max favorable excursion (R) reached before the stop
        public double   MaeR;      // max ADVERSE excursion (R) — intraday heat, for real drawdown
        public bool     Stopped;
        public bool     Open = true;
        public DateTime StopTime;          // when the stop was hit
        public DateTime[] RrHit = new DateTime[6];  // first time MfeR reached each RrLadder level
    }

    // Result of simulating a 1-contract funded account over the resolved trades.
    public class AccountSimResult
    {
        public bool   Passed;        // hit profit target first
        public bool   Blown;         // hit trailing drawdown first
        public double FinalDollars;
        public double PeakDollars;
        public double MaxDrawdown;
        public int    TradesTaken;
        public int    Skipped;       // skipped (stop too wide for risk cap)
        public int    TradesToResult;
    }

    public class TrendSnapshot
    {
        public TrendState          State;
        public TrendState          Lean;   // directional lean: State, or the last trend broken (during Flat)
        public int                 Wins;       // resolved entry outcomes (this TF)
        public int                 Losses;
        public int                 Scratches;  // breakeven exits (reached 1R then stopped → 0R)
        public double              TotalR;     // sum of realized R
        public double              ControllingHigh = double.NaN;
        public double              ControllingLow  = double.NaN;
        public List<LegPivot>      LegPivots       = new List<LegPivot>();
        public List<TrendEntry>    Entries         = new List<TrendEntry>();
        public ControlPoint        CurrentControlPoint;
        public List<SwingPivot>    RecentPivots        = new List<SwingPivot>();
        public List<ControlPoint>  RecentControlPoints = new List<ControlPoint>();
        public TrendBreakEvent     LastBreak;
        public DateTime            LastUpdated;

        // Active (unsealed) leg diagnostics — for tuning the leg-seal logic.
        public int                 ActiveLegDir;       // +1 bull, -1 bear, 0 none
        public double              ActiveLegExtreme = double.NaN;
        public double              ActiveLegStart   = double.NaN;
        public double              LastBarClose     = double.NaN;
        public int                 LegsSealed;
        public int                 PivotsCount;

        public double ControllingPivotPrice =>
            State == TrendState.Bull ? ControllingLow  :
            State == TrendState.Bear ? ControllingHigh :
            double.NaN;
    }

    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class Leg
    {
        public int      Direction;    // +1 bull, -1 bear
        public double   Extreme;      // highest close (bull) or lowest close (bear)
        public DateTime ExtremeTime;  // bar time when extreme was set
        public double   StartClose;
        public double   EndClose;
        public DateTime StartTime;
        public double   BarHigh;      // highest HIGH during the leg (wick)
        public double   BarLow;       // lowest LOW during the leg (wick)
    }

    // ─────────────────────────────────────────────────────────────────────────
    public class TrendStateMachine
    {
        // ── Compat props ──────────────────────────────────────────────────────
        public int  SwingFractalLookback            = 3;
        public int  RequireSegments                 = 3;
        public bool RequireEngulfingForControlPoint = false;
        public int  MaxPivotHistory                 = 50;

        // ── Leg size filter ───────────────────────────────────────────────────
        // A leg only seals if its extreme moved at least this many ticks from
        // its start close. Filters out single-candle noise on choppy TFs.
        public int MinLegTicks = 4;

        public TrendState CurrentState { get; private set; } = TrendState.Flat;

        public event Action<ControlPoint>           OnControlPointDetected;
        public event Action<TrendBreakEvent>        OnTrendBroken;
        public event Action<TrendState, TrendState> OnTrendStateChanged;

        // ── Active leg ────────────────────────────────────────────────────────
        private int      _legDir     = 0;
        private double   _legExtreme = double.NaN;
        private DateTime _legExtremeTime;
        private double   _legStart   = double.NaN;
        private DateTime _legStartTime;
        private double   _lastClose  = double.NaN;
        private double   _legBarHigh = double.NaN;   // running swing high (wick) of current leg
        private double   _legBarLow  = double.NaN;   // running swing low (wick) of current leg

        // ── Completed legs ────────────────────────────────────────────────────
        private readonly List<Leg>      _legs     = new List<Leg>();
        private readonly List<LegPivot> _pivots   = new List<LegPivot>();

        // ── Dual control points ───────────────────────────────────────────────
        private double _ctrlHigh = double.NaN;
        private double _ctrlLow  = double.NaN;

        // ── Bias lean + entry detection ───────────────────────────────────────
        private TrendState _lastActiveTrend = TrendState.Flat;   // last non-Flat trend (for lean)
        private TrendState _entryPrevState  = TrendState.Flat;   // to detect Flat->trend (initial entry)
        private DateTime   _entryLastPivotTime = DateTime.MinValue; // dedupe re-entry per pivot
        private readonly List<TrendEntry> _entries = new List<TrendEntry>();
        private readonly List<TrendEntry> _openTrades = new List<TrendEntry>(); // tracked for win/loss
        private int        _wins, _losses, _scratches;
        private readonly List<double> _tradeRs = new List<double>();   // every resolved trade's blended R — for journal/Monte-Carlo
        public IReadOnlyList<double> GetTradeRs() => new List<double>(_tradeRs);
        private double     _totalR;

        // MFE-based trade book: each trade is followed to its STOP, recording the max
        // favorable excursion (in R). Lets us sweep ANY take-profit R:R after the fact:
        // a trade "wins at RR" if MfeR >= RR (reached the target before the stop).
        public double TpRR = 2.0;                          // chosen take-profit R:R (set by indicator)
        private readonly List<SimTrade> _simTrades = new List<SimTrade>();
        private double     _tickSizeCache = 0.25;                   // for entry stop buffers
        private List<IofZone> _activeZones;                         // current IOF zones (for tight wick stops)
        public  double BreakevenAtPct = 0.5;                        // breakeven-stop trigger as a fraction of TpRR (entries resolve at TpRR)
        public  bool UseZoneEntries = true;                         // fire on first-touch retrace into a trend-aligned zone
        public  bool ZoneRequireRejection = true;                   // need a close BACK OUT of the zone (rejection), not raw touch
        public  double ZoneMinRR = 2.0;                             // skip zone setups whose zone-to-zone R:R is below this
        private readonly HashSet<DateTime> _enteredZones = new HashSet<DateTime>();  // one-touch consumed zones

        // The indicator feeds in this chart-TF's scanned zones so entries can use a tight
        // zone WICK as the stop (the method's real stop) instead of the full swing — which
        // is what lets higher TFs (4h/1h) trade without a huge risk.
        public void SetActiveZones(List<IofZone> zones) => _activeZones = zones;

        // ─────────────────────────────────────────────────────────────────────
        public void OnBarClose(int barIndex, DateTime time,
                               double open, double high, double low, double close,
                               double tickSize)
        {
            if (double.IsNaN(_lastClose))
            {
                _lastClose      = close;
                _legDir         = close >= open ? 1 : -1;
                _legExtreme     = close;
                _legExtremeTime = time;
                _legStart       = close;
                _legStartTime   = time;
                _legBarHigh     = high;
                _legBarLow      = low;
                return;
            }

            TrendState oldState = CurrentState;
            _tickSizeCache = tickSize;
            _legBarHigh = Math.Max(_legBarHigh, high);
            _legBarLow  = Math.Min(_legBarLow,  low);
            int dir = close > open ? 1 : close < open ? -1 : _legDir;

            // Leg break requires: opposite candle closes beyond prior close AND
            // the leg that's ending moved at least MinLegTicks from its start.
            double minMove = MinLegTicks * tickSize;
            bool legMoved = (_legDir == 1  && _legExtreme >= _legStart + minMove)
                         || (_legDir == -1 && _legExtreme <= _legStart - minMove);

            bool broken = legMoved
                       && ((_legDir ==  1 && dir == -1 && close < _lastClose)
                        || (_legDir == -1 && dir ==  1 && close > _lastClose));

            if (broken)
            {
                var leg = new Leg
                {
                    Direction   = _legDir,
                    Extreme     = _legExtreme,
                    ExtremeTime = _legExtremeTime,
                    StartClose  = _legStart,
                    EndClose    = _lastClose,
                    StartTime   = _legStartTime,
                    BarHigh     = _legBarHigh,
                    BarLow      = _legBarLow
                };
                _legs.Add(leg);
                if (_legs.Count > 20) _legs.RemoveAt(0);

                // Label this leg relative to the previous same-direction leg
                AddLegPivot(leg);

                _legDir         = dir;
                _legExtreme     = close;
                _legExtremeTime = time;
                _legStart       = close;
                _legStartTime   = time;
                _legBarHigh     = high;   // new leg starts tracking from this bar
                _legBarLow      = low;
            }
            else
            {
                bool newExtreme = (_legDir == 1 && close > _legExtreme)
                               || (_legDir == -1 && close < _legExtreme);
                if (newExtreme)
                {
                    _legExtreme     = close;
                    _legExtremeTime = time;
                }
                else if (!legMoved)
                {
                    // The leg never developed MinLegTicks in its own direction, yet price has
                    // now moved MinLegTicks the OTHER way past the leg's start — the initial
                    // direction was a mis-read (a single reversal bar that never followed
                    // through). Flip the leg direction (no pivot) so structure tracks the real
                    // move instead of freezing forever and stranding the control points.
                    bool wrongWayBull = (_legDir ==  1 && close <= _legStart - minMove);
                    bool wrongWayBear = (_legDir == -1 && close >= _legStart + minMove);
                    if (wrongWayBull || wrongWayBear)
                    {
                        _legDir         = -_legDir;
                        _legExtreme     = close;   // flipped leg now tracks from here
                        _legExtremeTime = time;
                        // keep _legStart/_legStartTime so the flipped leg measures from origin
                    }
                }
            }

            _lastClose = close;
            EvaluateState(close);

            if (CurrentState != TrendState.Flat) _lastActiveTrend = CurrentState;
            DetectEntries(time, close);
            DetectZoneEntries(time, high, low, close);
            UpdateEntryOutcomes(time, high, low);
            UpdateSimTrades(time, high, low);

            if (CurrentState != oldState && OnTrendStateChanged != null)
                OnTrendStateChanged(oldState, CurrentState);
        }

        // ── Entry / re-entry detection (continuation, Mr. Black) ──────────────
        // Initial entry = trend confirms from Flat. Re-entry = a new continuation pivot
        // in the trend direction (Bear→a fresh LH = sell-the-rally; Bull→a fresh HL =
        // buy-the-dip). Stop = beyond that pivot. Entries are signals only (no orders).
        private void DetectEntries(DateTime time, double close)
        {
            // Initial entry on a fresh trend confirmation
            if (_entryPrevState == TrendState.Flat && CurrentState != TrendState.Flat)
            {
                int side = CurrentState == TrendState.Bull ? 1 : -1;
                double stop = CurrentState == TrendState.Bull ? _ctrlLow : _ctrlHigh;
                AddEntry(time, close, side, "initial", TighterZoneStop(side, close, stop));
            }
            _entryPrevState = CurrentState;

            // Re-entry on a new continuation pivot (one per pivot)
            if (CurrentState != TrendState.Flat && _pivots.Count > 0)
            {
                var p = _pivots[_pivots.Count - 1];
                if (p.Time != _entryLastPivotTime)
                {
                    _entryLastPivotTime = p.Time;
                    // STRUCTURAL stop: beyond the swing's actual high/low (the wick), not just
                    // the close — so the stop sits where the trade is really invalidated.
                    double buf = Math.Max(2, MinLegTicks) * _tickSizeCache;
                    if (CurrentState == TrendState.Bear && p.Label == "LH")
                    {
                        double swingHigh = double.IsNaN(p.BarHigh) ? p.Price : p.BarHigh;
                        AddEntry(p.Time, p.Price, -1, "reentry", TighterZoneStop(-1, p.Price, swingHigh + buf));
                    }
                    else if (CurrentState == TrendState.Bull && p.Label == "HL")
                    {
                        double swingLow = double.IsNaN(p.BarLow) ? p.Price : p.BarLow;
                        AddEntry(p.Time, p.Price, 1, "reentry", TighterZoneStop(1, p.Price, swingLow - buf));
                    }
                }
            }
        }

        // ── Zone-retrace entry (the real IOF method) ─────────────────────────
        // Fire on the FIRST touch of a trend-aligned active IOF zone — i.e. price pulling
        // back into a demand zone while the lean is bullish (or a supply zone while bearish).
        // Entry at the proximal edge, stop beyond the far wick, target = next opposing zone.
        // This trades the pullbacks the trend-break logic sits out (esp. on higher TFs).
        private void DetectZoneEntries(DateTime time, double high, double low, double close)
        {
            if (!UseZoneEntries || _activeZones == null) return;
            // Directional bias: the live trend, or the last one if currently Flat.
            int bias = CurrentState == TrendState.Bull ? 1 : CurrentState == TrendState.Bear ? -1
                     : _lastActiveTrend == TrendState.Bull ? 1 : _lastActiveTrend == TrendState.Bear ? -1 : 0;
            if (bias == 0) return;
            double buf = Math.Max(2, MinLegTicks) * _tickSizeCache;

            foreach (var z in _activeZones)
            {
                if (z == null || !z.Active) continue;
                if ((bias > 0) != z.IsLong) continue;          // bull→demand(long), bear→supply(short)
                if (z.BaseEndTime >= time) continue;           // only retrace into an already-formed zone
                if (_enteredZones.Contains(z.BaseEndTime)) continue;  // one-touch only

                double edge = z.EntryEdge;
                // PRECISION: require a rejection — price wicks into the zone but closes back
                // OUT in the trade direction. Catches the bounce, skips the blow-through.
                bool touched  = z.IsLong ? low <= edge : high >= edge;
                if (!touched) continue;
                bool rejected = z.IsLong ? close > edge : close < edge;
                if (ZoneRequireRejection && !rejected) continue;

                int side = z.IsLong ? 1 : -1;
                double stop   = z.IsLong ? z.FarWick - buf : z.FarWick + buf;
                double target = FindOpposingZoneTarget(side, edge);
                // QUALITY: need a real opposing-zone target at the minimum R:R, else skip.
                if (double.IsNaN(target)) continue;
                double risk = Math.Abs(stop - edge), reward = Math.Abs(target - edge);
                if (risk <= 0 || reward / risk < ZoneMinRR) continue;

                _enteredZones.Add(z.BaseEndTime);                       // consume on a real entry
                if (_enteredZones.Count > 400) _enteredZones.Clear();   // bound memory
                AddEntry(time, edge, side, "zone", stop, target);
            }
        }

        // Nearest opposing active zone in the profit direction = the zone-to-zone target.
        private double FindOpposingZoneTarget(int side, double entry)
        {
            if (_activeZones == null) return double.NaN;
            double best = double.NaN;
            foreach (var z in _activeZones)
            {
                if (z == null || !z.Active) continue;
                bool opposing = side > 0 ? !z.IsLong : z.IsLong;   // long→supply above; short→demand below
                if (!opposing) continue;
                double edge = z.EntryEdge;
                if (side > 0) { if (edge > entry && (double.IsNaN(best) || edge < best)) best = edge; }
                else          { if (edge < entry && (double.IsNaN(best) || edge > best)) best = edge; }
            }
            return best;
        }

        // If a trend-aligned IOF zone sits just beyond the entry, its far WICK is a tighter,
        // valid stop than the full swing — the method's real stop. Returns the tighter of the
        // two so higher TFs (4h/1h) can trade on a small wick instead of the whole leg.
        private double TighterZoneStop(int side, double entry, double structuralStop)
        {
            if (_activeZones == null) return structuralStop;
            double buf = Math.Max(2, MinLegTicks) * _tickSizeCache;
            double best = double.NaN;
            foreach (var z in _activeZones)
            {
                if (z == null || !z.Active) continue;
                bool wantSupply = side < 0;            // short → supply zone above; long → demand below
                if (z.IsLong == wantSupply) continue;  // keep demand for long, supply for short
                double wick = side < 0 ? z.WickHi : z.WickLo;
                double stop = side < 0 ? wick + buf : wick - buf;
                bool correctSide = side < 0 ? stop > entry : stop < entry;
                if (!correctSide) continue;
                double dist = Math.Abs(stop - entry);
                if (dist < buf) continue;              // not absurdly tight
                if (double.IsNaN(best) || dist < Math.Abs(best - entry)) best = stop;
            }
            if (double.IsNaN(best)) return structuralStop;
            return Math.Abs(best - entry) < Math.Abs(structuralStop - entry) ? best : structuralStop;
        }

        private void AddEntry(DateTime time, double price, int side, string kind, double stop)
            => AddEntry(time, price, side, kind, stop, double.NaN);

        private void AddEntry(DateTime time, double price, int side, string kind, double stop, double explicitTarget)
        {
            double risk = Math.Abs(stop - price);

            // Zone entries pass an explicit zone-to-zone target; otherwise use the opposing
            // recent swing control. If that doesn't clear the risk, fall back to a 3R projection.
            double target = !double.IsNaN(explicitTarget) ? explicitTarget
                          : side < 0 ? _ctrlLow : _ctrlHigh;
            double reward = side < 0 ? price - target : target - price;
            if (double.IsNaN(target) || reward <= risk)
            {
                target = side < 0 ? price - 3 * risk : price + 3 * risk;
                reward = 3 * risk;
            }
            double rr = risk > 0 ? reward / risk : 0;

            var entry = new TrendEntry
            {
                Time  = time, Price = price, Side = side, Kind = kind,
                Stop  = stop, Target = target, RR = rr, Trend = CurrentState
            };
            _entries.Add(entry);
            if (_entries.Count > 150) _entries.RemoveAt(0);   // deeper journal history
            _openTrades.Add(entry);   // same object ref — resolving updates the display copy too

            _simTrades.Add(new SimTrade
            {
                EntryTime = time, Entry = price, Side = side, Stop = stop,
                RiskPts = Math.Abs(stop - price),
                TargetR = rr                       // zone-to-zone target (R) — where the runner aims
            });
            if (_simTrades.Count > 3000) _simTrades.RemoveAt(0);
        }

        // Follow each open trade forward, recording MFE (R) until its stop is hit.
        private void UpdateSimTrades(DateTime time, double high, double low)
        {
            foreach (var st in _simTrades)
            {
                if (!st.Open || time <= st.EntryTime || st.RiskPts <= 0) continue;

                // Adverse excursion (intraday heat) — tracked every bar incl. the stop bar.
                double adv  = st.Side > 0 ? (st.Entry - low) : (high - st.Entry);
                double maeR = adv / st.RiskPts;
                if (maeR > st.MaeR) st.MaeR = maeR;

                bool stopped = st.Side > 0 ? low <= st.Stop : high >= st.Stop;
                if (stopped) { st.Stopped = true; st.Open = false; st.StopTime = time; }
                else
                {
                    double fav  = st.Side > 0 ? (high - st.Entry) : (st.Entry - low);
                    double mfeR = fav / st.RiskPts;
                    if (mfeR > st.MfeR) st.MfeR = mfeR;
                    for (int k = 0; k < RrLadder.Length; k++)
                        if (st.RrHit[k] == DateTime.MinValue && st.MfeR >= RrLadder[k]) st.RrHit[k] = time;
                }
            }
        }

        // Resolve open trades against each bar: stop-first (conservative) if both touched.
        private void UpdateEntryOutcomes(DateTime time, double high, double low)
        {
            double third = TpRR / 3.0;
            for (int i = _openTrades.Count - 1; i >= 0; i--)
            {
                var e = _openTrades[i];
                if (time <= e.Time) continue;   // don't judge an entry on its own (or earlier) bar

                double riskPts = Math.Abs(e.Price - e.Stop);
                if (riskPts <= 0) { _openTrades.RemoveAt(i); continue; }

                bool stopped = e.Side < 0 ? high >= e.Stop : low <= e.Stop;
                if (!stopped)   // MFE only accrues on bars that did NOT hit the stop (conservative, same as the sim book)
                {
                    double fav  = e.Side > 0 ? (high - e.Price) : (e.Price - low);
                    double mfeR = fav / riskPts;
                    if (mfeR > e.MfeR) e.MfeR = mfeR;
                }

                // 3-LEG SCALE-OUT — IDENTICAL to the governor's OutcomeAt so chart, trade log and
                // account agree. leg1 banks at +1 third, leg2 at +2 thirds, leg3 (RUNNER) at the
                // zone target; the stop on open legs trails up. ResultR = blended R per contract.
                double runner = e.RR > 0 ? Math.Min(e.RR, 6.0) : TpRR;
                if (runner < 2 * third) runner = 2 * third;
                double m = e.MfeR;
                bool resolved = false; double blended = 0;
                if (m >= runner) { blended = (third + 2 * third + runner) / 3.0; resolved = true; }   // runner hit
                else if (stopped)
                {
                    double l1 = m >= third     ? third     : -1.0;
                    double l2 = m >= 2 * third ? 2 * third : (m >= third ? 0.0 : -1.0);
                    double l3 = m >= 2 * third ? third     : (m >= third ? 0.0 : -1.0);
                    blended = (l1 + l2 + l3) / 3.0; resolved = true;
                }
                if (resolved)
                {
                    e.ResultR = blended;
                    if      (blended >  0.0001) { e.Status = "win";     _wins++;      _totalR += blended; }
                    else if (blended < -0.0001) { e.Status = "loss";    _losses++;    _totalR += blended; }
                    else                        { e.Status = "scratch"; _scratches++; }
                    _tradeRs.Add(blended);
                    if (_tradeRs.Count > 1000) _tradeRs.RemoveAt(0);
                    _openTrades.RemoveAt(i);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private void AddLegPivot(Leg leg)
        {
            // Label relative to the DOMINANT recent swing in the same direction (max of the
            // last few same-direction legs for highs, min for lows) — NOT just the
            // immediately-prior leg. Otherwise a local bounce high that is still below the
            // real swing high gets mislabeled "HH", the structure looks mixed, and the trend
            // reads Flat during an obvious breakdown.
            const int RefLegs = 3;
            double refExtreme = double.NaN;
            int found = 0;
            for (int i = _legs.Count - 2; i >= 0 && found < RefLegs; i--)
            {
                if (_legs[i].Direction != leg.Direction) continue;
                refExtreme = double.IsNaN(refExtreme)
                    ? _legs[i].Extreme
                    : (leg.Direction == 1 ? Math.Max(refExtreme, _legs[i].Extreme)
                                          : Math.Min(refExtreme, _legs[i].Extreme));
                found++;
            }

            string label;
            if (leg.Direction == 1) // bull leg = a swing high
                label = (found == 0 || leg.Extreme > refExtreme) ? "HH" : "LH";
            else                    // bear leg = a swing low
                label = (found == 0 || leg.Extreme < refExtreme) ? "LL" : "HL";

            _pivots.Add(new LegPivot
            {
                Time  = leg.ExtremeTime,
                Price = leg.Extreme,
                Label = label,
                IsBull = leg.Direction == 1,
                BarHigh = leg.BarHigh,
                BarLow  = leg.BarLow
            });
            if (_pivots.Count > 60) _pivots.RemoveAt(0);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mr. Black control points: the controlling level is the MOST RECENT opposing
        // swing pivot — NOT an all-time ratchet. In an uptrend the control is the last
        // Higher-Low (a close below it breaks the trend); in a downtrend it's the last
        // Lower-High (a close above breaks it). Flat is the transition state — no direct
        // Bull<->Bear flip; a trend is entered only on a close THROUGH the swing with
        // confirming HH/HL (bull) or LH/LL (bear) structure.
        private void EvaluateState(double close)
        {
            // Most recent swing high (bull pivot) and swing low (bear pivot).
            LegPivot rHigh = null, rLow = null;
            for (int i = _pivots.Count - 1; i >= 0; i--)
            {
                if (rHigh == null &&  _pivots[i].IsBull) rHigh = _pivots[i];
                if (rLow  == null && !_pivots[i].IsBull) rLow  = _pivots[i];
                if (rHigh != null && rLow != null) break;
            }

            if (rHigh == null || rLow == null)
            {
                _ctrlHigh = rHigh?.Price ?? double.NaN;
                _ctrlLow  = rLow?.Price  ?? double.NaN;
                CurrentState = TrendState.Flat;
                return;
            }

            double recentLH = rHigh.Price;   // most recent swing high
            double recentHL = rLow.Price;    // most recent swing low
            bool bullStruct = rHigh.Label == "HH" && rLow.Label == "HL";
            bool bearStruct = rHigh.Label == "LH" && rLow.Label == "LL";

            switch (CurrentState)
            {
                case TrendState.Bull:
                    if (close < recentHL) CurrentState = TrendState.Flat;  // closed below the HL
                    break;
                case TrendState.Bear:
                    if (close > recentLH) CurrentState = TrendState.Flat;  // closed above the LH
                    break;
                default: // Flat — enter a trend on structure + a close through the swing
                    if      (bullStruct && close > recentLH) CurrentState = TrendState.Bull;
                    else if (bearStruct && close < recentHL) CurrentState = TrendState.Bear;
                    break;
            }

            // Expose the active (recent) control levels for the snapshot and drawing.
            _ctrlHigh = recentLH;
            _ctrlLow  = recentHL;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Outcome of a sim trade at a given take-profit R:R: +rr if it reached the target
        // before the stop, -1 if stopped first, NaN if still open (exclude).
        private static double OutcomeAt(SimTrade st, double rr)
        {
            if (st.MfeR >= rr) return rr;
            if (st.Stopped)    return -1.0;
            return double.NaN;
        }

        private static readonly double[] RrLadder = { 1.0, 1.5, 2.0, 2.5, 3.0, 4.0 };

        // When does this trade free you up to take the next one? Target-hit time (win) or
        // stop time (loss). MaxValue if it never resolves (still open) → blocks nothing real.
        private static DateTime ExitTime(SimTrade st, double rr)
        {
            if (st.MfeR >= rr)
            {
                for (int k = 0; k < RrLadder.Length; k++)
                    if (RrLadder[k] >= rr - 1e-9 && st.RrHit[k] != DateTime.MinValue) return st.RrHit[k];
                if (st.StopTime != DateTime.MinValue) return st.StopTime;   // fallback
            }
            if (st.Stopped) return st.StopTime;
            return DateTime.MaxValue;
        }

        private static int DayKey(DateTime t) => t.Year * 1000 + t.DayOfYear;

        // Single 1-contract account run at the chosen TpRR. END-OF-DAY trailing drawdown:
        // the limit trails the highest END-OF-DAY balance and is only evaluated at EOD, so
        // intraday dips don't blow you. Cost per trade (commission+slippage) subtracted.
        public AccountSimResult RunAccountSim(double pointValue, double ddLimit, double target, double maxRiskDollars, double costPerTrade)
        {
            var r = new AccountSimResult();
            double equity = 0, eodHWM = 0;
            int curDay = -1;
            DateTime freeAt = DateTime.MinValue;
            foreach (var st in _simTrades)
            {
                if (st.EntryTime < freeAt) continue;     // still in a position — one trade at a time
                double oc = OutcomeAt(st, TpRR);
                if (double.IsNaN(oc)) continue;
                double riskDollars = st.RiskPts * pointValue;
                if (riskDollars <= 0) continue;
                if (maxRiskDollars > 0 && riskDollars > maxRiskDollars) { r.Skipped++; continue; }

                int day = DayKey(st.EntryTime);
                if (curDay < 0) curDay = day;
                if (day != curDay)                       // crossed into a new day → EOD of prior day
                {
                    if (equity > eodHWM) eodHWM = equity;
                    double ddd = eodHWM - equity;
                    if (ddd > r.MaxDrawdown) r.MaxDrawdown = ddd;
                    if (ddLimit > 0 && ddd >= ddLimit) { r.Blown = true; r.TradesToResult = r.TradesTaken; break; }
                    curDay = day;
                }

                equity += oc * riskDollars - costPerTrade;
                r.TradesTaken++;
                freeAt = ExitTime(st, TpRR);
                if (target > 0 && equity >= target) { r.Passed = true; r.TradesToResult = r.TradesTaken; break; }
            }
            if (!r.Passed && !r.Blown)
            {
                if (equity > eodHWM) eodHWM = equity;
                double ddd = eodHWM - equity;
                if (ddd > r.MaxDrawdown) r.MaxDrawdown = ddd;
            }
            r.FinalDollars = equity;
            r.PeakDollars  = eodHWM;
            return r;
        }

        public (int passes, int blows, int neither) RunAccountPassRate(
            double pointValue, double ddLimit, double target, double maxRiskDollars, double costPerTrade)
            => PassRateAt(TpRR, pointValue, ddLimit, target, maxRiskDollars, costPerTrade);

        // Robust pass-rate at a given R:R: start a fresh account at EVERY trade; EOD trailing
        // drawdown + cost per trade. The honest "pass a funded account" metric.
        private (int passes, int blows, int neither) PassRateAt(
            double rr, double pointValue, double ddLimit, double target, double maxRiskDollars, double costPerTrade)
        {
            int passes = 0, blows = 0, neither = 0;
            int n = _simTrades.Count;
            for (int start = 0; start < n; start++)
            {
                double equity = 0, eodHWM = 0;
                int curDay = -1;
                bool done = false;
                DateTime freeAt = DateTime.MinValue;
                for (int i = start; i < n; i++)
                {
                    var st = _simTrades[i];
                    if (st.EntryTime < freeAt) continue;     // one trade at a time
                    double oc = OutcomeAt(st, rr);
                    if (double.IsNaN(oc)) continue;
                    double riskD = st.RiskPts * pointValue;
                    if (riskD <= 0) continue;
                    if (maxRiskDollars > 0 && riskD > maxRiskDollars) continue;

                    int day = DayKey(st.EntryTime);
                    if (curDay < 0) curDay = day;
                    if (day != curDay)
                    {
                        if (equity > eodHWM) eodHWM = equity;
                        if (ddLimit > 0 && (eodHWM - equity) >= ddLimit) { blows++; done = true; break; }
                        curDay = day;
                    }

                    equity += oc * riskD - costPerTrade;
                    freeAt = ExitTime(st, rr);
                    if (target > 0 && equity >= target) { passes++; done = true; break; }
                }
                if (!done) neither++;
            }
            return (passes, blows, neither);
        }

        // Full-history stress: run ALL trades (no target stop) at TpRR → worst END-OF-DAY
        // drawdown ever seen + longest losing streak. The honest downside picture.
        public (double worstEodDD, int longestLossStreak, double fullFinal, int trades) RunStress(
            double pointValue, double maxRiskDollars, double costPerTrade)
        {
            double equity = 0, eodHWM = 0, worstDD = 0;
            int curDay = -1, streak = 0, longest = 0, trades = 0;
            DateTime freeAt = DateTime.MinValue;
            foreach (var st in _simTrades)
            {
                if (st.EntryTime < freeAt) continue;     // one trade at a time
                double oc = OutcomeAt(st, TpRR);
                if (double.IsNaN(oc)) continue;
                double riskD = st.RiskPts * pointValue;
                if (riskD <= 0) continue;
                if (maxRiskDollars > 0 && riskD > maxRiskDollars) continue;

                int day = DayKey(st.EntryTime);
                if (curDay < 0) curDay = day;
                if (day != curDay)
                {
                    if (equity > eodHWM) eodHWM = equity;
                    double dd = eodHWM - equity; if (dd > worstDD) worstDD = dd;
                    curDay = day;
                }
                equity += oc * riskD - costPerTrade;
                freeAt = ExitTime(st, TpRR);
                trades++;
                if (oc < 0) { streak++; if (streak > longest) longest = streak; } else streak = 0;
            }
            if (equity > eodHWM) eodHWM = equity;
            double fdd = eodHWM - equity; if (fdd > worstDD) worstDD = fdd;
            return (worstDD, longest, equity, trades);
        }

        // Sweep candidate take-profit R:Rs → (rr, passes, blows, winRate, expectancyR).
        public List<(double rr, int passes, int blows, double winRate, double expR)> RunRRSweep(
            double[] rrs, double pointValue, double ddLimit, double target, double maxRiskDollars, double costPerTrade)
        {
            var outp = new List<(double, int, int, double, double)>();
            if (rrs == null) return outp;
            foreach (var rr in rrs)
            {
                var (p, b, _) = PassRateAt(rr, pointValue, ddLimit, target, maxRiskDollars, costPerTrade);
                int w = 0, l = 0;
                DateTime freeAt = DateTime.MinValue;
                foreach (var st in _simTrades)
                {
                    if (st.EntryTime < freeAt) continue;                  // one trade at a time
                    double oc = OutcomeAt(st, rr);
                    if (double.IsNaN(oc)) continue;
                    double riskD = st.RiskPts * pointValue;
                    if (riskD <= 0 || (maxRiskDollars > 0 && riskD > maxRiskDollars)) continue;
                    if (oc >= 0) w++; else l++;
                    freeAt = ExitTime(st, rr);
                }
                int dec = w + l;
                double wr   = dec > 0 ? (double)w / dec : 0;
                double expR = dec > 0 ? (w * rr - l) / dec : 0;
                outp.Add((rr, p, b, wr, expR));
            }
            return outp;
        }

        public TrendSnapshot GetSnapshot()
        {
            var pivotsCopy = new List<LegPivot>(_pivots);
            return new TrendSnapshot
            {
                State            = CurrentState,
                Lean             = CurrentState != TrendState.Flat ? CurrentState
                                   : _lastActiveTrend == TrendState.Bull ? TrendState.Bear   // uptrend broke down → lean short
                                   : _lastActiveTrend == TrendState.Bear ? TrendState.Bull   // downtrend broke up → lean long
                                   : TrendState.Flat,
                Wins             = _wins,
                Losses           = _losses,
                Scratches        = _scratches,
                TotalR           = _totalR,
                ControllingHigh  = _ctrlHigh,
                ControllingLow   = _ctrlLow,
                LegPivots        = pivotsCopy,
                Entries          = new List<TrendEntry>(_entries),
                LastUpdated      = DateTime.UtcNow,
                ActiveLegDir     = _legDir,
                ActiveLegExtreme = _legExtreme,
                ActiveLegStart   = _legStart,
                LastBarClose     = _lastClose,
                LegsSealed       = _legs.Count,
                PivotsCount      = _pivots.Count
            };
        }

        // The bot's "watchlist" — armed zone setups it's waiting for price to retrace into.
        // Same filters as the live zone entry (aligned + not entered + ≥ ZoneMinRR), minus the
        // rejection (which only confirms at touch). READ-ONLY: does not affect taken trades.
        public List<PendingSetup> GetPendingSetups()
        {
            var list = new List<PendingSetup>();
            if (!UseZoneEntries || _activeZones == null) return list;
            int bias = CurrentState == TrendState.Bull ? 1 : CurrentState == TrendState.Bear ? -1
                     : _lastActiveTrend == TrendState.Bull ? 1 : _lastActiveTrend == TrendState.Bear ? -1 : 0;
            if (bias == 0) return list;
            double buf = Math.Max(2, MinLegTicks) * _tickSizeCache;
            foreach (var z in _activeZones)
            {
                if (z == null || !z.Active) continue;
                if ((bias > 0) != z.IsLong) continue;
                if (_enteredZones.Contains(z.BaseEndTime)) continue;
                int side = z.IsLong ? 1 : -1;
                double edge = z.EntryEdge;
                double stop = z.IsLong ? z.FarWick - buf : z.FarWick + buf;
                double target = FindOpposingZoneTarget(side, edge);
                if (double.IsNaN(target)) continue;
                double risk = Math.Abs(stop - edge), reward = Math.Abs(target - edge);
                if (risk <= 0 || reward / risk < ZoneMinRR) continue;
                list.Add(new PendingSetup
                {
                    Side = side, Entry = edge, Stop = stop, Target = target,
                    RR = reward / risk, Formation = z.Formation, ZoneTime = z.BaseEndTime
                });
            }
            return list;
        }

        // Expose the MFE-based trade book (copy) so the Risk Governor can replay it
        // under hard circuit-breaker rules — identical logic in backtest and live.
        public IReadOnlyList<SimTrade> GetSimTrades() => new List<SimTrade>(_simTrades);

        // The most recent STILL-OPEN sim trade — its live MfeR is the running peak unrealized
        // gain (in R). Lets the governor lock a pass the instant an open winner touches $53k,
        // instead of waiting for the bar to close (and maybe reverse).
        public SimTrade GetLatestOpenTrade()
        {
            SimTrade latest = null;
            foreach (var st in _simTrades)
                if (st.Open && (latest == null || st.EntryTime > latest.EntryTime)) latest = st;
            return latest;
        }

        public bool IsAlignedWithTrend(bool isLongZone)
        {
            if (CurrentState == TrendState.Flat) return false;
            return isLongZone ? CurrentState == TrendState.Bull : CurrentState == TrendState.Bear;
        }

        public void Reset()
        {
            _legs.Clear();
            _pivots.Clear();
            _legDir         = 0;
            _legExtreme     = double.NaN;
            _legStart       = double.NaN;
            _lastClose      = double.NaN;
            _legBarHigh     = double.NaN;
            _legBarLow      = double.NaN;
            _ctrlHigh       = double.NaN;
            _ctrlLow        = double.NaN;
            CurrentState    = TrendState.Flat;
            _lastActiveTrend   = TrendState.Flat;
            _entryPrevState    = TrendState.Flat;
            _entryLastPivotTime = DateTime.MinValue;
            _entries.Clear();
            _openTrades.Clear();
            _simTrades.Clear();
            _enteredZones.Clear();
            _wins = 0; _losses = 0; _scratches = 0; _totalR = 0; _tradeRs.Clear();
        }
    }
}
