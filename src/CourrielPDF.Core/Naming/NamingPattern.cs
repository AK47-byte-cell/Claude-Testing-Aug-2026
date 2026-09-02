namespace CourrielPDF.Core.Naming
{
    public enum NamingPattern
    {
        /// <summary>Same base name as the source file (or the subject when there is no file).</summary>
        SameAsSource = 0,
        /// <summary>"2026-09-02 14h35 - Objet".</summary>
        DateSubject = 1,
        /// <summary>"2026-09-02 14h35 - Expéditeur - Objet".</summary>
        DateSenderSubject = 2,
    }
}
