using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Flow.Launcher.Plugin.Program
{
    /// <summary>
    /// Reads the launch counts Windows keeps in UserAssist (the same data the Start menu uses for "most used"),
    /// so the home page can show top apps like the Windows 10 search panel does.
    /// </summary>
    internal static class TopApps
    {
        private const string UserAssistKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

        // Executables and shortcuts
        private static readonly string[] CountGuids =
        {
            "{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}",
            "{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}"
        };

        internal record Usage(int RunCount, DateTime LastRun);

        /// <summary>
        /// Key is either a full path (exe or lnk) or an AppUserModelID, compared case insensitively
        /// </summary>
        internal static Dictionary<string, Usage> Read()
        {
            var usages = new Dictionary<string, Usage>(StringComparer.OrdinalIgnoreCase);

            foreach (var guid in CountGuids)
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{UserAssistKey}\{guid}\Count");
                if (key == null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is not byte[] data || data.Length < 68) continue;

                    var runCount = BitConverter.ToInt32(data, 4);
                    if (runCount <= 0) continue;

                    var fileTime = BitConverter.ToInt64(data, 60);
                    var lastRun = fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;

                    var name = ExpandKnownFolder(Rot13(valueName));
                    if (usages.TryGetValue(name, out var existing))
                    {
                        usages[name] = new Usage(existing.RunCount + runCount,
                            existing.LastRun > lastRun ? existing.LastRun : lastRun);
                    }
                    else
                    {
                        usages[name] = new Usage(runCount, lastRun);
                    }
                }
            }

            return usages;
        }

        private static string Rot13(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                sb.Append(c switch
                {
                    >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                    >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                    _ => c
                });
            }
            return sb.ToString();
        }

        // Entries look like "{KNOWNFOLDERID}\Sub\Path.exe"
        private static string ExpandKnownFolder(string name)
        {
            if (name.Length < 39 || name[0] != '{' || name[37] != '}') return name;
            if (!Guid.TryParse(name.AsSpan(0, 38), out var folderId)) return name;

            try
            {
                if (SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var pathPtr) != 0) return name;
                try
                {
                    var folder = Marshal.PtrToStringUni(pathPtr);
                    return string.IsNullOrEmpty(folder) ? name : Path.Combine(folder, name[38..].TrimStart('\\'));
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }
            }
            catch
            {
                return name;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        internal static Usage Find(Dictionary<string, Usage> usages, IEnumerable<string> keys)
        {
            Usage best = null;
            foreach (var k in keys)
            {
                if (string.IsNullOrEmpty(k) || !usages.TryGetValue(k, out var u)) continue;
                if (best == null || u.RunCount > best.RunCount) best = u;
            }
            return best;
        }
    }
}
