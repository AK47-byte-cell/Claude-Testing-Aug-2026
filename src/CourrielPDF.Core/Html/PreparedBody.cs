using System.Collections.Generic;
using CourrielPDF.Core.Model;

namespace CourrielPDF.Core.Html
{
    /// <summary>The sanitised message body, ready to be wrapped in the print document.</summary>
    public sealed class PreparedBody
    {
        public string InnerHtml { get; set; } = string.Empty;
        public string BodyStyle { get; set; } = string.Empty;
        public string? BodyLang { get; set; }
        public List<string> StyleSheets { get; } = new List<string>();
        /// <summary>Attachments that were embedded into the body (CID images, RTF pictures) and must not be listed as attachments.</summary>
        public HashSet<EmailAttachment> InlinedAttachments { get; } = new HashSet<EmailAttachment>();
        public List<string> Warnings { get; } = new List<string>();
        public bool IsPlainText { get; set; }
    }
}
