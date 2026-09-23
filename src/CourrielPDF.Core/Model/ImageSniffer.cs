using System;
using System.Text;

namespace CourrielPDF.Core.Model
{
    /// <summary>Recognises common image formats, and their pixel size, from the first bytes of a file without decoding it.</summary>
    public static class ImageSniffer
    {
        public static string? DetectMimeType(byte[]? d)
        {
            if (d == null || d.Length < 12) return null;
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
            if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
            if (d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'8') return "image/gif";
            if (d[0] == (byte)'B' && d[1] == (byte)'M') return "image/bmp";
            if (d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F' && d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P') return "image/webp";
            if ((d[0] == 0x49 && d[1] == 0x49 && d[2] == 0x2A && d[3] == 0x00) || (d[0] == 0x4D && d[1] == 0x4D && d[2] == 0x00 && d[3] == 0x2A)) return "image/tiff";
            if (d[0] == (byte)'<')
            {
                var head = Encoding.ASCII.GetString(d, 0, Math.Min(d.Length, 512)).ToLowerInvariant();
                if (head.IndexOf("<svg", StringComparison.Ordinal) >= 0) return "image/svg+xml";
            }
            return null;
        }

        /// <summary>True for the formats a browser draws natively (so they can appear inside a message body).</summary>
        public static bool IsBrowserDisplayable(string? mimeType)
        {
            switch ((mimeType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "image/png":
                case "image/jpeg":
                case "image/jpg":
                case "image/gif":
                case "image/bmp":
                case "image/webp":
                case "image/svg+xml":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Reads the pixel size of a PNG, GIF, BMP or JPEG from its header. Returns false when unknown.</summary>
        public static bool TryGetDimensions(byte[]? d, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (d == null || d.Length < 24) return false;
            switch (DetectMimeType(d))
            {
                case "image/png":
                    width = ReadBigEndian32(d, 16);
                    height = ReadBigEndian32(d, 20);
                    return width > 0 && height > 0;
                case "image/gif":
                    width = d[6] | (d[7] << 8);
                    height = d[8] | (d[9] << 8);
                    return width > 0 && height > 0;
                case "image/bmp":
                    if (d.Length < 26) return false;
                    width = BitConverter.ToInt32(d, 18);
                    height = Math.Abs(BitConverter.ToInt32(d, 22));
                    return width > 0 && height > 0;
                case "image/jpeg":
                    return TryGetJpegDimensions(d, out width, out height);
                default:
                    return false;
            }
        }

        private static int ReadBigEndian32(byte[] d, int offset)
        {
            if (offset + 4 > d.Length) return 0;
            return (d[offset] << 24) | (d[offset + 1] << 16) | (d[offset + 2] << 8) | d[offset + 3];
        }

        /// <summary>Walks the JPEG segments until the first "start of frame", which carries the dimensions.</summary>
        private static bool TryGetJpegDimensions(byte[] d, out int width, out int height)
        {
            width = 0;
            height = 0;
            var i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                var marker = d[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                if (marker == 0xD9 || marker == 0xDA) return false;
                var length = (d[i + 2] << 8) | d[i + 3];
                if (length < 2) return false;
                var isStartOfFrame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isStartOfFrame)
                {
                    height = (d[i + 5] << 8) | d[i + 6];
                    width = (d[i + 7] << 8) | d[i + 8];
                    return width > 0 && height > 0;
                }
                i += 2 + length;
            }
            return false;
        }
    }
}
