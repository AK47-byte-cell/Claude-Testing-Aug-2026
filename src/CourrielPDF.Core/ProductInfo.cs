using System;
using System.Text;
using System.Threading;

namespace CourrielPDF.Core
{
    /// <summary>Static product information and one-time runtime initialisation.</summary>
    public static class ProductInfo
    {
        public const string Name = "CourrielPDF";
        public const string Version = "0.1.0";

        private static int _initialised;

        /// <summary>
        /// Registers the legacy code-page encodings (windows-1252 etc.) that Outlook messages
        /// frequently use. Safe to call any number of times.
        /// </summary>
        public static void EnsureInitialised()
        {
            if (Interlocked.Exchange(ref _initialised, 1) == 1)
                return;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch (Exception)
            {
                // Already registered or not needed on this runtime.
            }
        }
    }
}
