using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Flow.Launcher.Plugin.Explorer.Search
{
    /// <summary>
    /// Recent files from the Windows Recent folder, shown on the home page like the Windows 10 search panel
    /// </summary>
    internal static class RecentFiles
    {
        private const int MaxResults = 8;
        private const int MaxShortcutsToScan = 60;
        private const int BaseScore = 50000;

        private static readonly Query EmptyQuery = new() { ActionKeyword = string.Empty };

        internal static List<Result> Get(CancellationToken token)
        {
            var recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (!Directory.Exists(recentDir)) return new List<Result>();

            var shortcuts = new System.IO.DirectoryInfo(recentDir)
                .EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxShortcutsToScan)
                .ToList();

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return new List<Result>();
            dynamic shell = Activator.CreateInstance(shellType);

            var results = new List<Result>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var shortcut in shortcuts)
                {
                    token.ThrowIfCancellationRequested();

                    string target;
                    try
                    {
                        target = (string)shell.CreateShortcut(shortcut.FullName).TargetPath;
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(target) || !seen.Add(target) || !File.Exists(target)) continue;

                    var result = ResultManager.CreateFileResult(target, EmptyQuery, BaseScore - results.Count);
                    result.SubTitle = Localize.plugin_explorer_recent_file(Path.GetDirectoryName(target));
                    result.TitleHighlightData = null;
                    results.Add(result);

                    if (results.Count >= MaxResults) break;
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }

            return results;
        }
    }
}
