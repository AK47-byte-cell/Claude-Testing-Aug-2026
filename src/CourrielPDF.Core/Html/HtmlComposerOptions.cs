using System;

namespace CourrielPDF.Core.Html
{
    public sealed class HtmlComposerOptions
    {
        /// <summary>Keep &lt;img src="http(s)://..."&gt; references. When false they are replaced by a placeholder.</summary>
        public bool AllowRemoteImages { get; set; }

        /// <summary>Add a small "Fichier source / converti le" line under the header.</summary>
        public bool IncludeProvenance { get; set; } = true;

        /// <summary>Name shown in the provenance line (file name only).</summary>
        public string? SourceFileName { get; set; }

        public DateTimeOffset ConvertedAt { get; set; } = DateTimeOffset.Now;
    }
}
