using System;
using System.IO;

namespace CourrielPDF.Core.Model
{
    /// <summary>An attachment of an <see cref="EmailMessage"/>, fully loaded in memory.</summary>
    public sealed class EmailAttachment
    {
        public EmailAttachment(string fileName, byte[] data)
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? "piece-jointe" : fileName.Trim();
            Data = data ?? Array.Empty<byte>();
        }

        public string FileName { get; set; }
        public byte[] Data { get; }
        public string? ContentId { get; set; }
        public string? MimeType { get; set; }

        /// <summary>True when the mail client flagged the part as inline (e.g. an image inside the body).</summary>
        public bool IsInline { get; set; }

        /// <summary>True when the mail client marked the attachment as hidden (Outlook PR_ATTACHMENT_HIDDEN).</summary>
        public bool IsHidden { get; set; }

        /// <summary>Position of the attachment inside an RTF body, or -1 when not rendered inline.</summary>
        public long RenderingPosition { get; set; } = -1;

        /// <summary>When the attachment is itself an e-mail (forwarded item), its parsed content.</summary>
        public EmailMessage? EmbeddedMessage { get; set; }

        /// <summary>Extension of the raw representation of an embedded message (".msg" or ".eml").</summary>
        public string? EmbeddedMessageExtension { get; set; }

        public long Size => Data.LongLength;

        public string Extension => Path.GetExtension(FileName) ?? string.Empty;

        /// <summary>A PDF, detected from the content signature first and the name/MIME type second.</summary>
        public bool IsPdf
        {
            get
            {
                if (Data.Length >= 5 && Data[0] == (byte)'%' && Data[1] == (byte)'P' && Data[2] == (byte)'D' && Data[3] == (byte)'F')
                    return true;
                if (Data.Length >= 1024)
                {
                    // Some producers put junk before the header; look within the first KB.
                    var head = System.Text.Encoding.ASCII.GetString(Data, 0, 1024);
                    if (head.IndexOf("%PDF-", StringComparison.Ordinal) >= 0) return true;
                }
                return string.Equals(Extension, ".pdf", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool IsImage
        {
            get
            {
                if (MimeType != null && MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
                switch (Extension.ToLowerInvariant())
                {
                    case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".webp": case ".svg": case ".tif": case ".tiff": case ".emf": case ".wmf":
                        return true;
                }
                return false;
            }
        }

        /// <summary>An image a browser can draw (PNG, JPEG, GIF, BMP, WebP, SVG), judged from the content first.</summary>
        public bool IsDisplayableImage
        {
            get
            {
                var magic = ImageSniffer.DetectMimeType(Data);
                if (magic != null) return ImageSniffer.IsBrowserDisplayable(magic);
                switch (Extension.ToLowerInvariant())
                {
                    case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".webp": case ".svg":
                        return true;
                }
                return ImageSniffer.IsBrowserDisplayable(MimeType);
            }
        }

        /// <summary>Best-effort MIME type for embedding the attachment as a data URI.</summary>
        public string EffectiveMimeType
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(MimeType) && MimeType!.IndexOf('/') > 0 && !MimeType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                    return MimeType;
                switch (Extension.ToLowerInvariant())
                {
                    case ".png": return "image/png";
                    case ".jpg": case ".jpeg": return "image/jpeg";
                    case ".gif": return "image/gif";
                    case ".bmp": return "image/bmp";
                    case ".webp": return "image/webp";
                    case ".svg": return "image/svg+xml";
                    case ".tif": case ".tiff": return "image/tiff";
                    case ".pdf": return "application/pdf";
                    default: return MimeType ?? "application/octet-stream";
                }
            }
        }

        public override string ToString() => FileName + " (" + Size + " octets)";
    }
}
