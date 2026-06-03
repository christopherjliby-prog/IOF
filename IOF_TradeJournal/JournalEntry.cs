// =============================================================================
// JournalEntry.cs — Trade data model for the IOF Trade Journal
// =============================================================================
// All fields captured per trade across all four build phases.
// Phase 1 populates: identity + execution fields only.
// Phase 2 adds: zone context.
// Phase 3 adds: environment + MAE/MFE.
// Phase 4 adds: HTML dashboard generation.
// =============================================================================

using System;
using System.Text;

namespace TradePhantoms.Journal
{
    public class JournalEntry
    {
        // ── Phase 1: Identity ─────────────────────────────────────────────────

        public string TradeId             { get; set; } = "";
        public DateTime Date              { get; set; }
        public DateTime EntryTime         { get; set; }
        public DateTime ExitTime          { get; set; }
        public int HoldTimeSeconds        { get; set; }
        public string HoldTimeFormatted   { get; set; } = "";
        public string Session             { get; set; } = "";

        // ── Phase 1: Execution ────────────────────────────────────────────────

        public string Symbol              { get; set; } = "";
        public string Direction           { get; set; } = "";   // "LONG" | "SHORT"
        public int Contracts              { get; set; }
        public double EntryPrice          { get; set; }
        public double ExitPrice           { get; set; }
        public double GrossPnL            { get; set; }
        public double Commission          { get; set; }
        public double NetPnL              { get; set; }
        public string ExitReason          { get; set; } = "";   // "TP" | "SL" | "BE" | "MANUAL"
        public double RMultiple           { get; set; }

        // ── Phase 2: IOF Zone Context ─────────────────────────────────────────

        public string NearestZoneType               { get; set; } = "";  // "RBR" | "DBR" | "DBD" | "RBD"
        public double NearestZoneTop                { get; set; }
        public double NearestZoneBottom             { get; set; }
        public double ZoneScore                     { get; set; }
        public int    ZoneScoreMax                  { get; set; } = 21;
        public bool   EntryInsideZone               { get; set; }
        public int    EntryDistanceFromZoneEdgeTicks { get; set; }
        public double DepartureMultiplier           { get; set; }
        public double AbsorptionMultiplier          { get; set; }
        public double MtfcBonus                     { get; set; }
        public bool   HvnConfluence                 { get; set; }
        public int    ZoneTouchCountAtEntry          { get; set; }
        public string ZonePurityAtEntry             { get; set; } = "";  // "FRESH" | "TESTED" | "DEGRADED"
        public string ZoneTimeframe                 { get; set; } = "";  // "5m"

        // ── Phase 2: Entry Quality Grade ─────────────────────────────────────

        public string EntryGrade { get; set; } = "";   // "A" | "B" | "C" | "D" | "F"

        // ── Phase 3: Environment ──────────────────────────────────────────────

        public string HtfTrend              { get; set; } = "";  // "BULL" | "BEAR" | "FLAT"
        public string ItfTrend              { get; set; } = "";
        public bool   TradeWithTrend        { get; set; }
        public int    NearestHtfZoneDistance { get; set; }
        public double BarAtr20              { get; set; }
        public string TimeOfDayBucket       { get; set; } = "";  // "0930-1000"

        // ── Phase 3: Exit Analysis ────────────────────────────────────────────

        public bool   HitTp1                  { get; set; }
        public bool   HitTp2                  { get; set; }
        public bool   HitTp3                  { get; set; }
        public bool   EarlyExit               { get; set; }
        public bool   StopMoved               { get; set; }
        public double MaxAdverseExcursion     { get; set; }  // points against position
        public double MaxFavorableExcursion   { get; set; }  // points in favor

        // ── Internal tracking (not written to log) ────────────────────────────

        public bool   IsComplete              { get; set; }
        public string PositionId             { get; set; } = "";

        // ── Helpers ───────────────────────────────────────────────────────────

        public static string FormatHoldTime(int seconds)
        {
            if (seconds < 60) return $"{seconds}s";
            int m = seconds / 60;
            int s = seconds % 60;
            if (m < 60) return $"{m}m {s:D2}s";
            int h = m / 60;
            m %= 60;
            return $"{h}h {m:D2}m";
        }

        public string ToCsvRow()
        {
            return string.Join(",",
                CsvEscape(TradeId),
                Date.ToString("yyyy-MM-dd"),
                EntryTime.ToString("HH:mm:ss.fff"),
                ExitTime  == default ? "" : ExitTime.ToString("HH:mm:ss.fff"),
                HoldTimeSeconds,
                CsvEscape(HoldTimeFormatted),
                CsvEscape(Session),
                CsvEscape(Symbol),
                CsvEscape(Direction),
                Contracts,
                EntryPrice.ToString("F4"),
                ExitPrice.ToString("F4"),
                GrossPnL.ToString("F2"),
                Commission.ToString("F2"),
                NetPnL.ToString("F2"),
                CsvEscape(ExitReason),
                RMultiple.ToString("F2"),
                CsvEscape(EntryGrade),
                CsvEscape(NearestZoneType),
                NearestZoneTop.ToString("F4"),
                NearestZoneBottom.ToString("F4"),
                ZoneScore.ToString("F1"),
                ZoneScoreMax,
                EntryInsideZone ? "1" : "0",
                EntryDistanceFromZoneEdgeTicks,
                DepartureMultiplier.ToString("F2"),
                AbsorptionMultiplier.ToString("F2"),
                MtfcBonus.ToString("F1"),
                HvnConfluence ? "1" : "0",
                ZoneTouchCountAtEntry,
                CsvEscape(ZonePurityAtEntry),
                CsvEscape(ZoneTimeframe),
                CsvEscape(HtfTrend),
                CsvEscape(ItfTrend),
                TradeWithTrend ? "1" : "0",
                NearestHtfZoneDistance,
                BarAtr20.ToString("F2"),
                CsvEscape(TimeOfDayBucket),
                HitTp1 ? "1" : "0",
                HitTp2 ? "1" : "0",
                HitTp3 ? "1" : "0",
                EarlyExit ? "1" : "0",
                StopMoved ? "1" : "0",
                MaxAdverseExcursion.ToString("F2"),
                MaxFavorableExcursion.ToString("F2")
            );
        }

        public static string CsvHeader =>
            "TradeId,Date,EntryTime,ExitTime,HoldTimeSeconds,HoldTimeFormatted,Session," +
            "Symbol,Direction,Contracts,EntryPrice,ExitPrice,GrossPnL,Commission,NetPnL," +
            "ExitReason,RMultiple,EntryGrade," +
            "NearestZoneType,NearestZoneTop,NearestZoneBottom,ZoneScore,ZoneScoreMax," +
            "EntryInsideZone,EntryDistanceFromZoneEdgeTicks,DepartureMultiplier," +
            "AbsorptionMultiplier,MtfcBonus,HvnConfluence,ZoneTouchCountAtEntry," +
            "ZonePurityAtEntry,ZoneTimeframe," +
            "HtfTrend,ItfTrend,TradeWithTrend,NearestHtfZoneDistance,BarAtr20,TimeOfDayBucket," +
            "HitTp1,HitTp2,HitTp3,EarlyExit,StopMoved,MaxAdverseExcursion,MaxFavorableExcursion";

        private static string CsvEscape(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }
    }
}
