using System;
using CourrielPDF.Core.Naming;
using CourrielPDF.Core.Rendering;

namespace CourrielPDF.Core.Conversion
{
    public sealed class ConversionOptions
    {
        public NamingPattern Naming { get; set; } = NamingPattern.SameAsSource;

        /// <summary>Merge PDF attachments after the e-mail pages.</summary>
        public bool AppendPdfAttachments { get; set; } = true;

        /// <summary>Save the other attachments in a "&lt;pdf&gt; - pièces jointes" folder beside the PDF.</summary>
        public bool SaveOtherAttachments { get; set; } = true;

        /// <summary>Convert forwarded e-mails found as attachments and append them like PDF attachments.</summary>
        public bool ConvertEmbeddedMessages { get; set; } = true;

        /// <summary>Insert a title page before each appended attachment.</summary>
        public bool SeparatorPages { get; set; } = true;

        /// <summary>Print "Page x de y" at the bottom of every page.</summary>
        public bool NumberPages { get; set; } = true;

        /// <summary>Let the browser download images hosted on the Internet. Off by default (privacy, speed).</summary>
        public bool AllowRemoteImages { get; set; }

        /// <summary>Add the "Fichier source / converti le" line under the header.</summary>
        public bool IncludeProvenance { get; set; } = true;

        /// <summary>Explicit path to msedge.exe/chrome.exe. When null the browser is auto-detected.</summary>
        public string? BrowserPath { get; set; }

        /// <summary>
        /// Supplies the rendering engine. When null, a headless Edge/Chromium process is used
        /// (see <see cref="ChromiumPdfRenderer"/>). The desktop application injects its WebView2 renderer here.
        /// The service owns and disposes the renderer it obtains.
        /// </summary>
        public Func<IPdfRenderer>? RendererFactory { get; set; }

        public TimeSpan RenderTimeout { get; set; } = TimeSpan.FromSeconds(90);

        /// <summary>Optional diagnostic sink.</summary>
        public Action<string>? Log { get; set; }

        // ---- Attachments-only mode ----

        /// <summary>Also save the pictures that belong to the message body (signature logos, pasted images).</summary>
        public bool AttachmentsIncludeInlineImages { get; set; }

        /// <summary>Save forwarded e-mails found as attachments as .msg/.eml files.</summary>
        public bool AttachmentsSaveForwardedMessages { get; set; } = true;

        /// <summary>Also extract the attachments carried by forwarded e-mails.</summary>
        public bool AttachmentsExtractForwardedMessages { get; set; } = true;

        public ConversionOptions Clone() => (ConversionOptions)MemberwiseClone();
    }
}
