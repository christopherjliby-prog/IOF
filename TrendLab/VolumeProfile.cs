// VolumeProfile.cs — Self-contained volume-profile (VAH/VAL/POC) computation.
//
// Builds a price-bucketed volume histogram from the chart's HistoricalData over a
// lookback window, then derives:
//   POC = price bucket with the most volume
//   VA  = standard 70% value area expanded outward from the POC (TPO-style:
//         repeatedly add the heavier of the two buckets above vs the two below).
// Intra-bar tick distribution isn't available, so each bar's volume is spread
// evenly across the buckets it spans (low..high) — the usual profile approximation.

using System;
using System.Collections.Generic;
using TradingPlatform.BusinessLayer;

namespace TradePhantomsIOF.Trend
{
    public struct VaResult
    {
        public bool   Valid;
        public double Poc;
        public double Vah;
        public double Val;
        public double TotalVolume;
        public int    BarsUsed;
    }

    public static class VolumeProfile
    {
        // Guard against pathological bar ranges blowing up the bucket loop.
        private const int MaxBucketsPerBar = 20000;

        public static VaResult ComputeProfile(
            HistoricalData hd, int lookbackBars, double tickSize, int bucketTicks, double vaPercent)
        {
            var result = new VaResult { Valid = false };

            if (hd == null || hd.Count < 2) return result;
            if (tickSize <= 0) tickSize = 0.25;
            if (bucketTicks < 1) bucketTicks = 1;
            if (vaPercent <= 0 || vaPercent >= 1) vaPercent = 0.70;

            double bucketSize = tickSize * bucketTicks;

            int count = hd.Count;
            if (count < 2) return result;
            if (lookbackBars < 1) lookbackBars = 1;

            // Quantower's live HistoricalData is indexed newest-first; older feeds may be
            // oldest-first. Detect direction so we always profile the most RECENT closed bars.
            var firstB = hd[0]         as HistoryItemBar;
            var lastB  = hd[count - 1] as HistoryItemBar;
            bool newestFirst = firstB != null && lastB != null
                               && firstB.TimeLeft > lastB.TimeLeft;

            // Build the list of closed-bar indices, most-recent first, capped at lookbackBars.
            var idxs = new List<int>(lookbackBars);
            if (newestFirst)
            {
                // index 0 = forming bar; closed bars are 1..count-1
                for (int i = 1; i < count && idxs.Count < lookbackBars; i++) idxs.Add(i);
            }
            else
            {
                // index count-1 = forming bar; closed bars are count-2..0
                for (int i = count - 2; i >= 0 && idxs.Count < lookbackBars; i--) idxs.Add(i);
            }

            var vol = new Dictionary<long, double>();
            long minBucket = long.MaxValue, maxBucket = long.MinValue;
            double total = 0;
            int used = 0;

            foreach (int i in idxs)
            {
                var bar = hd[i] as HistoryItemBar;
                if (bar == null) continue;

                double v = bar.Volume;
                if (v <= 0) { used++; continue; }

                long bLow  = (long)Math.Floor(bar.Low  / bucketSize);
                long bHigh = (long)Math.Floor(bar.High / bucketSize);
                if (bHigh < bLow) { var t = bLow; bLow = bHigh; bHigh = t; }

                int span = (int)(bHigh - bLow) + 1;
                if (span < 1) span = 1;
                if (span > MaxBucketsPerBar) span = MaxBucketsPerBar;

                double volPer = v / span;
                long bEnd = bLow + span - 1;
                for (long b = bLow; b <= bEnd; b++)
                {
                    vol.TryGetValue(b, out double cur);
                    vol[b] = cur + volPer;
                }

                if (bLow  < minBucket) minBucket = bLow;
                if (bEnd  > maxBucket) maxBucket = bEnd;
                total += v;
                used++;
            }

            result.BarsUsed = used;
            if (total <= 0 || vol.Count == 0) return result;

            // POC = heaviest bucket
            long pocBucket = 0;
            double pocVol = -1;
            foreach (var kv in vol)
            {
                if (kv.Value > pocVol) { pocVol = kv.Value; pocBucket = kv.Key; }
            }

            // Expand value area outward from POC until >= vaPercent of total volume.
            double target = total * vaPercent;
            double acc    = pocVol;
            long lowIdx   = pocBucket;
            long highIdx  = pocBucket;

            while (acc < target && (lowIdx > minBucket || highIdx < maxBucket))
            {
                double volAbove = VolAt(vol, highIdx + 1) + VolAt(vol, highIdx + 2);
                double volBelow = VolAt(vol, lowIdx - 1) + VolAt(vol, lowIdx - 2);

                bool canGoUp   = highIdx < maxBucket;
                bool canGoDown = lowIdx  > minBucket;

                if (canGoUp && (!canGoDown || volAbove >= volBelow))
                {
                    acc += volAbove;
                    highIdx += 2;
                }
                else if (canGoDown)
                {
                    acc += volBelow;
                    lowIdx -= 2;
                }
                else break;
            }

            // VAH = top edge of highest VA bucket, VAL = bottom edge of lowest.
            result.Poc         = RoundToTick((pocBucket + 0.5) * bucketSize, tickSize);
            result.Vah         = RoundToTick((highIdx + 1)     * bucketSize, tickSize);
            result.Val         = RoundToTick(lowIdx            * bucketSize, tickSize);
            result.TotalVolume = total;
            result.Valid       = true;
            return result;
        }

        private static double VolAt(Dictionary<long, double> vol, long idx)
        {
            vol.TryGetValue(idx, out double v);
            return v;
        }

        private static double RoundToTick(double price, double tickSize)
        {
            if (tickSize <= 0) return price;
            return Math.Round(price / tickSize) * tickSize;
        }
    }
}
