using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CourrielPDF.Core.Reading;

namespace CourrielPDF.Core.Conversion
{
    /// <summary>Turns a mix of dropped files and folders into the ordered list of e-mail files to convert.</summary>
    public static class InputExpander
    {
        public static List<string> Expand(IEnumerable<string> paths, bool recurseFolders = true)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                string full;
                try { full = Path.GetFullPath(p.Trim()); } catch { continue; }

                if (Directory.Exists(full))
                {
                    IEnumerable<string> files;
                    try
                    {
                        files = Directory.EnumerateFiles(full, "*.*", recurseFolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                            .Where(EmailLoader.IsSupported)
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        continue;
                    }
                    foreach (var f in files)
                        if (seen.Add(f)) result.Add(f);
                }
                else if (File.Exists(full))
                {
                    if (seen.Add(full)) result.Add(full);
                }
            }
            return result;
        }
    }
}
