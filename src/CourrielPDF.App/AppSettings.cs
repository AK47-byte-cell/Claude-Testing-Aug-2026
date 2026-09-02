using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CourrielPDF.Core.Conversion;
using CourrielPDF.Core.Naming;

namespace CourrielPDF.App
{
    /// <summary>User preferences, stored as simple key=value lines under %APPDATA%\CourrielPDF.</summary>
    internal sealed class AppSettings
    {
        public NamingPattern Naming { get; set; } = NamingPattern.SameAsSource;
        public bool AppendPdfAttachments { get; set; } = true;
        public bool SaveOtherAttachments { get; set; } = true;
        public bool ConvertEmbeddedMessages { get; set; } = true;
        public bool SeparatorPages { get; set; } = true;
        public bool NumberPages { get; set; } = true;
        public bool AllowRemoteImages { get; set; }
        public bool IncludeProvenance { get; set; } = true;
        public bool OpenFolderAfter { get; set; }
        public string LastOutputFolder { get; set; } = string.Empty;
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }

        public static string FolderPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CourrielPDF");
        public static string FilePath => Path.Combine(FolderPath, "parametres.txt");

        public ConversionOptions ToConversionOptions()
        {
            return new ConversionOptions
            {
                Naming = Naming,
                AppendPdfAttachments = AppendPdfAttachments,
                SaveOtherAttachments = SaveOtherAttachments,
                ConvertEmbeddedMessages = ConvertEmbeddedMessages,
                SeparatorPages = SeparatorPages,
                NumberPages = NumberPages,
                AllowRemoteImages = AllowRemoteImages,
                IncludeProvenance = IncludeProvenance,
            };
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                s.Naming = (NamingPattern)GetInt(map, "Naming", (int)s.Naming);
                if (!Enum.IsDefined(typeof(NamingPattern), s.Naming)) s.Naming = NamingPattern.SameAsSource;
                s.AppendPdfAttachments = GetBool(map, "AppendPdfAttachments", s.AppendPdfAttachments);
                s.SaveOtherAttachments = GetBool(map, "SaveOtherAttachments", s.SaveOtherAttachments);
                s.ConvertEmbeddedMessages = GetBool(map, "ConvertEmbeddedMessages", s.ConvertEmbeddedMessages);
                s.SeparatorPages = GetBool(map, "SeparatorPages", s.SeparatorPages);
                s.NumberPages = GetBool(map, "NumberPages", s.NumberPages);
                s.AllowRemoteImages = GetBool(map, "AllowRemoteImages", s.AllowRemoteImages);
                s.IncludeProvenance = GetBool(map, "IncludeProvenance", s.IncludeProvenance);
                s.OpenFolderAfter = GetBool(map, "OpenFolderAfter", s.OpenFolderAfter);
                s.LastOutputFolder = map.TryGetValue("LastOutputFolder", out var f) ? f : string.Empty;
                s.WindowWidth = GetInt(map, "WindowWidth", 0);
                s.WindowHeight = GetInt(map, "WindowHeight", 0);
            }
            catch
            {
                // Corrupt settings file: start from defaults.
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                var lines = new[]
                {
                    "Naming=" + ((int)Naming).ToString(CultureInfo.InvariantCulture),
                    "AppendPdfAttachments=" + AppendPdfAttachments,
                    "SaveOtherAttachments=" + SaveOtherAttachments,
                    "ConvertEmbeddedMessages=" + ConvertEmbeddedMessages,
                    "SeparatorPages=" + SeparatorPages,
                    "NumberPages=" + NumberPages,
                    "AllowRemoteImages=" + AllowRemoteImages,
                    "IncludeProvenance=" + IncludeProvenance,
                    "OpenFolderAfter=" + OpenFolderAfter,
                    "LastOutputFolder=" + LastOutputFolder,
                    "WindowWidth=" + WindowWidth.ToString(CultureInfo.InvariantCulture),
                    "WindowHeight=" + WindowHeight.ToString(CultureInfo.InvariantCulture),
                };
                File.WriteAllLines(FilePath, lines);
            }
            catch
            {
                // Never fail because preferences could not be written.
            }
        }

        private static bool GetBool(Dictionary<string, string> map, string key, bool def)
        {
            return map.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : def;
        }

        private static int GetInt(Dictionary<string, string> map, string key, int def)
        {
            return map.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : def;
        }
    }
}
