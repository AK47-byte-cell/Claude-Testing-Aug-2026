using System;
using System.Collections.Generic;

namespace CourrielPDF.Core.Conversion
{
    public sealed class ConversionResult
    {
        public ConversionResult(string source)
        {
            Source = source;
        }

        /// <summary>Source path (or display name for messages that never existed on disk).</summary>
        public string Source { get; }
        public string? OutputPdfPath { get; set; }
        public int PageCount { get; set; }
        public List<string> AppendedAttachments { get; } = new List<string>();
        public List<string> SavedAttachmentPaths { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public string? Error { get; set; }
        public TimeSpan Duration { get; set; }

        /// <summary>Rendering engine that produced the PDF, e.g. "WebView2 (Microsoft Edge 151)".</summary>
        public string? Engine { get; set; }

        public bool Success => Error == null && OutputPdfPath != null;
    }
}
