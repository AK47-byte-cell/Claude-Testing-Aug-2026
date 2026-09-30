using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CourrielPDF.App.Rendering;
using CourrielPDF.Core.Conversion;
using CourrielPDF.Core.Rendering;

namespace CourrielPDF.App
{
    /// <summary>
    /// Unattended self-test used by the build pipeline: processes the given file or folder with the same engines as the
    /// interactive window (WebView2 first, headless Edge second), writes a journal and exits with 0 on success.
    /// Invoked as: CourrielPDF.exe --autotest &lt;fichier ou dossier&gt; &lt;dossier de sortie&gt; [journal.txt] [pj]
    /// The optional "pj" switches to attachments-only mode.
    /// </summary>
    internal sealed class AutoTestForm : Form
    {
        private readonly string _input;
        private readonly string _outputDir;
        private readonly string? _journalPath;
        private readonly bool _attachmentsOnly;
        private readonly StringBuilder _journal = new StringBuilder();

        public AutoTestForm(string input, string outputDir, string? journalPath, bool attachmentsOnly)
        {
            _input = input;
            _outputDir = outputDir;
            _journalPath = journalPath;
            _attachmentsOnly = attachmentsOnly;
            Text = "CourrielPDF — autotest";
            ShowInTaskbar = false;
            Opacity = 0;
            Size = new Size(900, 700);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(0, 0);
            Shown += async (s, e) => await RunAsync();
        }

        private void Log(string line)
        {
            _journal.AppendLine(line);
        }

        private async Task RunAsync()
        {
            var exitCode = 0;
            try
            {
                Log("CourrielPDF autotest — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + (_attachmentsOnly ? " — mode pièces jointes seulement" : string.Empty));
                var runtime = WebView2Bootstrap.GetRuntimeVersion(out var runtimeError);
                Log("WebView2 Runtime : " + (runtime ?? "indisponible — " + runtimeError));
                Log("Edge : " + (BrowserLocator.Find() ?? "introuvable"));

                var files = InputExpander.Expand(new[] { _input });
                if (files.Count == 0)
                {
                    Log("ERREUR : aucun fichier .msg/.eml dans " + _input);
                    exitCode = 2;
                }
                else
                {
                    var options = new ConversionOptions();
                    var host = this;
                    options.RendererFactory = () => new FallbackPdfRenderer(
                        () => runtime != null ? new WebView2PdfRenderer(host, options.AllowRemoteImages) { Timeout = options.RenderTimeout } : null,
                        () => new ChromiumPdfRenderer(BrowserLocator.Find() ?? throw new BrowserNotFoundException()) { Timeout = options.RenderTimeout, AllowNetwork = options.AllowRemoteImages },
                        m => Log("Moteur : " + m));
                    options.Log = m => Log("  · " + m);

                    var outputDir = _outputDir;
                    var attachmentsOnly = _attachmentsOnly;
                    var results = await Task.Run(() =>
                    {
                        var list = new List<ConversionResult>();
                        using (var service = new ConversionService(options))
                        {
                            foreach (var f in files)
                                list.Add(attachmentsOnly ? service.ExtractAttachments(f, outputDir) : service.ConvertFile(f, outputDir));
                        }
                        return list;
                    }).ConfigureAwait(true);

                    foreach (var r in results)
                    {
                        if (r.Success && r.Mode == ConversionMode.AttachmentsOnly)
                        {
                            Log("OK      " + r.Source + " : " + r.SavedAttachmentPaths.Count + " pièce(s) jointe(s) enregistrée(s)" + (r.SkippedInlineImages > 0 ? ", " + r.SkippedInlineImages + " image(s) intégrée(s) ignorée(s)" : string.Empty));
                            foreach (var p in r.SavedAttachmentPaths) Log("        + " + p);
                        }
                        else if (r.Success)
                        {
                            Log("OK      " + r.OutputPdfPath + " (" + r.PageCount + " pages) — moteur : " + r.Engine);
                        }
                        else
                        {
                            Log("ERREUR  " + r.Source + " : " + r.Error);
                            exitCode = 1;
                        }
                        foreach (var w in r.Warnings) Log("        ! " + w);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("ERREUR FATALE : " + ex);
                exitCode = 3;
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(_journalPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_journalPath!))!);
                        File.WriteAllText(_journalPath!, _journal.ToString(), new UTF8Encoding(false));
                    }
                }
                catch { }
                Environment.ExitCode = exitCode;
                Close();
            }
        }
    }
}
