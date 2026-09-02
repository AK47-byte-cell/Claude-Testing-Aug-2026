using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CourrielPDF.Core.Html;
using CourrielPDF.Core.Model;

namespace CourrielPDF.Core.Naming
{
    /// <summary>Builds Windows-safe output file names and resolves collisions.</summary>
    public static class OutputNaming
    {
        public const int MaxBaseNameLength = 120;
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        public static string BuildBaseName(EmailMessage message, NamingPattern pattern, string? sourceFileName)
        {
            var subject = Sanitize(string.IsNullOrWhiteSpace(message.Subject) ? "(sans objet)" : message.Subject, MaxBaseNameLength);
            if (subject.Length == 0) subject = "(sans objet)";
            var date = message.BestDate.HasValue ? FrenchFormats.FileDate(message.BestDate.Value) : "sans date";

            switch (pattern)
            {
                case NamingPattern.DateSubject:
                    return Fit(date + " - ", subject);
                case NamingPattern.DateSenderSubject:
                    {
                        var sender = message.From != null ? Sanitize(message.From.ShortName, 40) : string.Empty;
                        if (sender.Length == 0) sender = "expéditeur inconnu";
                        return Fit(date + " - " + sender + " - ", subject);
                    }
                default:
                    {
                        if (!string.IsNullOrWhiteSpace(sourceFileName))
                        {
                            var baseName = Sanitize(Path.GetFileNameWithoutExtension(sourceFileName), MaxBaseNameLength);
                            if (baseName.Length > 0) return baseName;
                        }
                        return subject;
                    }
            }
        }

        private static string Fit(string prefix, string subject)
        {
            var room = MaxBaseNameLength - prefix.Length;
            if (room < 10) room = 10;
            if (subject.Length > room) subject = subject.Substring(0, room).TrimEnd(' ', '.', '-');
            return prefix + subject;
        }

        /// <summary>Removes characters Windows forbids, control characters and trailing dots/spaces.</summary>
        public static string Sanitize(string? name, int maxLength = MaxBaseNameLength)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();
            var sb = new StringBuilder(name!.Length);
            foreach (var c in name)
            {
                if (char.IsControl(c)) { sb.Append(' '); continue; }
                if (Array.IndexOf(invalid, c) >= 0)
                {
                    sb.Append(c == ':' || c == '/' || c == '\\' || c == '|' ? '-' : ' ');
                    continue;
                }
                sb.Append(c);
            }
            var s = Whitespace.Replace(sb.ToString(), " ").Trim();
            s = s.Trim(' ', '.');
            if (s.Length > maxLength) s = s.Substring(0, maxLength).TrimEnd(' ', '.', '-');
            var upper = s.ToUpperInvariant();
            if (Reserved.Contains(upper) || Reserved.Contains(Path.GetFileNameWithoutExtension(upper)))
                s = "_" + s;
            return s;
        }

        /// <summary>Returns a path in <paramref name="directory"/> that does not exist yet, adding " (2)", " (3)"… when needed.</summary>
        public static string MakeUniquePath(string directory, string baseName, string extension)
        {
            if (!extension.StartsWith(".")) extension = "." + extension;
            var candidate = Path.Combine(directory, baseName + extension);
            var n = 2;
            while (File.Exists(candidate) || Directory.Exists(candidate))
            {
                candidate = Path.Combine(directory, baseName + " (" + n + ")" + extension);
                n++;
            }
            return candidate;
        }

        /// <summary>Same as <see cref="MakeUniquePath"/> for a folder.</summary>
        public static string MakeUniqueDirectory(string directory, string baseName)
        {
            var candidate = Path.Combine(directory, baseName);
            var n = 2;
            while (Directory.Exists(candidate) || File.Exists(candidate))
            {
                candidate = Path.Combine(directory, baseName + " (" + n + ")");
                n++;
            }
            return candidate;
        }
    }
}
