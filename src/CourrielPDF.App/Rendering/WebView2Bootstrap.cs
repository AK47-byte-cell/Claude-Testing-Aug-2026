using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace CourrielPDF.App.Rendering
{
    /// <summary>
    /// The executable is a single file, so the native WebView2Loader.dll that the WebView2 SDK needs is carried as an
    /// embedded resource, extracted once under %LOCALAPPDATA% and handed to the SDK through SetLoaderDllFolderPath.
    /// </summary>
    internal static class WebView2Bootstrap
    {
        private static readonly object Sync = new object();
        private static bool _attempted;
        private static string? _error;
        private static string? _loaderPath;
        private static string? _runtimeVersion;
        private static string? _runtimeError;
        private static bool _runtimeChecked;

        public static string? LoaderPath => _loaderPath;
        public static string? LoaderError => _error;
        public static string SdkVersion => typeof(CoreWebView2Environment).Assembly.GetName().Version?.ToString() ?? "?";

        public static string UserDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CourrielPDF", "WebView2");

        /// <summary>Extracts the loader for the current process architecture and registers it. Safe to call repeatedly.</summary>
        public static bool EnsureLoader()
        {
            lock (Sync)
            {
                if (_attempted) return _error == null;
                _attempted = true;
                try
                {
                    string arch;
                    switch (RuntimeInformation.ProcessArchitecture)
                    {
                        case Architecture.X64: arch = "x64"; break;
                        case Architecture.X86: arch = "x86"; break;
                        case Architecture.Arm64: arch = "arm64"; break;
                        default: throw new PlatformNotSupportedException("Architecture de processeur non prise en charge : " + RuntimeInformation.ProcessArchitecture);
                    }

                    var resourceName = "CourrielPDF.App.WebView2Loader." + arch + ".dll";
                    byte[] bytes;
                    using (var stream = typeof(WebView2Bootstrap).Assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream == null) throw new FileNotFoundException("Ressource intégrée introuvable : " + resourceName);
                        using (var ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            bytes = ms.ToArray();
                        }
                    }

                    var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CourrielPDF", "webview2-loader", SdkVersion, arch);
                    Directory.CreateDirectory(folder);
                    var target = Path.Combine(folder, "WebView2Loader.dll");
                    if (!File.Exists(target) || new FileInfo(target).Length != bytes.LongLength)
                    {
                        try
                        {
                            File.WriteAllBytes(target, bytes);
                        }
                        catch (IOException) when (File.Exists(target))
                        {
                            // Another instance of the application holds the file; the existing copy will do.
                        }
                    }

                    CoreWebView2Environment.SetLoaderDllFolderPath(folder);
                    _loaderPath = target;
                    return true;
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                    return false;
                }
            }
        }

        /// <summary>Version of the installed WebView2 runtime, or null with a French explanation in <paramref name="error"/>.</summary>
        public static string? GetRuntimeVersion(out string? error)
        {
            lock (Sync)
            {
                if (_runtimeChecked)
                {
                    error = _runtimeError;
                    return _runtimeVersion;
                }
                _runtimeChecked = true;
                if (!EnsureLoader())
                {
                    _runtimeError = "Le chargeur WebView2 n'a pu être préparé : " + _error;
                    error = _runtimeError;
                    return null;
                }
                try
                {
                    _runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
                    if (string.IsNullOrWhiteSpace(_runtimeVersion))
                    {
                        _runtimeVersion = null;
                        _runtimeError = "Le composant « Microsoft Edge WebView2 Runtime » n'est pas installé sur cet ordinateur.";
                    }
                }
                catch (WebView2RuntimeNotFoundException)
                {
                    _runtimeError = "Le composant « Microsoft Edge WebView2 Runtime » n'est pas installé sur cet ordinateur.";
                }
                catch (Exception ex)
                {
                    _runtimeError = ex.GetType().Name + " : " + ex.Message;
                }
                error = _runtimeError;
                return _runtimeVersion;
            }
        }
    }
}
