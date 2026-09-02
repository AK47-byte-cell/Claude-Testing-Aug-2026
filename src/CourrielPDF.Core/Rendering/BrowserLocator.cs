using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace CourrielPDF.Core.Rendering
{
    /// <summary>Finds a Chromium-based browser (Microsoft Edge first) able to print HTML to PDF.</summary>
    public static class BrowserLocator
    {
        public const string EnvironmentVariable = "COURRIELPDF_BROWSER";

        public static string? Find(string? preferredPath = null)
        {
            foreach (var candidate in Candidates(preferredPath))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch
                {
                    // ignore odd paths
                }
            }
            return null;
        }

        public static IEnumerable<string> Candidates(string? preferredPath)
        {
            if (!string.IsNullOrWhiteSpace(preferredPath)) yield return preferredPath!;

            var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(env)) yield return env!;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var p in RegistryAppPaths("msedge.exe")) yield return p;
                var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                var pf = Environment.GetEnvironmentVariable("ProgramFiles");
                var local = Environment.GetEnvironmentVariable("LocalAppData");
                var pfw = Environment.GetEnvironmentVariable("ProgramW6432");
                foreach (var root in new[] { pf86, pf, pfw, local })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    yield return Path.Combine(root!, "Microsoft", "Edge", "Application", "msedge.exe");
                }
                foreach (var p in RegistryAppPaths("chrome.exe")) yield return p;
                foreach (var root in new[] { pf86, pf, pfw, local })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    yield return Path.Combine(root!, "Google", "Chrome", "Application", "chrome.exe");
                }
                foreach (var name in new[] { "msedge.exe", "chrome.exe", "chromium.exe" })
                    foreach (var p in OnPath(name)) yield return p;
            }
            else
            {
                foreach (var name in new[] { "microsoft-edge", "microsoft-edge-stable", "msedge", "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "chrome" })
                    foreach (var p in OnPath(name)) yield return p;
                yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
                yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            }
        }

        private static IEnumerable<string> RegistryAppPaths(string exe)
        {
            var results = new List<string>();
            foreach (var hive in new[] { "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER" })
            {
                foreach (var sub in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" })
                {
                    try
                    {
                        var v = Microsoft.Win32.Registry.GetValue(hive + "\\" + sub + exe, null, null) as string;
                        if (!string.IsNullOrWhiteSpace(v)) results.Add(v!.Trim('"'));
                    }
                    catch
                    {
                        // registry unavailable (non-Windows) or access denied
                    }
                }
            }
            return results;
        }

        private static IEnumerable<string> OnPath(string name)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate;
                try { candidate = Path.Combine(dir.Trim(), name); }
                catch { continue; }
                yield return candidate;
            }
        }
    }
}
