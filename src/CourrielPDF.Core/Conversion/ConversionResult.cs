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

        public ConversionMode Mode { get; set; } = ConversionMode.ConvertToPdf;

        /// <summary>Path of the PDF, in <see cref="ConversionMode.ConvertToPdf"/> mode.</summary>
        public string? OutputPdfPath { get; set; }
        public int PageCount { get; set; }
        public List<string> AppendedAttachments { get; } = new List<string>();

        /// <summary>Attachments written to disk (the sub-folder in PDF mode, the destination folder in attachments-only mode).</summary>
        public List<string> SavedAttachmentPaths { get; } = new List<string>();

        /// <summary>Attachments-only mode: pictures belonging to the message body that were deliberately not saved.</summary>
        public int SkippedInlineImages { get; set; }

        public List<string> Warnings { get; } = new List<string>();
        public string? Error { get; set; }
        public bool Cancelled { get; set; }
        public TimeSpan Duration { get; set; }

        /// <summary>Rendering engine that produced the PDF, e.g. "WebView2 (Microsoft Edge 151)".</summary>
        public string? Engine { get; set; }

        public bool Success => Error == null;
    }
}
