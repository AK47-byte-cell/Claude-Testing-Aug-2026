using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace CourrielPDF.App.Rendering
{
    /// <summary>
    /// The WebView2 SDK needs Microsoft's native WebView2Loader.dll. It is shipped, unmodified and Microsoft-signed,
    /// as a plain file next to CourrielPDF.exe (with x86 and arm64 copies in sub-folders). Nothing is ever extracted
    /// or written to disk at run time: antivirus heuristics treat programs that drop executable files as suspicious.
    /// </summary>
    internal static class WebView2Bootstrap
    {
        public const string LoaderFileName = "WebView2Loader.dll";

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

        public static string ProcessArchitectureName
        {
            get
            {
                switch (RuntimeInformation.ProcessArchitecture)
                {
                    case Architecture.X64: return "x64";
                    case Architecture.X86: return "x86";
                    case Architecture.Arm64: return "arm64";
                    default: return RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
                }
            }
        }

        /// <summary>Locates the loader beside the executable and registers it with the SDK. Safe to call repeatedly.</summary>
        public static bool EnsureLoader()
        {
            lock (Sync)
            {
                if (_attempted) return _error == null;
                _attempted = true;
                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Directory.GetCurrentDirectory();
                    var arch = ProcessArchitectureName;
                    var candidates = new[]
                    {
                        Path.Combine(baseDir, arch, LoaderFileName),
                        Path.Combine(baseDir, LoaderFileName),
                        Path.Combine(baseDir, "runtimes", "win-" + arch, "native", LoaderFileName),
                    };

                    string? found = null;
                    string? mismatch = null;
                    foreach (var c in candidates)
                    {
                        if (!File.Exists(c)) continue;
                        var machine = ReadPeMachine(c);
                        if (machine == null || MachineMatches(machine.Value))
                        {
                            found = c;
                            break;
                        }
                        mismatch = c + " (compilé pour " + MachineName(machine.Value) + ")";
                    }

                    if (found == null)
                    {
                        _error = "Le fichier " + LoaderFileName + " est introuvable à côté de CourrielPDF.exe (dossier " + baseDir + "). "
                                 + "Décompressez l'archive complète et gardez les fichiers ensemble."
                                 + (mismatch != null ? " Fichier ignoré : " + mismatch + "." : string.Empty);
                        return false;
                    }

                    CoreWebView2Environment.SetLoaderDllFolderPath(Path.GetDirectoryName(found)!);
                    _loaderPath = found;
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
                    _runtimeError = _error;
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

        // ---- PE header inspection: make sure the DLL matches the process architecture before loading it ----

        private static ushort? ReadPeMachine(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x40) return null;
                    if (br.ReadUInt16() != 0x5A4D) return null; // "MZ"
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    var peOffset = br.ReadInt32();
                    if (peOffset <= 0 || peOffset + 6 > fs.Length) return null;
                    fs.Seek(peOffset, SeekOrigin.Begin);
                    if (br.ReadUInt32() != 0x00004550) return null; // "PE\0\0"
                    return br.ReadUInt16();
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool MachineMatches(ushort machine)
        {
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64: return machine == 0x8664;
                case Architecture.X86: return machine == 0x014C;
                case Architecture.Arm64: return machine == 0xAA64;
                default: return true;
            }
        }

        private static string MachineName(ushort machine)
        {
            switch (machine)
            {
                case 0x8664: return "x64";
                case 0x014C: return "x86";
                case 0xAA64: return "arm64";
                default: return "0x" + machine.ToString("X4");
            }
        }
    }
}
