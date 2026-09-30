namespace CourrielPDF.Core.Html
{
    /// <summary>One line of the "Pièces jointes" header entry.</summary>
    public sealed class AttachmentDisplayItem
    {
        public AttachmentDisplayItem(string fileName, long size, string? note)
        {
            FileName = fileName;
            Size = size;
            Note = note;
        }

        public string FileName { get; }
        public long Size { get; }
        /// <summary>What happened to the attachment, e.g. "jointe ci-après" or "enregistrée séparément".</summary>
        public string? Note { get; }
    }
}
