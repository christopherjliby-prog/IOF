// =====================================================================================
// SignalEngine.cs — Order-flow entry doctrine (ORDERFLOW_SPEC.md, 16 tools) as code.
// =====================================================================================
// This encodes the ENTRY/EXIT decision doctrine from the on-disk bot-behaviour spec
// (Drive: ORDERFLOW_SPEC.md, council-validated 2026-07-02). It does NOT place orders —
// it returns a Signal (or None). IOF_PropEvalBot.cs owns execution and, crucially,
// routes every signal through the RiskGovernor before a single contract is sent.
//
// THE SPEC'S CORE DOCTRINE (faithfully implemented as the gate order below):
//   1. CONTEXT   — HTF bias / regime. "Without a level, order flow is just noise."
//   2. LOCATION  — price must be AT a marked VP level (POC / HVN edge / LVN edge / VA edge).
//   3. CONFIRM   — order-flow reads at that level, requiring >= 2 INDEPENDENT axes.
//   + cbrackn's 3 HARD VETOES: (M1) no key level -> no trade; (M2) no opposing
//     aggression / no trigger -> no trade; (M3) insignificant delta/volume -> no trade.
//   + Entry = a RESTING LIMIT at the level (no-chase, Tool 15/#19).
//   + Stop  = beyond the cluster / HVN far edge. Target = before the next barrier.
//   + 3R GATE IS UNCONDITIONAL (Tools 12/13). Fat-zone (>25pt) reject.
//   + Trigger = delta-flip + reclaim/hold. NEVER fire on the absorption bar (Tool 7).
//   + Dedup: {absorption, delta-div, CVD-div} = ONE axis; finished-auction = independent;
//     delta-flip = the trigger axis (Tools 7/14).
//
// CALIBRATION HONESTY (verbatim to the spec's own stance):
//   Every numeric threshold here (imbalance ratio, absorption significance, finished
//   cutoff, burst size, edge tolerance, ...) is an MNQ PLACEHOLDER. The spec says
//   repeatedly: "validate on the 5yr footprint before live." These are exposed as
//   parameters and MUST be calibrated on real MNQ footprint data before the signal
//   side is trusted with real money. The RiskGovernor is what makes the bot SAFE in
//   the meantime; the SignalEngine is what makes it trade to the doctrine.
//
// DATA REQUIREMENTS: needs per-bar footprint (per-price bid/ask volume + delta). In
// Quantower this comes from IVolumeAnalysisIndicator / VolumeAnalysisData on the bars
// (the existing indicator already implements that interface). The strategy feeds those
// structures in via the FootprintBar/ProfileLevel DTOs below so this file stays
// SDK-agnostic and unit-testable.
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace TradePhantomsIOF.Signals
{
    public enum Side { None = 0, Long = 1, Short = -1 }

    /// <summary>One price level of a footprint candle: aggressive buy vs sell volume.</summary>
    public struct FootprintCell
    {
        public double Price;
        public double Buy;    // ask-aggressive (lifted the ask)
        public double Sell;   // bid-aggressive (hit the bid)
        public double Total => Buy + Sell;
        public double Delta => Buy - Sell;
    }

    /// <summary>A completed footprint candle (ascending cells) + aggregates.</summary>
    public sealed class FootprintBar
    {
        public DateTime Time;
        public double Open, High, Low, Close;
        public List<FootprintCell> Cells = new List<FootprintCell>(); // ascending by price
        public double BarDelta => Cells.Sum(c => c.Delta);
        public double BarVolume => Cells.Sum(c => c.Total);
        public FootprintCell ExtremeHigh => Cells[Cells.Count - 1];
        public FootprintCell ExtremeLow => Cells[0];
    }

    /// <summary>A volume-profile location leg (POC / HVN edge / LVN edge / VA edge).</summary>
    public sealed class ProfileLevel
    {
        public enum Kind { POC, HVN_NearEdge, LVN_Edge, VAH, VAL }
        public Kind Type;
        public double Price;
        public double NearEdge;   // directional band edge the entry leans on
        public double FarEdge;    // where the protective stop sits beyond
        public bool Untested;     // first-touch gating (naked/untested only)
        public bool Fixed;        // developing levels are gated OUT of entries
    }

    public sealed class SignalConfig
    {
        // --- Tool 8/9: imbalance (RESOLVED diagonal). MNQ placeholder ~4:1. ---
        public double ImbalanceRatio = 4.0;
        public double ImbalanceMinFloor = 20;      // winning-operand floor (contracts)
        public int    StackedMinRun = 3;           // Tool 9: >=3 consecutive same-side

        // --- Tool 7/14: absorption significance = |sum delta| / volume-at-price. ---
        public double AbsorptionSignificance = 0.30;  // ~0.25-0.33 band; recalibrate MNQ
        public double PriceHeldToleranceTicks = 3;     // "price failed to progress" tolerance

        // --- Tool 6: finished auction (one side dries up at the extreme). ---
        public double FinishedSidePctOfExtreme = 0.12; // min(bid,ask) <= X% * extremeVol
        public double FinishedAbsFloor = 30;           // extreme cell must be >= this

        // --- Tool 16: aggregated-urgency burst (algo-split defence). ---
        public double BurstContracts = 40;             // >=V same-side within W (placeholder)

        // --- Location / geometry. ---
        public double EdgeToleranceTicks = 4;          // "at-edge" predicate tolerance
        public double TickSize = 0.25;
        public double PointValue = 2.0;

        // --- Gates. ---
        public int    MinIndependentReads = 2;         // spec: >= 2 independent axes
        public double MinRRR = 3.0;                     // UNCONDITIONAL 3R gate
        public double FatZoneMaxPoints = 25.0;          // >25pt structural-stop reject
        public bool   RequireHTFAlignment = true;       // HTF-is-king
        public int    StopBufferTicks = 2;
    }

    /// <summary>A fully-formed trade proposal (still subject to the RiskGovernor).</summary>
    public sealed class Signal
    {
        public Side Side;
        public double EntryLimit;   // resting limit price at the level (no chase)
        public double StopPrice;    // beyond the cluster / HVN far edge
        public double TargetPrice;  // before the next barrier
        public double SlDistancePrice => Math.Abs(EntryLimit - StopPrice);
        public double Rrr;
        public List<string> Reads = new List<string>();  // which independent axes fired
        public string LocationDesc = "";
    }

    /// <summary>
    /// Stateless-per-call evaluator. Holds only the short rolling history it needs for
    /// cross-bar reads (delta-flip, cluster absorption). Feed it closed footprint bars,
    /// the current VP location legs, and the HTF bias; it returns a Signal or null.
    /// </summary>
    public sealed class SignalEngine
    {
        private readonly SignalConfig _cfg;
        private readonly List<FootprintBar> _recent = new List<FootprintBar>(); // rolling window
        private const int Window = 60;

        public SignalEngine(SignalConfig cfg) { _cfg = cfg ?? new SignalConfig(); }

        public void PushBar(FootprintBar bar)
        {
            _recent.Add(bar);
            if (_recent.Count > Window) _recent.RemoveAt(0);
        }

        // -----------------------------------------------------------------------------
        // Tool 8 — IMBALANCE (resolved diagonal): BUY = Buy[i] >= r*Sell[i-1];
        //          SELL = Sell[i] >= r*Buy[i+1]. Floor on the WINNING operand only.
        // -----------------------------------------------------------------------------
        private bool CellBuyImbalance(FootprintBar b, int i)
        {
            if (i < 1) return false;
            double buy = b.Cells[i].Buy, sellBelow = b.Cells[i - 1].Sell;
            if (buy < _cfg.ImbalanceMinFloor) return false;              // winning-operand floor
            return sellBelow <= 0 ? false : buy >= _cfg.ImbalanceRatio * sellBelow;
        }
        private bool CellSellImbalance(FootprintBar b, int i)
        {
            if (i >= b.Cells.Count - 1) return false;
            double sell = b.Cells[i].Sell, buyAbove = b.Cells[i + 1].Buy;
            if (sell < _cfg.ImbalanceMinFloor) return false;
            return buyAbove <= 0 ? false : sell >= _cfg.ImbalanceRatio * buyAbove;
        }

        // -----------------------------------------------------------------------------
        // Tool 9 — STACKED IMBALANCES: >=N consecutive same-side imbalanced cells.
        //          A gap resets the run. Maximal-run dedup (one stack, not N singles).
        // -----------------------------------------------------------------------------
        private bool HasStackedImbalance(FootprintBar b, Side side)
        {
            int run = 0;
            for (int i = 0; i < b.Cells.Count; i++)
            {
                bool imb = side == Side.Long ? CellBuyImbalance(b, i) : CellSellImbalance(b, i);
                run = imb ? run + 1 : 0;
                if (run >= _cfg.StackedMinRun) return true;
            }
            return false;
        }

        // -----------------------------------------------------------------------------
        // Tool 7/14 — ABSORPTION: heavy one-sided delta at a level but price does NOT
        //          progress in the delta's direction. significance = |sumΔ|/volume.
        //          This is a STATE (arms), never a fill on the absorption bar.
        // -----------------------------------------------------------------------------
        private bool IsAbsorption(FootprintBar b, Side expectedTrapSide)
        {
            double vol = b.BarVolume;
            if (vol <= 0) return false;
            double sig = Math.Abs(b.BarDelta) / vol;
            if (sig < _cfg.AbsorptionSignificance) return false;         // M3 veto: insignificant

            double range = b.High - b.Low;
            if (range <= 0) return false;
            // "Effort with no reward": significant one-sided aggression, but price closed
            // AWAY from that side's direction (the aggressors were not paid).
            // Placeholder close-location fraction (upper/lower 40%); MNQ-calibrate.
            const double closeFrac = 0.60;
            if (expectedTrapSide == Side.Short)
                // heavy SELLING (-delta) yet the bar CLOSED in its upper 40% -> sellers
                // trapped, the low held -> long setup.
                return b.BarDelta < 0 && (b.Close - b.Low) >= closeFrac * range;
            if (expectedTrapSide == Side.Long)
                // heavy BUYING (+delta) yet the bar CLOSED in its lower 40% -> buyers trapped.
                return b.BarDelta > 0 && (b.High - b.Close) >= closeFrac * range;
            return false;
        }

        // -----------------------------------------------------------------------------
        // Tool 6 — FINISHED AUCTION: at the extreme, ONE side drops to ~zero.
        // -----------------------------------------------------------------------------
        private bool FinishedAtExtreme(FootprintBar b, bool atHigh)
        {
            var cell = atHigh ? b.ExtremeHigh : b.ExtremeLow;
            if (cell.Total < _cfg.FinishedAbsFloor) return false;       // absolute floor mandatory
            double weak = Math.Min(cell.Buy, cell.Sell);
            return weak <= _cfg.FinishedSidePctOfExtreme * cell.Total;
        }

        // -----------------------------------------------------------------------------
        // Tool 2/16 — CROSS-BAR DELTA FLIP: sign(BarDelta) reversal that is significant.
        //          This is the TRIGGER axis (separate from the arming reads).
        // -----------------------------------------------------------------------------
        private bool DeltaFlip(Side toSide)
        {
            if (_recent.Count < 2) return false;
            var cur = _recent[_recent.Count - 1];
            var prev = _recent[_recent.Count - 2];
            // significance: |cur delta| is an outlier vs the rolling window
            var deltas = _recent.Select(x => Math.Abs(x.BarDelta)).ToList();
            double mean = deltas.Average();
            double std = Math.Sqrt(deltas.Select(d => (d - mean) * (d - mean)).Average()) + 1e-9;
            double z = (Math.Abs(cur.BarDelta) - mean) / std;
            bool significant = z >= 1.5;
            if (toSide == Side.Long)
                return prev.BarDelta < 0 && cur.BarDelta > 0 && significant;
            if (toSide == Side.Short)
                return prev.BarDelta > 0 && cur.BarDelta < 0 && significant;
            return false;
        }

        // -----------------------------------------------------------------------------
        // The AND-gate: context -> location -> confirmation (>=2 independent reads).
        // Returns a Signal or null. This is where the whole doctrine composes.
        // -----------------------------------------------------------------------------
        public Signal Evaluate(Side htfBias, IReadOnlyList<ProfileLevel> locations, double lastPrice)
        {
            if (_recent.Count == 0) return null;
            var bar = _recent[_recent.Count - 1];

            // ---- (M1) LOCATION veto: must be AT an untested, fixed VP level. ----
            double edgeTol = _cfg.EdgeToleranceTicks * _cfg.TickSize;
            ProfileLevel loc = locations?.FirstOrDefault(l =>
                l.Fixed && l.Untested && Math.Abs(lastPrice - l.NearEdge) <= edgeTol);
            if (loc == null) return null;   // "Without a level, order flow is just noise."

            // Determine the side we'd trade at this level given the HTF bias.
            // Reversal at a counter-trend extreme, or continuation with-trend at a held edge.
            Side side = InferSide(htfBias, loc, bar);
            if (side == Side.None) return null;

            // ---- CONTEXT veto: HTF-is-king. ----
            if (_cfg.RequireHTFAlignment && htfBias != Side.None && side != htfBias)
            {
                // Only allow a counter-HTF trade if it is an EXPLICIT reversal read at an
                // extreme (finished auction + absorption) — otherwise skip (HTF wins).
                bool strongReversal = FinishedAtExtreme(bar, side == Side.Short) &&
                                      IsAbsorption(bar, Opposite(side));
                if (!strongReversal) return null;
            }

            // ---- CONFIRMATION: collect INDEPENDENT axes (with dedup). ----
            // Axis A = effort/absorption group {absorption, delta-div, CVD-div} -> ONE vote.
            // Axis B = finished-auction (independent).
            // Axis C = stacked/single imbalance (aggression axis) -> ONE vote.
            // Trigger = delta-flip (separate; required, not counted as an arming read).
            var reads = new List<string>();
            bool absorption = IsAbsorption(bar, Opposite(side));       // trapped side = opposite
            if (absorption) reads.Add("absorption");
            if (FinishedAtExtreme(bar, side == Side.Short)) reads.Add("finished-auction");
            if (HasStackedImbalance(bar, side)) reads.Add("stacked-imbalance");

            // ---- (M2) TRIGGER veto: need the opposing-aggression takeover (the flip). ----
            bool trigger = DeltaFlip(side);
            if (!trigger) return null;   // never fade delta on sight; wait for the takeover

            if (reads.Count < _cfg.MinIndependentReads) return null;   // need >=2 independent arms

            // ---- Build the plan: resting limit at the level, stop beyond far edge. ----
            double buffer = _cfg.StopBufferTicks * _cfg.TickSize;
            double entry = loc.NearEdge;                               // no-chase resting limit
            double stop = side == Side.Long ? loc.FarEdge - buffer : loc.FarEdge + buffer;
            if (side == Side.Long && stop >= entry) stop = entry - buffer * 2;
            if (side == Side.Short && stop <= entry) stop = entry + buffer * 2;

            double slDist = Math.Abs(entry - stop);
            if (slDist <= 0) return null;

            // ---- Target = before the next barrier in the path (fallback: 3R). ----
            double target = NextBarrierTarget(side, entry, locations) ??
                            (side == Side.Long ? entry + _cfg.MinRRR * slDist
                                               : entry - _cfg.MinRRR * slDist);
            double rrr = Math.Abs(target - entry) / slDist;

            // ---- UNCONDITIONAL gates: 3R + fat-zone reject. ----
            if (rrr < _cfg.MinRRR) return null;
            if (slDist > _cfg.FatZoneMaxPoints) return null;

            return new Signal
            {
                Side = side, EntryLimit = entry, StopPrice = stop, TargetPrice = target,
                Rrr = rrr, Reads = reads, LocationDesc = loc.Type.ToString()
            };
        }

        private Side InferSide(Side htfBias, ProfileLevel loc, FootprintBar bar)
        {
            // At an untested near-edge, we react AWAY from the node. Approaching from below
            // (price <= nearEdge on a support-type level) -> long; from above -> short.
            // Use the bar's own extreme relative to the level as the approach tell.
            if (bar.Low <= loc.NearEdge && bar.Close >= loc.NearEdge) return Side.Long;
            if (bar.High >= loc.NearEdge && bar.Close <= loc.NearEdge) return Side.Short;
            // Fallback to HTF bias if the approach is ambiguous.
            return htfBias;
        }

        private double? NextBarrierTarget(Side side, double entry, IReadOnlyList<ProfileLevel> locations)
        {
            if (locations == null) return null;
            double buffer = _cfg.EdgeToleranceTicks * _cfg.TickSize;
            if (side == Side.Long)
            {
                var above = locations.Where(l => l.Fixed && l.NearEdge > entry)
                                     .OrderBy(l => l.NearEdge).FirstOrDefault();
                return above == null ? (double?)null : above.NearEdge - buffer; // before the barrier
            }
            else
            {
                var below = locations.Where(l => l.Fixed && l.NearEdge < entry)
                                     .OrderByDescending(l => l.NearEdge).FirstOrDefault();
                return below == null ? (double?)null : below.NearEdge + buffer;
            }
        }

        private static Side Opposite(Side s) => s == Side.Long ? Side.Short : (s == Side.Short ? Side.Long : Side.None);
    }
}
