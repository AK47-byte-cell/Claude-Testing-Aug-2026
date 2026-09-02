using System;
using System.IO;
using CourrielPDF.Core.Model;

namespace CourrielPDF.Core.Reading
{
    /// <summary>Entry point: picks the right reader from the file extension (or content) and loads the message.</summary>
    public static class EmailLoader
    {
        public static readonly string[] SupportedExtensions = { ".msg", ".eml" };

        public static bool IsSupported(string path)
        {
            var ext = Path.GetExtension(path);
            foreach (var s in SupportedExtensions)
                if (string.Equals(s, ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static EmailMessage Load(string path)
        {
            ProductInfo.EnsureInitialised();
            if (!File.Exists(path))
                throw new FileNotFoundException("Fichier introuvable : " + path, path);

            var ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            if (ext == ".msg" || (ext != ".eml" && LooksLikeCompoundFile(path)))
                return new MsgEmailReader().Read(path);
            return new EmlEmailReader().Read(path);
        }

        /// <summary>Outlook .msg files are OLE compound files and start with the D0 CF 11 E0 signature.</summary>
        private static bool LooksLikeCompoundFile(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var b = new byte[8];
                    var n = fs.Read(b, 0, 8);
                    return n == 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
