using System;
using System.Collections.Generic;

namespace CourrielPDF.Core.Model
{
    /// <summary>Client-neutral representation of an e-mail loaded from a .msg or .eml file.</summary>
    public sealed class EmailMessage
    {
        public string Subject { get; set; } = string.Empty;
        public EmailAddress? From { get; set; }
        public List<EmailAddress> To { get; } = new List<EmailAddress>();
        public List<EmailAddress> Cc { get; } = new List<EmailAddress>();
        public List<EmailAddress> Bcc { get; } = new List<EmailAddress>();
        public DateTimeOffset? SentOn { get; set; }
        public DateTimeOffset? ReceivedOn { get; set; }
        public string? HtmlBody { get; set; }
        public string? RtfBody { get; set; }
        public string? TextBody { get; set; }
        public List<EmailAttachment> Attachments { get; } = new List<EmailAttachment>();
        public string? Importance { get; set; }

        /// <summary>Full path of the source file, when the message was loaded from disk.</summary>
        public string? SourcePath { get; set; }

        /// <summary>"msg" or "eml".</summary>
        public string SourceFormat { get; set; } = "msg";

        /// <summary>The best available date: sent, else received.</summary>
        public DateTimeOffset? BestDate => SentOn ?? ReceivedOn;

        public bool HasHtmlBody => !string.IsNullOrWhiteSpace(HtmlBody);
        public bool HasRtfBody => !string.IsNullOrWhiteSpace(RtfBody);
        public bool HasTextBody => !string.IsNullOrWhiteSpace(TextBody);
    }
}
