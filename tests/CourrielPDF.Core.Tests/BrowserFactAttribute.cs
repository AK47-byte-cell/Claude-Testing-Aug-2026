using CourrielPDF.Core.Rendering;
using Xunit;

namespace CourrielPDF.Core.Tests
{
    /// <summary>A fact that only runs when a Chromium browser is available on the machine.</summary>
    public sealed class BrowserFactAttribute : FactAttribute
    {
        public BrowserFactAttribute()
        {
            if (BrowserLocator.Find() == null)
                Skip = "Aucun navigateur Chromium (Edge/Chrome) n'est disponible sur cette machine.";
        }
    }
}
