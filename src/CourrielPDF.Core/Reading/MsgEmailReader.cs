using System;
using System.IO;
using CourrielPDF.Core.Model;
using MsgReader.Outlook;

namespace CourrielPDF.Core.Reading
{
    /// <summary>Reads Outlook .msg files with the MsgReader library.</summary>
    public sealed class MsgEmailReader : IEmailReader
    {
        private const int MaxEmbeddedDepth = 3;

        public bool CanRead(string path) => string.Equals(Path.GetExtension(path), ".msg", StringComparison.OrdinalIgnoreCase);

        public EmailMessage Read(string path)
        {
            ProductInfo.EnsureInitialised();
            using (var msg = new Storage.Message(path))
            {
                var result = Convert(msg, 0);
                result.SourcePath = path;
                return result;
            }
        }

        public EmailMessage Read(Stream stream)
        {
            ProductInfo.EnsureInitialised();
            using (var msg = new Storage.Message(stream))
                return Convert(msg, 0);
        }

        private static EmailMessage Convert(Storage.Message msg, int depth)
        {
            var m = new EmailMessage { SourceFormat = "msg" };
            m.Subject = (msg.Subject ?? string.Empty).Trim();

            var sender = msg.Sender;
            if (sender != null && (!string.IsNullOrWhiteSpace(sender.DisplayName) || !string.IsNullOrWhiteSpace(sender.Email)))
            {
                var addr = new EmailAddress(sender.DisplayName, sender.Email);
                // Fall back to the "on behalf of" sender when the transport sender has no SMTP address.
                if (addr.Address == null && msg.SenderRepresenting != null && !string.IsNullOrWhiteSpace(msg.SenderRepresenting.Email))
                    addr = new EmailAddress(sender.DisplayName ?? msg.SenderRepresenting.DisplayName, msg.SenderRepresenting.Email);
                if (!addr.IsEmpty) m.From = addr;
            }
            else if (msg.SenderRepresenting != null)
            {
                var addr = new EmailAddress(msg.SenderRepresenting.DisplayName, msg.SenderRepresenting.Email);
                if (!addr.IsEmpty) m.From = addr;
            }

            if (msg.Recipients != null)
            {
                foreach (var r in msg.Recipients)
                {
                    var addr = new EmailAddress(r.DisplayName, r.Email);
                    if (addr.IsEmpty) continue;
                    switch (r.Type)
                    {
                        case RecipientType.Cc: m.Cc.Add(addr); break;
                        case RecipientType.Bcc: m.Bcc.Add(addr); break;
                        default: m.To.Add(addr); break;
                    }
                }
            }

            m.SentOn = msg.SentOn;
            m.ReceivedOn = msg.ReceivedOn;
            if (m.SentOn == null && m.ReceivedOn == null)
                m.SentOn = msg.CreationTime;

            m.HtmlBody = NullIfBlank(SafeGet(() => msg.BodyHtml));
            m.RtfBody = NullIfBlank(SafeGet(() => msg.BodyRtf));
            m.TextBody = NullIfBlank(SafeGet(() => msg.BodyText));

            if (msg.Importance.HasValue)
            {
                switch (msg.Importance.Value)
                {
                    case MessageImportance.High: m.Importance = "Haute"; break;
                    case MessageImportance.Low: m.Importance = "Basse"; break;
                }
            }

            if (msg.Attachments != null)
            {
                var index = 0;
                foreach (var o in msg.Attachments)
                {
                    index++;
                    if (o is Storage.Attachment a)
                    {
                        if (a.OleAttachment) continue; // OLE objects embedded in RTF cannot be rendered on their own
                        var data = SafeGet(() => a.Data) ?? Array.Empty<byte>();
                        var name = a.FileName;
                        if (string.IsNullOrWhiteSpace(name)) name = "piece-jointe-" + index + GuessExtension(a.MimeType);
                        var att = new EmailAttachment(name!, data)
                        {
                            ContentId = NullIfBlank(a.ContentId),
                            MimeType = NullIfBlank(a.MimeType),
                            IsInline = a.IsInline,
                            IsHidden = a.Hidden,
                            RenderingPosition = a.RenderingPosition == -1 || a.RenderingPosition == int.MaxValue || (uint)a.RenderingPosition == 0xFFFFFFFF ? -1 : a.RenderingPosition,
                        };
                        if (a.IsContactPhoto) { att.IsInline = true; att.IsHidden = true; }
                        m.Attachments.Add(att);
                    }
                    else if (o is Storage.Message embedded)
                    {
                        var name = (string.IsNullOrWhiteSpace(embedded.Subject) ? "Courriel joint" : embedded.Subject!.Trim()) + ".msg";
                        byte[] raw = Array.Empty<byte>();
                        try
                        {
                            using (var ms = new MemoryStream())
                            {
                                embedded.Save(ms);
                                raw = ms.ToArray();
                            }
                        }
                        catch
                        {
                            // Keep going without the raw bytes; the parsed content is still usable.
                        }
                        var att = new EmailAttachment(name, raw)
                        {
                            MimeType = "application/vnd.ms-outlook",
                            EmbeddedMessageExtension = ".msg",
                            RenderingPosition = embedded.RenderingPosition == -1 || (uint)embedded.RenderingPosition == 0xFFFFFFFF ? -1 : embedded.RenderingPosition,
                        };
                        if (depth < MaxEmbeddedDepth)
                        {
                            try { att.EmbeddedMessage = Convert(embedded, depth + 1); }
                            catch { /* leave as a raw attachment */ }
                        }
                        m.Attachments.Add(att);
                    }
                }
            }

            return m;
        }

        private static string GuessExtension(string? mimeType)
        {
            switch ((mimeType ?? string.Empty).ToLowerInvariant())
            {
                case "image/png": return ".png";
                case "image/jpeg": return ".jpg";
                case "image/gif": return ".gif";
                case "image/bmp": return ".bmp";
                case "application/pdf": return ".pdf";
                default: return ".bin";
            }
        }

        private static T? SafeGet<T>(Func<T?> getter) where T : class
        {
            try { return getter(); }
            catch { return null; }
        }

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
