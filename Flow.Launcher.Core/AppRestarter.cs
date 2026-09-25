using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Flow.Launcher.Infrastructure;
using Squirrel;

namespace Flow.Launcher.Core
{
    /// <summary>
    /// Restarts Flow Launcher. Squirrel's restart needs its Update.exe, which only a Squirrel install has; a copy of
    /// the app folder anywhere else (such as a self-built install) restarts without it.
    /// </summary>
    public static class AppRestarter
    {
        private static string SquirrelUpdateExe => Path.Combine(Constant.ApplicationDirectory, "Update.exe");

        /// <summary>
        /// Starts Flow Launcher again and ends this process. Like Squirrel's restart, this calls
        /// <see cref="Environment.Exit"/>, so save settings first.
        /// </summary>
        public static void Restart()
        {
            if (File.Exists(SquirrelUpdateExe))
            {
                UpdateManager.RestartApp(Constant.ApplicationFileName);
                return;
            }

            // Flow runs as a single instance, so the new one may only start once this one has exited: a hidden
            // PowerShell waits for this process, then starts Flow again
            var script = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; " +
                         $"Start-Process -FilePath '{Constant.ExecutablePath.Replace("'", "''")}'";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Constant.ProgramDirectory
            });

            Environment.Exit(0);
        }
    }
}
