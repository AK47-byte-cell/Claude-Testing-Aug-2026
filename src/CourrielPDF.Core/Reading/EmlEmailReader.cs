using System;
using System.IO;
using System.Linq;
using CourrielPDF.Core.Model;
using MimeKit;
using MimeKit.Tnef;

namespace CourrielPDF.Core.Reading
{
    /// <summary>Reads RFC 822 / MIME .eml files with MimeKit.</summary>
    public sealed class EmlEmailReader : IEmailReader
    {
        private const int MaxEmbeddedDepth = 3;

        public bool CanRead(string path) => string.Equals(Path.GetExtension(path), ".eml", StringComparison.OrdinalIgnoreCase);

        public EmailMessage Read(string path)
        {
            ProductInfo.EnsureInitialised();
            var message = MimeMessage.Load(path);
            var result = Convert(message, 0);
            result.SourcePath = path;
            return result;
        }

        public EmailMessage Read(Stream stream)
        {
            ProductInfo.EnsureInitialised();
            return Convert(MimeMessage.Load(stream), 0);
        }

        private static EmailMessage Convert(MimeMessage mm, int depth)
        {
            var m = new EmailMessage { SourceFormat = "eml" };
            m.Subject = (mm.Subject ?? string.Empty).Trim();

            var from = mm.From?.Mailboxes.FirstOrDefault() ?? mm.Sender;
            if (from != null) m.From = new EmailAddress(from.Name, from.Address);

            AddAll(mm.To, m.To);
            AddAll(mm.Cc, m.Cc);
            AddAll(mm.Bcc, m.Bcc);

            if (mm.Date != DateTimeOffset.MinValue && mm.Date.Year > 1900)
                m.SentOn = mm.Date;
            m.ReceivedOn = TryParseReceivedDate(mm);

            m.HtmlBody = NullIfBlank(mm.HtmlBody);
            m.TextBody = NullIfBlank(mm.TextBody);

            var importance = mm.Headers["Importance"] ?? mm.Headers["X-Priority"];
            if (importance != null)
            {
                var v = importance.Trim().ToLowerInvariant();
                if (v.StartsWith("high") || v.StartsWith("1") || v.StartsWith("2")) m.Importance = "Haute";
                else if (v.StartsWith("low") || v.StartsWith("5") || v.StartsWith("4")) m.Importance = "Basse";
            }

            foreach (var entity in mm.BodyParts)
            {
                if (entity is MessagePart mp)
                {
                    var embedded = mp.Message;
                    var name = mp.ContentDisposition?.FileName ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                        name = (embedded == null || string.IsNullOrWhiteSpace(embedded.Subject) ? "Courriel joint" : embedded.Subject.Trim()) + ".eml";
                    else if (!name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".msg", StringComparison.OrdinalIgnoreCase))
                        name += ".eml";
                    byte[] raw = Array.Empty<byte>();
                    try
                    {
                        using (var ms = new MemoryStream())
                        {
                            embedded?.WriteTo(ms);
                            raw = ms.ToArray();
                        }
                    }
                    catch { }
                    var att = new EmailAttachment(name, raw) { MimeType = "message/rfc822", EmbeddedMessageExtension = ".eml" };
                    if (embedded != null && depth < MaxEmbeddedDepth)
                    {
                        try { att.EmbeddedMessage = Convert(embedded, depth + 1); } catch { }
                    }
                    m.Attachments.Add(att);
                    continue;
                }

                if (!(entity is MimePart part)) continue;

                // The text/plain and text/html bodies themselves are not attachments.
                if (part is TextPart tp && !part.IsAttachment && string.IsNullOrEmpty(part.FileName) && (tp.IsHtml || tp.IsPlain || tp.IsRichText))
                    continue;

                if (part is TnefPart tnef)
                {
                    // winmail.dat: unpack the Outlook attachments it carries.
                    try
                    {
                        var inner = tnef.ConvertToMessage();
                        var innerMsg = Convert(inner, depth + 1);
                        if (m.HtmlBody == null && innerMsg.HtmlBody != null) m.HtmlBody = innerMsg.HtmlBody;
                        if (m.TextBody == null && innerMsg.TextBody != null) m.TextBody = innerMsg.TextBody;
                        m.Attachments.AddRange(innerMsg.Attachments);
                        continue;
                    }
                    catch
                    {
                        // fall through and keep winmail.dat as an ordinary attachment
                    }
                }

                byte[] data;
                using (var ms = new MemoryStream())
                {
                    if (part.Content != null) part.Content.DecodeTo(ms);
                    data = ms.ToArray();
                }

                var fileName = part.FileName;
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    var ext = MimeTypes.TryGetExtension(part.ContentType.MimeType, out var e) ? e : ".bin";
                    fileName = "piece-jointe-" + (m.Attachments.Count + 1) + ext;
                }

                var disposition = part.ContentDisposition?.Disposition;
                var isInline = string.Equals(disposition, ContentDisposition.Inline, StringComparison.OrdinalIgnoreCase)
                               || (!part.IsAttachment && !string.IsNullOrEmpty(part.ContentId));

                m.Attachments.Add(new EmailAttachment(fileName!, data)
                {
                    ContentId = NullIfBlank(part.ContentId),
                    MimeType = part.ContentType.MimeType,
                    IsInline = isInline,
                });
            }

            return m;
        }

        private static void AddAll(InternetAddressList? list, System.Collections.Generic.List<EmailAddress> target)
        {
            if (list == null) return;
            foreach (var mb in list.Mailboxes)
            {
                var a = new EmailAddress(mb.Name, mb.Address);
                if (!a.IsEmpty) target.Add(a);
            }
        }

        private static DateTimeOffset? TryParseReceivedDate(MimeMessage mm)
        {
            try
            {
                foreach (var h in mm.Headers)
                {
                    if (h.Id != HeaderId.Received) continue;
                    var value = h.Value ?? string.Empty;
                    var semi = value.LastIndexOf(';');
                    if (semi < 0) continue;
                    if (MimeKit.Utils.DateUtils.TryParse(value.Substring(semi + 1).Trim(), out var d))
                        return d;
                }
            }
            catch { }
            return null;
        }

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
