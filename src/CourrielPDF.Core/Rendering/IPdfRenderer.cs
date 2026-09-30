using System;
using System.Threading;

namespace CourrielPDF.Core.Rendering
{
    /// <summary>Turns a self-contained HTML file into a PDF file.</summary>
    public interface IPdfRenderer : IDisposable
    {
        /// <summary>Human-readable name of the engine that produced the last PDF (shown in logs and diagnostics).</summary>
        string Description { get; }

        /// <summary>Renders <paramref name="htmlPath"/> to <paramref name="outputPdfPath"/>, throwing <see cref="PdfRenderException"/> on failure.</summary>
        void RenderFile(string htmlPath, string outputPdfPath, CancellationToken cancellationToken);
    }
}
