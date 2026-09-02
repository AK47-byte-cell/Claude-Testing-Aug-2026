using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using CourrielPDF.App.Rendering;
using CourrielPDF.Core;
using CourrielPDF.Core.Rendering;

namespace CourrielPDF.App
{
    /// <summary>Shows everything needed to understand a failure on a given machine, with a copy button.</summary>
    internal sealed class DiagnosticsForm : Form
    {
        private readonly TextBox _text;

        public DiagnosticsForm(IEnumerable<string> recentProblems)
        {
            Text = "CourrielPDF — Diagnostic";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(760, 560);
            MinimumSize = new Size(520, 360);
            ShowIcon = false;
            MinimizeBox = false;

            _text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f),
                BackColor = Color.White,
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
            var close = new Button { Text = "Fermer", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
            close.Click += (s, e) => Close();
            var copy = new Button { Text = "Copier le diagnostic", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(_text.Text); copy.Text = "Copié !"; } catch { }
            };
            buttons.Controls.Add(close);
            buttons.Controls.Add(copy);

            Controls.Add(_text);
            Controls.Add(buttons);
            CancelButton = close;

            _text.Text = Build(recentProblems);
            _text.Select(0, 0);
        }

        public static string Build(IEnumerable<string> recentProblems)
        {
            var sb = new StringBuilder();
            sb.AppendLine(ProductInfo.Name + " " + ProductInfo.Version + " — diagnostic du " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(new string('=', 70));
            sb.AppendLine("Windows            : " + Environment.OSVersion + (Environment.Is64BitOperatingSystem ? " (64 bits)" : " (32 bits)"));
            sb.AppendLine("Exécution .NET     : " + RuntimeInformation.FrameworkDescription + ", processus " + RuntimeInformation.ProcessArchitecture);
            sb.AppendLine("Exécutable         : " + SafePath(() => Application.ExecutablePath));
            sb.AppendLine("Dossier temporaire : " + SafePath(Path.GetTempPath));
            sb.AppendLine("Paramètres         : " + AppSettings.FilePath);
            sb.AppendLine();

            var runtime = WebView2Bootstrap.GetRuntimeVersion(out var runtimeError);
            sb.AppendLine("Moteur WebView2 (SDK " + WebView2Bootstrap.SdkVersion + ")");
            sb.AppendLine("  Runtime          : " + (runtime ?? "indisponible — " + runtimeError));
            sb.AppendLine("  Chargeur         : " + (WebView2Bootstrap.LoaderPath ?? "non extrait — " + WebView2Bootstrap.LoaderError));
            sb.AppendLine("  Données          : " + WebView2Bootstrap.UserDataFolder);
            sb.AppendLine();

            var edge = BrowserLocator.Find();
            sb.AppendLine("Microsoft Edge (mode secours)");
            sb.AppendLine("  Chemin           : " + (edge ?? "introuvable"));
            if (edge != null)
            {
                try { sb.AppendLine("  Version          : " + FileVersionInfo.GetVersionInfo(edge).FileVersion); } catch { }
            }
            sb.AppendLine("  Edge en cours    : " + (Process.GetProcessesByName("msedge").Length > 0 ? "oui" : "non"));
            sb.AppendLine();

            sb.AppendLine("Outlook classique  : " + (OutlookBridge.IsOutlookRunning() ? "ouvert" : "non détecté"));
            sb.AppendLine();

            sb.AppendLine("Derniers problèmes signalés");
            var any = false;
            foreach (var p in recentProblems)
            {
                any = true;
                sb.AppendLine("  - " + p);
            }
            if (!any) sb.AppendLine("  (aucun)");
            return sb.ToString();
        }

        private static string SafePath(Func<string> getter)
        {
            try { return getter(); } catch (Exception ex) { return "(" + ex.Message + ")"; }
        }
    }
}
