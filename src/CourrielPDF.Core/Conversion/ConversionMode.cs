namespace CourrielPDF.Core.Conversion
{
    /// <summary>What to do with a dropped e-mail.</summary>
    public enum ConversionMode
    {
        /// <summary>Convert the e-mail to PDF; PDF attachments are merged after it, the others saved in a sub-folder.</summary>
        ConvertToPdf = 0,

        /// <summary>Create no PDF at all: save the attachments of every e-mail into one destination folder.</summary>
        AttachmentsOnly = 1,
    }
}
