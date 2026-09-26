using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using Flow.Launcher.Infrastructure;
using Flow.Launcher.Infrastructure.UserSettings;
using Flow.Launcher.Plugin.SharedCommands;

namespace Flow.Launcher.Helper;

/// <summary>
/// Flow's taskbar button, like the Windows 10 search button: Flow's icon with the tooltip "Type here to search".
/// <para>
/// The taskbar names a pinned app after what its AppUserModelID resolves to, which for Flow.Launcher.exe is the
/// Start menu shortcut "Flow Launcher". So Flow pins a shortcut of its own instead, named "Type here to search"
/// (Flow's translation of the search box text) with an AppUserModelID of its own, kept outside the Start menu so
/// Start and search keep calling the app Flow Launcher.
/// </para>
/// </summary>
public static class TaskbarSearchButton
{
    private const string AppId = "Flow.Launcher.TypeHereToSearch";

    // Explorer must be up and Flow's window out of the way before pinning through Explorer's menu
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// On every start, brings the button's shortcut (and the taskbar's copy of it, if pinned) up to date with this
    /// Flow. On first start, also replaces a pinned Flow.Launcher.exe with the button, or pins it; that runs once,
    /// so unpinning the button later is respected.
    /// </summary>
    public static void SetUp(Settings settings)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(StartDelay);
            try
            {
                var name = ShortcutName();
                var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FlowLauncher", name);
                Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
                WriteShortcut(shortcut);

                // The taskbar starts its own copy of a pinned shortcut
                var pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar", name);
                if (File.Exists(pinned)) WriteShortcut(pinned);

                if (settings.TaskbarSearchButtonSetUp) return;
                App.API.LogInfo(nameof(TaskbarSearchButton), $"Shortcut ready: {shortcut}");

                // The plain Flow.Launcher.exe pin would be a second button with the old name
                var exeState = TaskbarPin.GetState(Constant.ExecutablePath);
                App.API.LogInfo(nameof(TaskbarSearchButton), $"Flow.Launcher.exe pin: {exeState}");
                if (exeState == TaskbarPin.PinState.Pinned)
                {
                    TaskbarPin.Toggle(Constant.ExecutablePath);
                }

                var error = TaskbarPin.GetState(shortcut) == TaskbarPin.PinState.Unpinned
                    ? TaskbarPin.Toggle(shortcut)
                    : null;
                if (error == null)
                {
                    App.API.LogInfo(nameof(TaskbarSearchButton), "Search button pinned");
                    settings.TaskbarSearchButtonSetUp = true;
                    settings.Save();
                }
                else
                {
                    App.API.LogError(nameof(TaskbarSearchButton), $"Could not pin the search button: {error}");
                }
            }
            catch (Exception e)
            {
                App.API.LogException(nameof(TaskbarSearchButton), "Could not set up the search button", e);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    // "Type here to search.lnk": Flow's translation of the search box text, which becomes the tooltip
    private static string ShortcutName()
    {
        var name = App.API.GetTranslation("queryTextBoxPlaceholder");
        name = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (string.IsNullOrEmpty(name)) name = "Type here to search";
        return name + ".lnk";
    }

    // Writes the button's shortcut: this Flow with --search (open straight into the search layout) and the button's
    // own AppUserModelID
    private static void WriteShortcut(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(Constant.ExecutablePath);
            link.SetArguments(MainWindow.SearchArgument);
            link.SetWorkingDirectory(Constant.ProgramDirectory);
            link.SetIconLocation(Constant.ExecutablePath, 0);

            var store = (IPropertyStore)link;
            var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5); // System.AppUserModel.ID
            var value = new PropVariant { vt = 31, pointer = Marshal.StringToCoTaskMemUni(AppId) }; // VT_LPWSTR
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                PropVariantClear(ref value);
            }

            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    #region Interop

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr file, int max, IntPtr data, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(IntPtr path, int max, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;

        public PropertyKey(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    #endregion
}
