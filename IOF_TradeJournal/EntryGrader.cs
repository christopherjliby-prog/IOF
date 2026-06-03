// =============================================================================
// EntryGrader.cs — IOF entry quality grade computation (A / B / C / D / F)
// =============================================================================
// Grade rubric (from IOF Trade Journal spec):
//   A  — Inside zone + MTFC + HVN + D≥2× + ABS≥2×   (all 4 confluence factors)
//   B  — Inside zone + any 2 confluence factors
//   C  — Inside zone + any 1 confluence factor
//   D  — Inside zone, zero confluence
//   F  — Entry outside zone (chased)
//
// Phase 1: uses zone proximity data only (no metrics → grade based on inside/outside).
// Phase 2: full rubric when DepartureMultiplier / AbsorptionMultiplier / MtfcBonus /
//          HvnConfluence are populated from ZoneMetricsRegistry.
// =============================================================================

namespace TradePhantoms.Journal
{
    public static class EntryGrader
    {
        public static string Grade(JournalEntry e)
        {
            // Phase 2+ data available — full rubric
            if (e.DepartureMultiplier > 0 || e.AbsorptionMultiplier > 0
                || e.MtfcBonus > 0 || e.HvnConfluence)
            {
                return FullGrade(e);
            }

            // Phase 1 fallback — only know inside/outside zone
            if (!e.EntryInsideZone)
                return "F";

            // Inside zone but no metrics yet → use "?" to signal incomplete grading
            // so the user knows Phase 2 integration is needed for full grades.
            return string.IsNullOrEmpty(e.NearestZoneType) ? "F" : "?";
        }

        private static string FullGrade(JournalEntry e)
        {
            if (!e.EntryInsideZone) return "F";

            int confluence = 0;
            bool hasD   = e.DepartureMultiplier  >= 2.0;
            bool hasAbs = e.AbsorptionMultiplier  >= 2.0;
            bool hasMtfc = e.MtfcBonus > 0;
            bool hasHvn  = e.HvnConfluence;

            if (hasD)    confluence++;
            if (hasAbs)  confluence++;
            if (hasMtfc) confluence++;
            if (hasHvn)  confluence++;

            if (confluence >= 4) return "A";
            if (confluence >= 2) return "B";
            if (confluence >= 1) return "C";
            return "D";
        }
    }
}
