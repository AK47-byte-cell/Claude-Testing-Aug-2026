using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CourrielPDF.Core.Model;
using HtmlAgilityPack;
using RtfPipe;

namespace CourrielPDF.Core.Html
{
    /// <summary>
    /// Turns an <see cref="EmailMessage"/> into a self-contained HTML document with a French header block,
    /// suitable for printing to PDF with a Chromium engine. All active content is stripped, inline images are
    /// embedded as data URIs and remote images are either kept or replaced by placeholders.
    /// </summary>
    public static class HtmlComposer
    {
        private const string TransparentGif = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

        private static readonly Regex AtPageRule = new Regex(@"@page\b[^{]*\{[^}]*\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AtImportRule = new Regex(@"@import\b[^;]*;", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PageProperty = new Regex(@"(^|[;\s{])page\s*:\s*[^;}""']+;?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PositionFixed = new Regex(@"position\s*:\s*fixed", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UrlPattern = new Regex(@"(https?://[^\s<>""']+[^\s<>""'.,;:!?)\]])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NumberPattern = new Regex(@"^\s*(\d+)", RegexOptions.Compiled);

        static HtmlComposer()
        {
            // HtmlAgilityPack closes <form> elements early by default, which mangles some newsletters.
            HtmlNode.ElementsFlags.Remove("form");
            HtmlNode.ElementsFlags.Remove("option");
        }

        // ------------------------------------------------------------------
        // Phase 1: body
        // ------------------------------------------------------------------

        public static PreparedBody PrepareBody(EmailMessage message, HtmlComposerOptions options)
        {
            var prepared = new PreparedBody();
            string html;

            if (message.HasHtmlBody)
            {
                html = message.HtmlBody!;
            }
            else if (message.HasRtfBody)
            {
                html = RtfToHtml(message, prepared) ?? PlainToHtml(message.TextBody);
                if (message.TextBody == null && html == PlainToHtml(null))
                    prepared.IsPlainText = true;
            }
            else
            {
                html = PlainToHtml(message.TextBody);
                prepared.IsPlainText = true;
            }

            var doc = new HtmlDocument
            {
                OptionOutputOriginalCase = true,
                OptionFixNestedTags = true,
                OptionAutoCloseOnEnd = true,
            };
            doc.LoadHtml(html);

            RemoveNodes(doc, "//script|//iframe|//frame|//frameset|//object|//embed|//applet|//link|//base|//meta|//noscript|//audio|//video|//source|//track|//title|//template|//input[@type='hidden']|//comment()");
            StripActiveAttributes(doc);
            ProcessImages(doc, message, options, prepared);
            CollectStyles(doc, prepared);

            var bodyNode = doc.DocumentNode.SelectSingleNode("//body");
            if (bodyNode != null)
            {
                prepared.InnerHtml = bodyNode.InnerHtml;
                prepared.BodyStyle = BuildBodyStyle(bodyNode);
                prepared.BodyLang = bodyNode.GetAttributeValue("lang", null) ?? doc.DocumentNode.SelectSingleNode("//html")?.GetAttributeValue("lang", null);
            }
            else
            {
                var head = doc.DocumentNode.SelectSingleNode("//head");
                head?.Remove();
                var htmlNode = doc.DocumentNode.SelectSingleNode("//html");
                prepared.InnerHtml = htmlNode != null ? htmlNode.InnerHtml : doc.DocumentNode.InnerHtml;
            }

            return prepared;
        }

        // ------------------------------------------------------------------
        // Phase 2: whole document
        // ------------------------------------------------------------------

        public static string ComposeDocument(EmailMessage message, PreparedBody body, IList<AttachmentDisplayItem> attachments, HtmlComposerOptions options)
        {
            var sb = new StringBuilder(body.InnerHtml.Length + 8192);
            sb.Append("<!DOCTYPE html>\n<html lang=\"fr\">\n<head>\n<meta charset=\"utf-8\">\n");
            sb.Append("<title>").Append(Encode(string.IsNullOrWhiteSpace(message.Subject) ? "(sans objet)" : message.Subject)).Append("</title>\n");
            sb.Append("<style>\n").Append(BaseCss).Append("\n</style>\n");
            foreach (var css in body.StyleSheets)
                sb.Append("<style>\n").Append(css).Append("\n</style>\n");
            sb.Append("</head>\n<body>\n");

            AppendHeader(sb, message, attachments, options);

            sb.Append("<div class=\"cpdf-body");
            if (body.IsPlainText) sb.Append(" cpdf-plain-wrapper");
            sb.Append('"');
            if (!string.IsNullOrEmpty(body.BodyLang)) sb.Append(" lang=\"").Append(Encode(body.BodyLang!)).Append('"');
            if (!string.IsNullOrEmpty(body.BodyStyle)) sb.Append(" style=\"").Append(Encode(body.BodyStyle)).Append('"');
            sb.Append(">\n");
            sb.Append(body.InnerHtml);
            sb.Append("\n</div>\n</body>\n</html>\n");
            return sb.ToString();
        }

        private static void AppendHeader(StringBuilder sb, EmailMessage m, IList<AttachmentDisplayItem> attachments, HtmlComposerOptions options)
        {
            sb.Append("<div class=\"cpdf-header\">\n");
            sb.Append("<div class=\"cpdf-subject\">").Append(Encode(string.IsNullOrWhiteSpace(m.Subject) ? "(sans objet)" : m.Subject)).Append("</div>\n");
            sb.Append("<table class=\"cpdf-fields\">\n");

            Row(sb, "De :", m.From != null && !m.From.IsEmpty ? Encode(m.From.Display) : "<span class=\"cpdf-muted\">(expéditeur inconnu)</span>");

            if (m.SentOn.HasValue)
                Row(sb, "Envoyé :", Encode(FrenchFormats.LongDateTime(m.SentOn.Value)));
            else if (m.ReceivedOn.HasValue)
                Row(sb, "Reçu :", Encode(FrenchFormats.LongDateTime(m.ReceivedOn.Value)));
            else
                Row(sb, "Envoyé :", "<span class=\"cpdf-muted\">(date inconnue)</span>");

            Row(sb, "À :", m.To.Count > 0 ? Join(m.To) : "<span class=\"cpdf-muted\">(aucun destinataire)</span>");
            if (m.Cc.Count > 0) Row(sb, "Cc :", Join(m.Cc));
            if (m.Bcc.Count > 0) Row(sb, "Cci :", Join(m.Bcc));
            Row(sb, "Objet :", Encode(string.IsNullOrWhiteSpace(m.Subject) ? "(sans objet)" : m.Subject));
            if (!string.IsNullOrEmpty(m.Importance)) Row(sb, "Importance :", Encode(m.Importance!));

            if (attachments.Count > 0)
            {
                var list = new StringBuilder();
                foreach (var a in attachments)
                {
                    list.Append("<div class=\"cpdf-att\">").Append(Encode(a.FileName));
                    list.Append(" <span class=\"cpdf-muted\">(").Append(Encode(FrenchFormats.FileSize(a.Size))).Append(')');
                    if (!string.IsNullOrEmpty(a.Note)) list.Append(" — ").Append(Encode(a.Note!));
                    list.Append("</span></div>");
                }
                Row(sb, attachments.Count > 1 ? "Pièces jointes :" : "Pièce jointe :", list.ToString());
            }

            sb.Append("</table>\n");

            if (options.IncludeProvenance)
            {
                sb.Append("<div class=\"cpdf-provenance\">");
                if (!string.IsNullOrEmpty(options.SourceFileName))
                    sb.Append("Fichier source : ").Append(Encode(options.SourceFileName!)).Append(" · ");
                sb.Append("Converti le ").Append(Encode(FrenchFormats.LongDateTime(options.ConvertedAt))).Append(" (").Append(ProductInfo.Name).Append(')');
                sb.Append("</div>\n");
            }

            sb.Append("</div>\n");
        }

        private static void Row(StringBuilder sb, string label, string valueHtml)
        {
            sb.Append("<tr><th>").Append(Encode(label)).Append("</th><td>").Append(valueHtml).Append("</td></tr>\n");
        }

        private static string Join(IEnumerable<EmailAddress> addresses)
        {
            return string.Join("; ", addresses.Select(a => Encode(a.Display)));
        }

        // ------------------------------------------------------------------
        // Sanitisation helpers
        // ------------------------------------------------------------------

        private static void RemoveNodes(HtmlDocument doc, string xpath)
        {
            var nodes = doc.DocumentNode.SelectNodes(xpath);
            if (nodes == null) return;
            foreach (var n in nodes.ToList())
                n.Remove();
        }

        private static void StripActiveAttributes(HtmlDocument doc)
        {
            foreach (var node in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element).ToList())
            {
                foreach (var attr in node.Attributes.ToList())
                {
                    var name = attr.Name;
                    if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    {
                        attr.Remove();
                        continue;
                    }
                    var value = (attr.Value ?? string.Empty).Trim();
                    var lower = value.ToLowerInvariant().Replace(" ", string.Empty).Replace("\t", string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty);
                    if (lower.StartsWith("javascript:") || lower.StartsWith("vbscript:") || lower.StartsWith("data:text/html"))
                    {
                        attr.Remove();
                        continue;
                    }
                    if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
                    {
                        var cleaned = PageProperty.Replace(value, "$1");
                        cleaned = PositionFixed.Replace(cleaned, "position:static");
                        if (cleaned.IndexOf("expression(", StringComparison.OrdinalIgnoreCase) >= 0) cleaned = string.Empty;
                        if (!ReferenceEquals(cleaned, value)) attr.Value = cleaned;
                    }
                }
                if (node.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
                    node.Attributes.Remove("target");
            }
        }

        private static void CollectStyles(HtmlDocument doc, PreparedBody prepared)
        {
            var styles = doc.DocumentNode.SelectNodes("//style");
            if (styles == null) return;
            foreach (var s in styles.ToList())
            {
                var css = s.InnerHtml ?? string.Empty;
                // Word/Outlook emit their own @page rules and named pages, which would fight with ours
                // and can insert a page break between the header and the body.
                css = AtPageRule.Replace(css, string.Empty);
                css = AtImportRule.Replace(css, string.Empty);
                css = PageProperty.Replace(css, "$1");
                css = PositionFixed.Replace(css, "position:static");
                css = css.Replace("</style", "<\\/style");
                if (!string.IsNullOrWhiteSpace(css)) prepared.StyleSheets.Add(css);
                s.Remove();
            }
        }

        private static string BuildBodyStyle(HtmlNode body)
        {
            var sb = new StringBuilder();
            var bg = body.GetAttributeValue("bgcolor", null);
            if (!string.IsNullOrWhiteSpace(bg)) sb.Append("background-color:").Append(bg.Trim()).Append(';');
            var text = body.GetAttributeValue("text", null);
            if (!string.IsNullOrWhiteSpace(text)) sb.Append("color:").Append(text.Trim()).Append(';');
            var style = body.GetAttributeValue("style", null);
            if (!string.IsNullOrWhiteSpace(style))
            {
                var cleaned = PageProperty.Replace(style, "$1").Trim();
                if (cleaned.Length > 0) sb.Append(cleaned).Append(cleaned.EndsWith(";") ? string.Empty : ";");
            }
            return sb.ToString();
        }

        private static void ProcessImages(HtmlDocument doc, EmailMessage message, HtmlComposerOptions options, PreparedBody prepared)
        {
            var nodes = doc.DocumentNode.SelectNodes("//*[@src or @background]");
            if (nodes == null) return;

            foreach (var node in nodes.ToList())
            {
                foreach (var attrName in new[] { "src", "background" })
                {
                    var attr = node.Attributes[attrName];
                    if (attr == null) continue;
                    var value = WebUtility.HtmlDecode(attr.Value ?? string.Empty).Trim();
                    if (value.Length == 0) continue;
                    var lower = value.ToLowerInvariant();

                    if (lower.StartsWith("data:"))
                        continue;

                    if (lower.StartsWith("cid:"))
                    {
                        var uri = ResolveCid(message, value.Substring(4), prepared);
                        if (uri != null) attr.Value = uri;
                        else ReplaceWithPlaceholder(doc, node, attrName, "[Image manquante]", prepared);
                        continue;
                    }

                    if (lower.StartsWith("http://") || lower.StartsWith("https://") || lower.StartsWith("//"))
                    {
                        if (options.AllowRemoteImages) continue;
                        if (IsTrackingPixel(node)) { node.Remove(); break; }
                        ReplaceWithPlaceholder(doc, node, attrName, "[Image externe non téléchargée]", prepared);
                        continue;
                    }

                    if (lower.StartsWith("file:") || lower.StartsWith("mailto:") || lower.StartsWith("about:"))
                    {
                        ReplaceWithPlaceholder(doc, node, attrName, "[Image non disponible]", prepared);
                        continue;
                    }

                    // Relative reference (e.g. "image001.png"): Outlook sometimes references attachments this way.
                    var byName = ResolveByFileName(message, value, prepared);
                    if (byName != null) attr.Value = byName;
                    else ReplaceWithPlaceholder(doc, node, attrName, "[Image non disponible]", prepared);
                }
            }
        }

        private static bool IsTrackingPixel(HtmlNode node)
        {
            var w = ParseDimension(node, "width");
            var h = ParseDimension(node, "height");
            if (w.HasValue && h.HasValue && w.Value <= 3 && h.Value <= 3) return true;
            var src = (node.GetAttributeValue("src", string.Empty)).ToLowerInvariant();
            if (src.Contains("track") || src.Contains("pixel") || src.Contains("beacon") || src.Contains("open.aspx") || src.Contains("/o.gif") || src.Contains("spacer"))
                return (w ?? 0) <= 10 && (h ?? 0) <= 10;
            return false;
        }

        private static int? ParseDimension(HtmlNode node, string name)
        {
            var v = node.GetAttributeValue(name, null);
            if (!string.IsNullOrEmpty(v))
            {
                var m = NumberPattern.Match(v);
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
            }
            var style = node.GetAttributeValue("style", null);
            if (!string.IsNullOrEmpty(style))
            {
                var m = Regex.Match(style, @"(?:^|;)\s*" + name + @"\s*:\s*(\d+)\s*(px|pt)?", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return m.Groups[2].Value.Equals("pt", StringComparison.OrdinalIgnoreCase) ? (int)Math.Round(n * 96.0 / 72.0) : n;
            }
            return null;
        }

        private static void ReplaceWithPlaceholder(HtmlDocument doc, HtmlNode node, string attrName, string label, PreparedBody prepared)
        {
            if (!attrName.Equals("src", StringComparison.OrdinalIgnoreCase) || !node.Name.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                // A background attribute or a non-<img> element (VML, etc.): just drop the reference.
                node.Attributes[attrName]?.Remove();
                return;
            }

            var alt = WebUtility.HtmlDecode(node.GetAttributeValue("alt", string.Empty) ?? string.Empty).Trim();
            var w = ParseDimension(node, "width");
            var h = ParseDimension(node, "height");
            if (w.HasValue && h.HasValue && w.Value <= 12 && h.Value <= 12)
            {
                node.Remove();
                return;
            }

            var span = doc.CreateElement("span");
            var style = new StringBuilder("display:inline-block;box-sizing:border-box;border:1px dashed #9a9a9a;background:#f4f4f4;color:#666;font:8pt Arial,sans-serif;line-height:1.2;padding:2px 4px;vertical-align:middle;overflow:hidden;text-align:center;");
            if (w.HasValue && w.Value > 12) style.Append("width:").Append(w.Value).Append("px;max-width:100%;");
            if (h.HasValue && h.Value > 12) style.Append("height:").Append(Math.Min(h.Value, 400)).Append("px;");
            span.SetAttributeValue("style", style.ToString());
            span.SetAttributeValue("class", "cpdf-placeholder");
            span.InnerHtml = Encode(alt.Length > 0 ? label + " : " + alt : label);
            node.ParentNode.ReplaceChild(span, node);

            if (!prepared.Warnings.Contains(label))
                prepared.Warnings.Add(label);
        }

        private static string? ResolveCid(EmailMessage message, string cid, PreparedBody prepared)
        {
            var key = NormaliseCid(cid);
            if (key.Length == 0) return null;
            foreach (var a in message.Attachments)
            {
                if (a.ContentId == null) continue;
                if (string.Equals(NormaliseCid(a.ContentId), key, StringComparison.OrdinalIgnoreCase))
                    return ToDataUri(a, prepared);
            }
            // Some clients reference "cid:image001.png@01DA..." where only the file-name part matches.
            var at = key.IndexOf('@');
            var fileKey = at > 0 ? key.Substring(0, at) : key;
            foreach (var a in message.Attachments)
            {
                if (string.Equals(a.FileName, fileKey, StringComparison.OrdinalIgnoreCase) || string.Equals(a.FileName, key, StringComparison.OrdinalIgnoreCase))
                    return ToDataUri(a, prepared);
                if (a.ContentId != null)
                {
                    var acid = NormaliseCid(a.ContentId);
                    var aat = acid.IndexOf('@');
                    if (aat > 0 && string.Equals(acid.Substring(0, aat), fileKey, StringComparison.OrdinalIgnoreCase))
                        return ToDataUri(a, prepared);
                }
            }
            return null;
        }

        private static string? ResolveByFileName(EmailMessage message, string reference, PreparedBody prepared)
        {
            var name = reference;
            var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (slash >= 0) name = name.Substring(slash + 1);
            var q = name.IndexOf('?');
            if (q >= 0) name = name.Substring(0, q);
            name = Uri.UnescapeDataString(name);
            if (name.Length == 0) return null;
            foreach (var a in message.Attachments)
            {
                if (string.Equals(a.FileName, name, StringComparison.OrdinalIgnoreCase))
                    return ToDataUri(a, prepared);
            }
            return null;
        }

        private static string NormaliseCid(string cid)
        {
            var s = cid.Trim();
            if (s.StartsWith("<")) s = s.Substring(1);
            if (s.EndsWith(">")) s = s.Substring(0, s.Length - 1);
            try { s = Uri.UnescapeDataString(s); } catch { }
            return s.Trim();
        }

        private static string? ToDataUri(EmailAttachment a, PreparedBody prepared)
        {
            var mime = ImageMimeFromMagic(a.Data) ?? (a.IsImage ? a.EffectiveMimeType : null);
            if (mime == null || mime == "image/tiff" || mime.Contains("emf") || mime.Contains("wmf"))
            {
                // The browser cannot draw this; leave it to the attachment handling instead of swallowing it.
                prepared.Warnings.Add("Image intégrée non affichable (" + a.FileName + ") : traitée comme pièce jointe.");
                return TransparentGif;
            }
            prepared.InlinedAttachments.Add(a);
            return "data:" + mime + ";base64," + Convert.ToBase64String(a.Data);
        }

        internal static string? ImageMimeFromMagic(byte[] d) => ImageSniffer.DetectMimeType(d);

        // ------------------------------------------------------------------
        // Body sources
        // ------------------------------------------------------------------

        private static string PlainToHtml(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "<div class=\"cpdf-plain cpdf-muted\">(Ce courriel ne contient aucun texte.)</div>";
            var encoded = Encode(text!.Replace("\r\n", "\n").Replace('\r', '\n'));
            encoded = UrlPattern.Replace(encoded, m => "<a href=\"" + m.Value + "\">" + m.Value + "</a>");
            return "<div class=\"cpdf-plain\">" + encoded + "</div>";
        }

        private static string? RtfToHtml(EmailMessage message, PreparedBody prepared)
        {
            try
            {
                var inlineCandidates = message.Attachments
                    .Where(a => a.RenderingPosition >= 0)
                    .OrderBy(a => a.RenderingPosition)
                    .ToList();

                var settings = new RtfHtmlSettings
                {
                    ImageUriGetter = pic =>
                    {
                        try
                        {
                            var mime = pic.MimeType();
                            if (pic.Bytes == null || pic.Bytes.Length == 0) return TransparentGif;
                            var detected = ImageMimeFromMagic(pic.Bytes) ?? mime;
                            if (detected == null || detected.Contains("wmf") || detected.Contains("emf") || detected == "image/tiff")
                                return TransparentGif;
                            return "data:" + detected + ";base64," + Convert.ToBase64String(pic.Bytes);
                        }
                        catch
                        {
                            return TransparentGif;
                        }
                    },
                    AttachmentRenderer = (index, writer) =>
                    {
                        EmailAttachment? att = index >= 0 && index < inlineCandidates.Count ? inlineCandidates[index] : null;
                        if (att == null) return;
                        var mime = ImageMimeFromMagic(att.Data);
                        if (mime != null && mime != "image/tiff")
                        {
                            prepared.InlinedAttachments.Add(att);
                            writer.WriteStartElement("img");
                            writer.WriteAttributeString("src", "data:" + mime + ";base64," + Convert.ToBase64String(att.Data));
                            writer.WriteAttributeString("alt", att.FileName);
                            writer.WriteEndElement();
                        }
                        else
                        {
                            writer.WriteStartElement("span");
                            writer.WriteAttributeString("class", "cpdf-att-marker");
                            writer.WriteString("[Pièce jointe : " + att.FileName + "]");
                            writer.WriteEndElement();
                        }
                    },
                };

                var html = Rtf.ToHtml(message.RtfBody!, settings);
                return string.IsNullOrWhiteSpace(html) ? null : html;
            }
            catch (Exception ex)
            {
                prepared.Warnings.Add("Le corps RTF n'a pu être converti (" + ex.GetType().Name + "); le texte brut a été utilisé.");
                return null;
            }
        }

        /// <summary>Escapes only the characters that matter in HTML; accented letters stay readable UTF-8.</summary>
        internal static string Encode(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s!.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private const string BaseCss = @"
@page { size: 8.5in 11in; margin: 0.6in 0.6in 0.75in 0.6in; }
html, body { margin: 0; padding: 0; background: #ffffff; }
.cpdf-header { font-family: 'Segoe UI', Calibri, Arial, sans-serif; font-size: 10pt; color: #000; margin: 0 0 10pt 0; padding: 0 0 6pt 0; border-bottom: 1.5pt solid #000; }
.cpdf-subject { font-size: 14pt; font-weight: bold; margin: 0 0 8pt 0; line-height: 1.25; }
.cpdf-fields { border-collapse: collapse; width: 100%; }
.cpdf-fields th { text-align: left; font-weight: bold; vertical-align: top; padding: 1.5pt 10pt 1.5pt 0; white-space: nowrap; width: 1%; }
.cpdf-fields td { vertical-align: top; padding: 1.5pt 0; word-break: break-word; }
.cpdf-att { margin: 0 0 1pt 0; }
.cpdf-muted { color: #666; }
.cpdf-provenance { color: #777; font-size: 8pt; margin-top: 6pt; }
.cpdf-body { font-family: Calibri, 'Segoe UI', Arial, sans-serif; font-size: 11pt; word-wrap: break-word; overflow-wrap: break-word; }
.cpdf-body img { max-width: 100% !important; height: auto !important; }
.cpdf-body table { max-width: 100% !important; }
.cpdf-plain { white-space: pre-wrap; font-family: Calibri, 'Segoe UI', Arial, sans-serif; font-size: 11pt; line-height: 1.35; }
.cpdf-att-marker { color: #555; font-style: italic; }
";
    }
}
