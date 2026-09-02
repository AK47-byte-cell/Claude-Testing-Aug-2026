using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CourrielPDF.Core.Html;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace CourrielPDF.Core.Pdf
{
    public sealed class PdfAssemblyOptions
    {
        public bool SeparatorPages { get; set; } = true;
        public bool NumberPages { get; set; } = true;
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? Subject { get; set; }
        public DateTime? CreationDate { get; set; }
    }

    /// <summary>Merges the rendered e-mail with its PDF attachments, adds separator pages and page numbers.</summary>
    public static class PdfAssembler
    {
        /// <summary>Opens PDF bytes for import. Returns null (with a French reason) when the file cannot be merged.</summary>
        public static PdfDocument? TryOpenForImport(byte[] data, out string? reason)
        {
            reason = null;
            EmbeddedFontResolver.EnsureRegistered();
            try
            {
                var ms = new MemoryStream(data, false);
                var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
                if (doc.PageCount == 0)
                {
                    doc.Dispose();
                    reason = "le fichier PDF ne contient aucune page";
                    return null;
                }
                return doc;
            }
            catch (PdfReaderException ex)
            {
                reason = ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 || ex.Message.IndexOf("encrypt", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "le fichier PDF est protégé par un mot de passe"
                    : "le fichier PDF n'a pu être lu (" + Shorten(ex.Message) + ")";
                return null;
            }
            catch (Exception ex)
            {
                reason = "le fichier PDF n'a pu être lu (" + Shorten(ex.Message) + ")";
                return null;
            }
        }

        /// <summary>Builds the final PDF and returns its page count.</summary>
        public static int Assemble(string bodyPdfPath, IList<PdfAttachmentPart> attachments, PdfAssemblyOptions options, string outputPath)
        {
            EmbeddedFontResolver.EnsureRegistered();

            using (var output = new PdfDocument())
            using (var body = PdfReader.Open(bodyPdfPath, PdfDocumentOpenMode.Import))
            {
                for (var i = 0; i < body.PageCount; i++)
                    output.AddPage(body.Pages[i]);

                var index = 0;
                foreach (var att in attachments)
                {
                    index++;
                    if (options.SeparatorPages)
                        AddSeparatorPage(output, index, attachments.Count, att);
                    for (var i = 0; i < att.Document.PageCount; i++)
                        output.AddPage(att.Document.Pages[i]);
                }

                if (options.NumberPages)
                    NumberPages(output);

                output.Info.Creator = ProductInfo.Name + " " + ProductInfo.Version;
                if (!string.IsNullOrWhiteSpace(options.Title)) output.Info.Title = options.Title!;
                if (!string.IsNullOrWhiteSpace(options.Author)) output.Info.Author = options.Author!;
                if (!string.IsNullOrWhiteSpace(options.Subject)) output.Info.Subject = options.Subject!;
                if (options.CreationDate.HasValue) output.Info.CreationDate = options.CreationDate.Value;

                var pageCount = output.PageCount;
                output.Save(outputPath);
                return pageCount;
            }
        }

        private static void AddSeparatorPage(PdfDocument output, int index, int total, PdfAttachmentPart att)
        {
            var page = output.AddPage();
            page.Size = PageSize.Letter;
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                var w = page.Width.Point;
                var h = page.Height.Point;
                var titleFont = new XFont(EmbeddedFontResolver.FamilyName, 18, XFontStyleEx.Bold);
                var nameFont = new XFont(EmbeddedFontResolver.FamilyName, 13);
                var infoFont = new XFont(EmbeddedFontResolver.FamilyName, 10);

                var label = att.KindLabel ?? "Pièce jointe";
                var title = total > 1 ? label + " " + index + " de " + total : label;
                gfx.DrawString(title, titleFont, XBrushes.Black, new XRect(54, h * 0.40, w - 108, 30), XStringFormats.Center);

                var name = att.FileName;
                if (name.Length > 110) name = name.Substring(0, 107) + "…";
                gfx.DrawString(name, nameFont, XBrushes.Black, new XRect(54, h * 0.40 + 34, w - 108, 24), XStringFormats.Center);

                var pages = att.PageCount == 1 ? "1 page" : att.PageCount.ToString(CultureInfo.InvariantCulture) + " pages";
                gfx.DrawString(FrenchFormats.FileSize(att.Size) + " · " + pages, infoFont, XBrushes.Gray, new XRect(54, h * 0.40 + 62, w - 108, 20), XStringFormats.Center);

                var pen = new XPen(XColors.Gray, 0.5);
                gfx.DrawLine(pen, w * 0.3, h * 0.40 - 12, w * 0.7, h * 0.40 - 12);
            }
        }

        private static void NumberPages(PdfDocument output)
        {
            var total = output.PageCount;
            var font = new XFont(EmbeddedFontResolver.FamilyName, 8);
            for (var i = 0; i < total; i++)
            {
                var page = output.Pages[i];
                try
                {
                    if (page.Rotate % 360 != 0) continue;
                    using (var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
                    {
                        var w = page.Width.Point;
                        var h = page.Height.Point;
                        var text = "Page " + (i + 1).ToString(CultureInfo.InvariantCulture) + " de " + total.ToString(CultureInfo.InvariantCulture);
                        gfx.DrawString(text, font, XBrushes.Gray, new XRect(0, h - 30, w, 14), XStringFormats.Center);
                    }
                }
                catch
                {
                    // Numbering must never break the conversion.
                }
            }
        }

        private static string Shorten(string s)
        {
            s = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 120 ? s.Substring(0, 117) + "…" : s;
        }
    }
}
