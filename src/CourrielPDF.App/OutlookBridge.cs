using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using CourrielPDF.Core.Naming;

namespace CourrielPDF.App
{
    internal sealed class OutlookUnavailableException : Exception
    {
        public OutlookUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// Talks to the classic Outlook desktop application through late-bound COM automation.
    /// When messages are dragged straight out of Outlook there is no file on disk; Outlook only offers
    /// virtual "FileGroupDescriptor" items. We therefore read Outlook's current selection (the items being
    /// dragged are always the selected ones) and ask Outlook to save each of them as a .msg file.
    /// </summary>
    internal static class OutlookBridge
    {
        private const int OlMsgUnicode = 9;
        private const uint RpcCallRejected = 0x80010001;
        private const uint RpcServerCallRetryLater = 0x8001010A;

        public static bool IsOutlookData(IDataObject data)
        {
            if (data == null) return false;
            if (data.GetDataPresent(DataFormats.FileDrop)) return false;
            return data.GetDataPresent("FileGroupDescriptorW")
                   || data.GetDataPresent("FileGroupDescriptor")
                   || data.GetDataPresent("RenPrivateMessages")
                   || data.GetDataPresent("RenPrivateItem");
        }

        /// <summary>Names of the virtual files offered by the drag source (for display and cross-checking).</summary>
        public static List<string> ReadDraggedNames(IDataObject data)
        {
            var names = new List<string>();
            try
            {
                if (data.GetDataPresent("FileGroupDescriptorW") && data.GetData("FileGroupDescriptorW") is Stream sw)
                    ReadDescriptor(sw, true, names);
                else if (data.GetDataPresent("FileGroupDescriptor") && data.GetData("FileGroupDescriptor") is Stream sa)
                    ReadDescriptor(sa, false, names);
            }
            catch
            {
                // Purely informative; ignore parsing problems.
            }
            return names;
        }

        private static void ReadDescriptor(Stream stream, bool unicode, List<string> names)
        {
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                var b = ms.ToArray();
                if (b.Length < 4) return;
                var count = BitConverter.ToInt32(b, 0);
                var itemSize = unicode ? 592 : 332;
                var nameOffset = 72;
                var nameLength = unicode ? 520 : 260;
                for (var i = 0; i < count && i < 2000; i++)
                {
                    var start = 4 + i * itemSize + nameOffset;
                    if (start + nameLength > b.Length) break;
                    var raw = unicode ? Encoding.Unicode.GetString(b, start, nameLength) : Encoding.Default.GetString(b, start, nameLength);
                    var nul = raw.IndexOf('\0');
                    if (nul >= 0) raw = raw.Substring(0, nul);
                    if (raw.Length > 0) names.Add(raw);
                }
            }
        }

        public static bool IsOutlookRunning()
        {
            try
            {
                var app = Marshal.GetActiveObject("Outlook.Application");
                Marshal.FinalReleaseComObject(app);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Saves every item currently selected in the active Outlook window as a .msg file inside <paramref name="folder"/>.</summary>
        public static List<string> SaveSelectionAsMsg(string folder, List<string> warnings)
        {
            var saved = new List<string>();
            object? app = null;
            object? explorer = null;
            object? selection = null;
            try
            {
                try
                {
                    app = Retry(() => Marshal.GetActiveObject("Outlook.Application"));
                }
                catch (COMException)
                {
                    throw new OutlookUnavailableException("Outlook (version classique) n'est pas ouvert ou n'est pas accessible. Ouvrez Outlook, sélectionnez les courriels, puis réessayez.");
                }

                explorer = Retry(() => Invoke(app!, "ActiveExplorer"));
                if (explorer == null)
                    throw new OutlookUnavailableException("Aucune fenêtre Outlook active. Ouvrez la liste des courriels dans Outlook, puis réessayez.");

                selection = Retry(() => GetProperty(explorer!, "Selection"));
                if (selection == null)
                    throw new OutlookUnavailableException("Impossible de lire la sélection dans Outlook.");

                var count = Convert.ToInt32(Retry(() => GetProperty(selection!, "Count")));
                Directory.CreateDirectory(folder);

                for (var i = 1; i <= count; i++)
                {
                    object? item = null;
                    try
                    {
                        item = Retry(() => Invoke(selection!, "Item", i));
                        if (item == null) continue;
                        string subject;
                        try { subject = (Retry(() => GetProperty(item!, "Subject")) as string) ?? string.Empty; }
                        catch { subject = string.Empty; }
                        var baseName = OutputNaming.Sanitize(string.IsNullOrWhiteSpace(subject) ? "(sans objet)" : subject, 100);
                        if (baseName.Length == 0) baseName = "(sans objet)";
                        var path = OutputNaming.MakeUniquePath(folder, baseName, ".msg");
                        var p = path;
                        Retry(() => Invoke(item!, "SaveAs", p, OlMsgUnicode));
                        if (File.Exists(path)) saved.Add(path);
                        else warnings.Add("Outlook n'a pas enregistré l'élément « " + baseName + " ».");
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Un élément sélectionné n'a pu être exporté depuis Outlook (" + Unwrap(ex).Message + ").");
                    }
                    finally
                    {
                        Release(item);
                    }
                }
            }
            finally
            {
                Release(selection);
                Release(explorer);
                Release(app);
            }
            return saved;
        }

        private static object? Invoke(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, args);
        }

        private static object? GetProperty(object target, string name)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }

        /// <summary>Outlook rejects incoming calls while it is busy (e.g. finishing its own drag-and-drop); retry politely.</summary>
        private static T Retry<T>(Func<T> action)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return action();
                }
                catch (Exception ex)
                {
                    var com = Unwrap(ex) as COMException;
                    var code = com != null ? (uint)com.ErrorCode : 0u;
                    if (com != null && (code == RpcCallRejected || code == RpcServerCallRetryLater) && attempt < 20)
                    {
                        Thread.Sleep(250);
                        continue;
                    }
                    if (com != null) throw com;
                    throw;
                }
            }
        }

        private static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
            return ex;
        }

        private static void Release(object? o)
        {
            try
            {
                if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
            }
            catch
            {
            }
        }
    }
}
