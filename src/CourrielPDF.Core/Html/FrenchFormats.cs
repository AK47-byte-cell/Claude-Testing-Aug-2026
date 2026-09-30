using System;
using System.Globalization;

namespace CourrielPDF.Core.Html
{
    /// <summary>Québec French formatting helpers (dates, sizes).</summary>
    public static class FrenchFormats
    {
        public static readonly CultureInfo Culture = GetCulture();

        private static CultureInfo GetCulture()
        {
            try { return CultureInfo.GetCultureInfo("fr-CA"); }
            catch { return CultureInfo.InvariantCulture; }
        }

        /// <summary>"mardi 2 septembre 2026 14 h 35" in the machine's local time zone.</summary>
        public static string LongDateTime(DateTimeOffset d)
        {
            var l = d.ToLocalTime();
            var date = l.ToString("dddd d MMMM yyyy", Culture);
            if (Culture.Equals(CultureInfo.InvariantCulture))
                date = l.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return date + " " + l.ToString("HH", CultureInfo.InvariantCulture) + " h " + l.ToString("mm", CultureInfo.InvariantCulture);
        }

        /// <summary>"2026-09-02 14h35", safe for file names.</summary>
        public static string FileDate(DateTimeOffset d)
        {
            return d.ToLocalTime().ToString("yyyy-MM-dd HH'h'mm", CultureInfo.InvariantCulture);
        }

        /// <summary>"245 Ko", "1,2 Mo", ...</summary>
        public static string FileSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " octets";
            var kb = bytes / 1024.0;
            if (kb < 1024) return Math.Max(1, Math.Round(kb)).ToString(CultureInfo.InvariantCulture) + " Ko";
            var mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#", Culture) + " Mo";
            return (mb / 1024.0).ToString("0.##", Culture) + " Go";
        }
    }
}
