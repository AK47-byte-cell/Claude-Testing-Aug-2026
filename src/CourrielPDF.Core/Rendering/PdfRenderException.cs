using System;

namespace CourrielPDF.Core.Rendering
{
    public class PdfRenderException : Exception
    {
        public PdfRenderException(string message) : base(message) { }
        public PdfRenderException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class BrowserNotFoundException : PdfRenderException
    {
        public BrowserNotFoundException()
            : base("Microsoft Edge (ou un autre navigateur Chromium) est introuvable sur cet ordinateur. " +
                   "Installez Microsoft Edge ou indiquez le chemin du navigateur dans la variable d'environnement " + BrowserLocator.EnvironmentVariable + ".")
        {
        }
    }
}
