using System;
using System.IO;
using PdfSharp.Fonts;

namespace CourrielPDF.Core.Pdf
{
    /// <summary>
    /// Serves the Liberation Sans font embedded in this assembly so that PDFsharp never depends on the
    /// fonts installed on the machine (identical output on Windows and on the Linux build servers).
    /// </summary>
    internal sealed class EmbeddedFontResolver : IFontResolver
    {
        public const string FamilyName = "CourrielPDF Sans";
        private const string FaceName = "CourrielPDF-LiberationSans-Regular";
        private const string ResourceName = "CourrielPDF.Core.Assets.LiberationSans-Regular.ttf";

        private static readonly object Sync = new object();
        private static byte[]? _fontBytes;
        private static bool _registered;

        public static void EnsureRegistered()
        {
            lock (Sync)
            {
                if (_registered) return;
                try
                {
                    if (GlobalFontSettings.FontResolver == null || !(GlobalFontSettings.FontResolver is EmbeddedFontResolver))
                        GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
                }
                catch (Exception)
                {
                    // A resolver was already set (and fonts created); keep it and fall back to it.
                    GlobalFontSettings.FallbackFontResolver = new EmbeddedFontResolver();
                }
                _registered = true;
            }
        }

        public byte[]? GetFont(string faceName)
        {
            if (!string.Equals(faceName, FaceName, StringComparison.Ordinal)) return null;
            return LoadBytes();
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            // Whatever family is requested, we only ship one face and simulate the styles.
            return new FontResolverInfo(FaceName, bold, italic);
        }

        private static byte[] LoadBytes()
        {
            lock (Sync)
            {
                if (_fontBytes != null) return _fontBytes;
                using (var s = typeof(EmbeddedFontResolver).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (s == null) throw new InvalidOperationException("Police intégrée introuvable : " + ResourceName);
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        _fontBytes = ms.ToArray();
                    }
                }
                return _fontBytes;
            }
        }
    }
}
