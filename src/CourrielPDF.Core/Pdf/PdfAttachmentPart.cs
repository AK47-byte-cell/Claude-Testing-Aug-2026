using System;
using PdfSharp.Pdf;

namespace CourrielPDF.Core.Pdf
{
    /// <summary>A PDF attachment (or converted embedded e-mail) ready to be appended.</summary>
    public sealed class PdfAttachmentPart : IDisposable
    {
        public PdfAttachmentPart(string fileName, long size, PdfDocument document, string? kindLabel = null)
        {
            FileName = fileName;
            Size = size;
            Document = document;
            KindLabel = kindLabel;
        }

        public string FileName { get; }
        public long Size { get; }
        public PdfDocument Document { get; }
        /// <summary>Optional label such as "Courriel joint" shown on the separator page.</summary>
        public string? KindLabel { get; }
        public int PageCount => Document.PageCount;

        public void Dispose() => Document.Dispose();
    }
}
