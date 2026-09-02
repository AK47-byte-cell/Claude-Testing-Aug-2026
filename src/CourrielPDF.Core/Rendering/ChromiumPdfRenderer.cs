using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CourrielPDF.Core.Rendering
{
    /// <summary>
    /// Prints an HTML file to PDF using a headless Chromium browser (Microsoft Edge on Windows).
    /// The browser runs with a throw-away profile so the user's normal Edge session is never touched.
    /// </summary>
    public sealed class ChromiumPdfRenderer : IPdfRenderer
    {
        private readonly string _profileDirectory;
        private bool _disposed;

        public ChromiumPdfRenderer(string browserPath, string? workingDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(browserPath) || !File.Exists(browserPath))
                throw new BrowserNotFoundException();
            BrowserPath = browserPath;
            var root = workingDirectory ?? Path.Combine(Path.GetTempPath(), ProductInfo.Name);
            _profileDirectory = Path.Combine(root, "profil-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_profileDirectory);
        }

        public string BrowserPath { get; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);

        public string Description => Path.GetFileName(BrowserPath) + " en arrière-plan (mode headless)";

        /// <summary>Exit code of the last browser process, when it exited on its own.</summary>
        public int? LastExitCode { get; private set; }

        /// <summary>Wall-clock time of the last browser run.</summary>
        public TimeSpan LastElapsed { get; private set; }

        /// <summary>Full command line of the last browser run (for diagnostics).</summary>
        public string LastCommandLine { get; private set; } = string.Empty;

        /// <summary>When false, all network access is disabled inside the browser (DNS resolution fails fast).</summary>
        public bool AllowNetwork { get; set; }

        public string LastStandardError { get; private set; } = string.Empty;

        public void RenderFile(string htmlPath, string outputPdfPath, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ChromiumPdfRenderer));
            if (!File.Exists(htmlPath)) throw new FileNotFoundException("Fichier HTML introuvable.", htmlPath);
            var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPdfPath));
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir!);
            if (File.Exists(outputPdfPath)) File.Delete(outputPdfPath);

            var url = new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri;
            var args = BuildArguments(url, Path.GetFullPath(outputPdfPath));

            var psi = new ProcessStartInfo
            {
                FileName = BrowserPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = _profileDirectory,
            };

            LastCommandLine = "\"" + BrowserPath + "\" " + args;
            LastExitCode = null;
            var started = DateTime.UtcNow;
            var stderr = new StringBuilder();
            using (var process = new Process { StartInfo = psi })
            {
                process.ErrorDataReceived += (s, e) => { if (e.Data != null && stderr.Length < 20000) stderr.AppendLine(e.Data); };
                process.OutputDataReceived += (s, e) => { };
                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    throw new PdfRenderException("Impossible de démarrer le navigateur « " + BrowserPath + " » : " + ex.Message, ex);
                }
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                var deadline = DateTime.UtcNow + Timeout;
                while (!process.WaitForExit(200))
                {
                    if (cancellationToken.IsCancellationRequested || DateTime.UtcNow > deadline)
                    {
                        KillTree(process);
                        LastStandardError = stderr.ToString();
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new PdfRenderException("Le navigateur n'a pas produit le PDF dans le délai imparti (" + (int)Timeout.TotalSeconds + " s).");
                    }
                }
                process.WaitForExit();
                LastStandardError = stderr.ToString();
                try { LastExitCode = process.ExitCode; } catch { LastExitCode = null; }
            }
            LastElapsed = DateTime.UtcNow - started;

            if (!File.Exists(outputPdfPath) || new FileInfo(outputPdfPath).Length == 0)
            {
                var tail = LastStandardError.Length > 1200 ? LastStandardError.Substring(LastStandardError.Length - 1200) : LastStandardError;
                var sb = new StringBuilder();
                sb.Append("Le navigateur n'a pas produit de fichier PDF (").Append(Path.GetFileName(BrowserPath));
                if (LastExitCode.HasValue) sb.Append(", code de sortie ").Append(LastExitCode.Value);
                sb.Append(", ").Append(LastElapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("fr-CA"))).Append(" s).");
                if (LastElapsed < TimeSpan.FromSeconds(3) && (LastExitCode ?? 0) == 0 && tail.Trim().Length == 0)
                    sb.Append(" Edge a rendu la main presque immédiatement sans imprimer : c'est ce qui se produit lorsqu'Edge est déjà ouvert et qu'une stratégie d'entreprise lui impose son profil utilisateur. Le moteur WebView2 intégré de l'application évite ce problème.");
                if (tail.Trim().Length > 0) sb.Append(" Détails : ").Append(tail.Trim());
                throw new PdfRenderException(sb.ToString());
            }
        }

        private string BuildArguments(string url, string outputPdfPath)
        {
            var sb = new StringBuilder();
            sb.Append("--headless ");
            sb.Append("--disable-gpu ");
            sb.Append("--no-first-run --no-default-browser-check --disable-extensions --disable-sync ");
            sb.Append("--disable-background-networking --disable-component-update --disable-default-apps ");
            sb.Append("--disable-breakpad --disable-crash-reporter --mute-audio --hide-scrollbars ");
            sb.Append("--disable-features=Translate,OptimizationHints,MediaRouter,msEdgeShoppingAssistant,msImplicitSignin ");
            sb.Append("--run-all-compositor-stages-before-draw ");
            sb.Append("--enable-logging=stderr --log-level=1 ");
            sb.Append("--virtual-time-budget=15000 ");
            sb.Append("--no-pdf-header-footer --print-to-pdf-no-header ");
            if (!AllowNetwork)
                sb.Append("--host-resolver-rules=\"MAP * ~NOTFOUND\" --proxy-server=\"direct://\" ");
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                sb.Append("--no-sandbox --disable-dev-shm-usage ");
            sb.Append("--user-data-dir=").Append(Quote(_profileDirectory)).Append(' ');
            sb.Append("--print-to-pdf=").Append(Quote(outputPdfPath)).Append(' ');
            sb.Append(Quote(url));
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            if (s.IndexOf('"') >= 0) s = s.Replace("\"", "\\\"");
            return "\"" + s + "\"";
        }

        private static void KillTree(Process process)
        {
            try
            {
                if (process.HasExited) return;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        using (var killer = Process.Start(new ProcessStartInfo("taskkill", "/PID " + process.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false }))
                            killer?.WaitForExit(5000);
                    }
                    catch { }
                }
                if (!process.HasExited) process.Kill();
            }
            catch
            {
                // best effort
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(_profileDirectory)) Directory.Delete(_profileDirectory, true);
                    return;
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
        }
    }
}
