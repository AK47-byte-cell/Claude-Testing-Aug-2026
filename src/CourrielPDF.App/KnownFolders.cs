using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CourrielPDF.App
{
    /// <summary>Windows known folders that the .NET Framework does not expose through Environment.SpecialFolder.</summary>
    internal static class KnownFolders
    {
        private static readonly Guid DownloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
        private static extern string SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken);

        /// <summary>The user's Downloads folder (redirections included), or a sensible fallback.</summary>
        public static string Downloads
        {
            get
            {
                try
                {
                    var path = SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero);
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
                catch
                {
                    // Fall through to the conventional location.
                }
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var candidate = Path.Combine(profile, "Downloads");
                return Directory.Exists(candidate) ? candidate : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
        }
    }
}
