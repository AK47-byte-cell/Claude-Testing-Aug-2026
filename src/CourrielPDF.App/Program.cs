using System;
using System.Threading;
using System.Windows.Forms;

namespace CourrielPDF.App
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length >= 3 && string.Equals(args[0], "--autotest", StringComparison.OrdinalIgnoreCase))
            {
                // Unattended self-test used by the build pipeline.
                Application.Run(new AutoTestForm(args[1], args[2], args.Length > 3 ? args[3] : null));
                return Environment.ExitCode;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowFatal(e.ExceptionObject as Exception);
            Application.Run(new MainForm());
            return 0;
        }

        private static void ShowFatal(Exception? ex)
        {
            try
            {
                MessageBox.Show("Une erreur imprévue s'est produite :" + Environment.NewLine + Environment.NewLine + (ex?.Message ?? "(inconnue)"),
                    "CourrielPDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
            }
        }
    }
}
