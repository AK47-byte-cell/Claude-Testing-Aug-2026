using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CourrielPDF.Core.Rendering;
using Microsoft.Web.WebView2.Core;

namespace CourrielPDF.App.Rendering
{
    /// <summary>
    /// Renders HTML to PDF with the Microsoft Edge WebView2 component hosted invisibly inside the application window.
    /// Unlike launching msedge.exe, this is unaffected by the policies that govern the Edge browser (forced user data
    /// directory, an already running Edge instance, etc.) and needs no separate process.
    /// The WebView2 objects live on the UI thread; <see cref="RenderFile"/> is called from a worker thread and waits.
    /// </summary>
    internal sealed class WebView2PdfRenderer : IPdfRenderer
    {
        private readonly Control _host;
        private readonly bool _allowNetwork;
        private CoreWebView2Environment? _environment;
        private CoreWebView2Controller? _controller;
        private bool _needsReset;
        private bool _disposed;
        private string _description = "WebView2 (Microsoft Edge)";

        public WebView2PdfRenderer(Control host, bool allowNetwork)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _allowNetwork = allowNetwork;
        }

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);

        public string Description => _description;

        public void RenderFile(string htmlPath, string outputPdfPath, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WebView2PdfRenderer));
            if (!_host.InvokeRequired)
                throw new InvalidOperationException("WebView2PdfRenderer.RenderFile doit être appelé depuis un thread de travail, pas depuis le thread de l'interface.");
            if (!File.Exists(htmlPath)) throw new FileNotFoundException("Fichier HTML introuvable.", htmlPath);

            var pdf = Path.GetFullPath(outputPdfPath);
            var dir = Path.GetDirectoryName(pdf);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            if (File.Exists(pdf)) File.Delete(pdf);
            var url = new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _host.BeginInvoke(new Action(() => RenderOnUiThread(url, pdf, tcs)));

            try
            {
                if (!tcs.Task.Wait((int)Timeout.TotalMilliseconds, cancellationToken))
                {
                    _needsReset = true;
                    throw new PdfRenderException("WebView2 n'a pas produit le PDF dans le délai imparti (" + (int)Timeout.TotalSeconds + " s).");
                }
            }
            catch (AggregateException ex)
            {
                var inner = ex.InnerException ?? ex;
                if (inner is PdfRenderException) throw inner;
                throw new PdfRenderException("WebView2 : " + inner.GetType().Name + " : " + inner.Message, inner);
            }

            if (!File.Exists(pdf) || new FileInfo(pdf).Length == 0)
                throw new PdfRenderException("WebView2 n'a produit aucun fichier PDF.");
        }

        private async void RenderOnUiThread(string url, string pdf, TaskCompletionSource<bool> tcs)
        {
            try
            {
                if (_needsReset) ResetController();
                await EnsureControllerAsync().ConfigureAwait(true);
                var webView = _controller!.CoreWebView2;

                var navigation = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
                handler = (s, e) =>
                {
                    webView.NavigationCompleted -= handler;
                    navigation.TrySetResult(e);
                };
                webView.NavigationCompleted += handler;
                webView.Navigate(url);
                var result = await navigation.Task.ConfigureAwait(true);
                if (!result.IsSuccess)
                    throw new PdfRenderException("WebView2 n'a pu charger le courriel (" + result.WebErrorStatus + ").");

                // Let the layout and the embedded images settle before printing.
                await Task.Delay(150).ConfigureAwait(true);

                var settings = _environment!.CreatePrintSettings();
                settings.Orientation = CoreWebView2PrintOrientation.Portrait;
                settings.PageWidth = 8.5;
                settings.PageHeight = 11;
                settings.MarginTop = 0.6;
                settings.MarginBottom = 0.75;
                settings.MarginLeft = 0.6;
                settings.MarginRight = 0.6;
                settings.ScaleFactor = 1.0;
                settings.ShouldPrintBackgrounds = true;
                settings.ShouldPrintHeaderAndFooter = false;
                settings.ShouldPrintSelectionOnly = false;

                var ok = await webView.PrintToPdfAsync(pdf, settings).ConfigureAwait(true);
                if (!ok) throw new PdfRenderException("WebView2 a refusé d'imprimer le courriel en PDF.");
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _needsReset = true;
                tcs.TrySetException(ex);
            }
        }

        private async Task EnsureControllerAsync()
        {
            if (_controller != null) return;

            if (_environment == null)
            {
                if (!WebView2Bootstrap.EnsureLoader())
                    throw new PdfRenderException("Le chargeur WebView2 n'a pu être préparé : " + WebView2Bootstrap.LoaderError);
                var userDataFolder = WebView2Bootstrap.UserDataFolder;
                Directory.CreateDirectory(userDataFolder);
                var options = new CoreWebView2EnvironmentOptions
                {
                    Language = "fr-CA",
                    AdditionalBrowserArguments = "--disable-background-networking --disable-component-update",
                };
                _environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options).ConfigureAwait(true);
                _description = "WebView2 (Microsoft Edge " + _environment.BrowserVersionString + ")";
            }

            var controller = await _environment.CreateCoreWebView2ControllerAsync(_host.Handle).ConfigureAwait(true);
            controller.IsVisible = false;
            controller.Bounds = new Rectangle(0, 0, 816, 1056);
            controller.DefaultBackgroundColor = Color.White;

            var webView = controller.CoreWebView2;
            var s = webView.Settings;
            s.IsScriptEnabled = false;
            s.AreDefaultScriptDialogsEnabled = false;
            s.AreDevToolsEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsWebMessageEnabled = false;
            s.AreHostObjectsAllowed = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsBuiltInErrorPageEnabled = false;

            if (!_allowNetwork)
            {
                // Never let a converted e-mail reach the network: every http(s) request gets an empty response.
                webView.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.All);
                webView.AddWebResourceRequestedFilter("https://*", CoreWebView2WebResourceContext.All);
                webView.WebResourceRequested += (sender, e) =>
                {
                    try { e.Response = _environment.CreateWebResourceResponse(null, 204, "No Content", string.Empty); }
                    catch { }
                };
            }

            webView.NavigationStarting += (sender, e) =>
            {
                var uri = e.Uri ?? string.Empty;
                if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    e.Cancel = true;
            };
            webView.NewWindowRequested += (sender, e) => e.Handled = true;
            webView.ProcessFailed += (sender, e) => _needsReset = true;

            _controller = controller;
            _needsReset = false;
        }

        private void ResetController()
        {
            try { _controller?.Close(); } catch { }
            _controller = null;
            _needsReset = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_host.IsHandleCreated && !_host.IsDisposed)
                {
                    if (_host.InvokeRequired) _host.BeginInvoke(new Action(ResetController));
                    else ResetController();
                }
            }
            catch
            {
                // Closing the hidden browser must never fail the caller.
            }
        }
    }
}
