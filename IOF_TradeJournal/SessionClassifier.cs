// =============================================================================
// SessionClassifier.cs — Time-of-day session detection (Eastern time)
// =============================================================================
// Sessions mapped to Eastern Time (auto-adjusts for DST via Windows TZ).
//   LONDON    : 03:00 – 08:30 ET
//   NY_OPEN   : 08:30 – 10:30 ET  (high-probability IOF window)
//   NY_MID    : 10:30 – 14:00 ET
//   NY_CLOSE  : 14:00 – 16:00 ET
//   OVERNIGHT : everything else
// =============================================================================

using System;

namespace TradePhantoms.Journal
{
    public static class SessionClassifier
    {
        private static readonly TimeZoneInfo _et;

        static SessionClassifier()
        {
            try
            {
                // Windows ID
                _et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            catch
            {
                try
                {
                    // Linux/macOS IANA ID
                    _et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                }
                catch
                {
                    _et = TimeZoneInfo.Utc;
                }
            }
        }

        public static string Classify(DateTime utcTime)
        {
            TimeSpan t;
            try { t = TimeZoneInfo.ConvertTimeFromUtc(utcTime, _et).TimeOfDay; }
            catch { t = utcTime.TimeOfDay; }

            if (t >= new TimeSpan(3, 0, 0) && t < new TimeSpan(8, 30, 0))
                return "LONDON";
            if (t >= new TimeSpan(8, 30, 0) && t < new TimeSpan(10, 30, 0))
                return "NY_OPEN";
            if (t >= new TimeSpan(10, 30, 0) && t < new TimeSpan(14, 0, 0))
                return "NY_MID";
            if (t >= new TimeSpan(14, 0, 0) && t < new TimeSpan(16, 0, 0))
                return "NY_CLOSE";
            return "OVERNIGHT";
        }

        /// <summary>Returns "0930-1000" style 30-minute bucket in Eastern time.</summary>
        public static string GetTimeBucket(DateTime utcTime)
        {
            DateTime et;
            try { et = TimeZoneInfo.ConvertTimeFromUtc(utcTime, _et); }
            catch { et = utcTime; }

            int h   = et.Hour;
            int m   = (et.Minute / 30) * 30;   // round down to :00 or :30
            int endM = m + 30;
            int endH = h;
            if (endM >= 60) { endM -= 60; endH++; }
            return $"{h:D2}{m:D2}-{endH:D2}{endM:D2}";
        }
    }
}
