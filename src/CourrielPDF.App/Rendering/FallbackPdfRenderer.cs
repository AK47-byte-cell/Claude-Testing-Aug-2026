using System;
using System.Threading;
using CourrielPDF.Core.Rendering;

namespace CourrielPDF.App.Rendering
{
    /// <summary>
    /// Uses the primary engine (WebView2) and, should it be unavailable or fail, switches permanently to the
    /// secondary engine (headless msedge.exe) for the rest of the batch. Both failures are reported together.
    /// </summary>
    internal sealed class FallbackPdfRenderer : IPdfRenderer
    {
        private readonly Func<IPdfRenderer?> _primaryFactory;
        private readonly Func<IPdfRenderer> _secondaryFactory;
        private readonly Action<string>? _log;
        private IPdfRenderer? _primary;
        private IPdfRenderer? _secondary;
        private bool _primaryUnusable;
        private string? _primaryError;
        private string _description = "aucun moteur";

        public FallbackPdfRenderer(Func<IPdfRenderer?> primaryFactory, Func<IPdfRenderer> secondaryFactory, Action<string>? log)
        {
            _primaryFactory = primaryFactory;
            _secondaryFactory = secondaryFactory;
            _log = log;
        }

        public string Description => _description;

        public void RenderFile(string htmlPath, string outputPdfPath, CancellationToken cancellationToken)
        {
            if (!_primaryUnusable)
            {
                try
                {
                    if (_primary == null) _primary = _primaryFactory();
                    if (_primary != null)
                    {
                        _primary.RenderFile(htmlPath, outputPdfPath, cancellationToken);
                        _description = _primary.Description;
                        return;
                    }
                    _primaryUnusable = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _primaryUnusable = true;
                    _primaryError = ex.Message;
                    _log?.Invoke("Le moteur WebView2 a échoué (" + ex.Message + "). Passage à Microsoft Edge en arrière-plan pour la suite.");
                }
            }

            try
            {
                if (_secondary == null) _secondary = _secondaryFactory();
                _secondary.RenderFile(htmlPath, outputPdfPath, cancellationToken);
                _description = _secondary.Description;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (_primaryError != null && !(ex is PdfRenderException && ex.Message.Contains("WebView2 avait")))
            {
                throw new PdfRenderException(ex.Message + " (Le moteur WebView2 avait aussi échoué : " + _primaryError + ")", ex);
            }
        }

        public void Dispose()
        {
            try { _primary?.Dispose(); } catch { }
            try { _secondary?.Dispose(); } catch { }
        }
    }
}
