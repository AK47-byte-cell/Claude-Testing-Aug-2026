using System;
using System.Threading;
using System.Windows.Forms;

namespace CourrielPDF.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowFatal(e.ExceptionObject as Exception);
            Application.Run(new MainForm());
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
