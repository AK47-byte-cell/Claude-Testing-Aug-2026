using CourrielPDF.Core.Html;
using CourrielPDF.Core.Model;

namespace CourrielPDF.Core.Conversion
{
    /// <summary>
    /// Tells real attachments apart from the pictures that belong to the message body (signature logos, icons,
    /// pasted images). A mail client marks those either by referencing them from the HTML body or by hiding them
    /// from the attachment list; a photograph sent from a phone can carry the same marks, so size matters too.
    /// </summary>
    public static class AttachmentClassifier
    {
        /// <summary>A picture that is part of the body and no larger than this is taken for decoration.</summary>
        public const long DecorativeImageMaxBytes = 128 * 1024;

        /// <summary>A picture that is part of the body and no wider or taller than this is taken for decoration.</summary>
        public const int DecorativeImageMaxPixels = 800;

        /// <summary>PDF mode: the picture is drawn inside the rendered body (or hidden by Outlook), so it is not an attachment.</summary>
        public static bool IsRenderedInBody(EmailAttachment a, PreparedBody? prepared)
        {
            if (a.EmbeddedMessage != null) return false;
            if (prepared != null && prepared.InlinedAttachments.Contains(a)) return true;
            return a.IsHidden && a.IsDisplayableImage;
        }

        /// <summary>Attachments-only mode: a small picture that belongs to the body and would only be noise as a file.</summary>
        public static bool IsDecorativeInlineImage(EmailAttachment a, PreparedBody? prepared)
        {
            if (a.EmbeddedMessage != null || !a.IsDisplayableImage) return false;
            var partOfBody = (prepared != null && prepared.InlinedAttachments.Contains(a)) || a.IsHidden;
            return partOfBody && LooksDecorative(a);
        }

        /// <summary>Small in bytes and, when the header can be read, small in pixels as well.</summary>
        public static bool LooksDecorative(EmailAttachment a)
        {
            if (a.Size > DecorativeImageMaxBytes) return false;
            if (ImageSniffer.TryGetDimensions(a.Data, out var width, out var height))
                return width <= DecorativeImageMaxPixels && height <= DecorativeImageMaxPixels;
            return true;
        }
    }
}
