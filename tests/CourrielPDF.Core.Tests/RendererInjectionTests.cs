using System;
using System.IO;
using System.Threading;
using CourrielPDF.Core.Conversion;
using CourrielPDF.Core.Rendering;
using PdfSharp.Pdf;
using Xunit;

namespace CourrielPDF.Core.Tests
{
    /// <summary>The desktop application injects its own (WebView2) engine; make sure the service honours it.</summary>
    public class RendererInjectionTests : IDisposable
    {
        private readonly string _dir = Fixtures.NewTempDirectory();

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private sealed class FakeRenderer : IPdfRenderer
        {
            public int Calls;
            public bool Disposed;
            public string? LastHtml;
            public string Description => "Faux moteur de test";

            public void RenderFile(string htmlPath, string outputPdfPath, CancellationToken cancellationToken)
            {
                Calls++;
                LastHtml = File.ReadAllText(htmlPath);
                using (var doc = new PdfDocument())
                {
                    doc.AddPage();
                    doc.Save(outputPdfPath);
                }
            }

            public void Dispose() => Disposed = true;
        }

        private sealed class FailingRenderer : IPdfRenderer
        {
            public string Description => "Moteur en panne";
            public void RenderFile(string htmlPath, string outputPdfPath, CancellationToken cancellationToken) => throw new PdfRenderException("panne simulée");
            public void Dispose() { }
        }

        [Fact]
        public void Injected_renderer_is_used_even_without_any_browser()
        {
            var source = Fixtures.WriteRtfMsg(_dir);
            var fake = new FakeRenderer();
            var options = new ConversionOptions { BrowserPath = Path.Combine(_dir, "nul.exe"), RendererFactory = () => fake };
            var previous = Environment.GetEnvironmentVariable("COURRIELPDF_BROWSER");
            var previousPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("COURRIELPDF_BROWSER", null);
                Environment.SetEnvironmentVariable("PATH", _dir);
                using (var service = new ConversionService(options))
                {
                    var r = service.ConvertFile(source);
                    Assert.True(r.Success, r.Error);
                    Assert.Equal(1, r.PageCount);
                    Assert.Equal("Faux moteur de test", r.Engine);
                    Assert.Equal(1, fake.Calls);
                    Assert.Contains("Message en RTF seulement", fake.LastHtml);
                    Assert.Contains("De :", fake.LastHtml);
                }
                Assert.True(fake.Disposed);
            }
            finally
            {
                Environment.SetEnvironmentVariable("COURRIELPDF_BROWSER", previous);
                Environment.SetEnvironmentVariable("PATH", previousPath);
            }
        }

        [Fact]
        public void Renderer_failure_is_reported_and_leaves_no_partial_output()
        {
            var source = Fixtures.WriteMsg(_dir);
            var options = new ConversionOptions { RendererFactory = () => new FailingRenderer() };
            using (var service = new ConversionService(options))
            {
                var r = service.ConvertFile(source);
                Assert.False(r.Success);
                Assert.Contains("panne simulée", r.Error);
                Assert.Empty(Directory.GetFiles(_dir, "*.pdf"));
                Assert.Empty(Directory.GetDirectories(_dir));
            }
        }
    }
}
