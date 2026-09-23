using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Flow.Launcher.Plugin.SharedCommands
{
    /// <summary>
    /// Pin and unpin items to the Windows taskbar.
    /// </summary>
    /// <remarks>
    /// Windows does not let other programs pin or unpin taskbar items: the "Taskband Pin" shell command reports
    /// success but does nothing unless it is chosen from Explorer's own context menu. So the state is read from that
    /// command directly, and pinning is done by selecting the item in an Explorer window and choosing
    /// "Pin to taskbar" / "Unpin from taskbar" from Explorer's context menu, exactly as a user would.
    /// </remarks>
    public static class TaskbarPin
    {
        /// <summary>
        /// Taskbar pin state of an item
        /// </summary>
        public enum PinState
        {
            /// <summary>Windows cannot pin this item to the taskbar (folders, documents, ...)</summary>
            NotPinnable,
            /// <summary>The item can be pinned and is not pinned</summary>
            Unpinned,
            /// <summary>The item is pinned to the taskbar</summary>
            Pinned
        }

        private const string AppsFolderPrefix = @"shell:AppsFolder\";

        /// <summary>
        /// Windows 10 "Pin" symbol from Segoe MDL2 Assets, as used in Start menu and jump list context menus
        /// </summary>
        public static readonly GlyphInfo PinGlyph = new("Segoe MDL2 Assets", "");

        /// <summary>
        /// Windows 10 "Unpin" symbol from Segoe MDL2 Assets, as used in Start menu and jump list context menus
        /// </summary>
        public static readonly GlyphInfo UnpinGlyph = new("Segoe MDL2 Assets", "");

        /// <summary>
        /// Path of a packaged (Store) app, to pass to the other methods of this class
        /// </summary>
        public static string AppPath(string appUserModelId) => AppsFolderPrefix + appUserModelId;

        /// <summary>
        /// Gets whether an item is pinned to the taskbar
        /// </summary>
        /// <param name="path">A file path, or <see cref="AppPath"/> for a packaged app</param>
        public static PinState GetState(string path)
        {
            if (string.IsNullOrEmpty(path)) return PinState.NotPinnable;

            try
            {
                return RunSta(() =>
                {
                    var command = (IExplorerCommand)Activator.CreateInstance(Type.GetTypeFromCLSID(TaskbandPinClsid));
                    var items = ShellItemArray(path);
                    if (command.GetState(items, true, out var state) != 0) return PinState.NotPinnable;
                    if ((state & (EcsDisabled | EcsHidden)) != 0) return PinState.NotPinnable;
                    return (state & EcsChecked) != 0 ? PinState.Pinned : PinState.Unpinned;
                });
            }
            catch (Exception)
            {
                return PinState.NotPinnable;
            }
        }

        /// <summary>
        /// Windows' own localized text for the pin or unpin command, without the keyboard accelerator marker
        /// </summary>
        public static string CommandText(bool pin) => MenuText(pin).Replace("&", string.Empty);

        /// <summary>
        /// Creates a "Pin to taskbar" or "Unpin from taskbar" context menu result, or returns null when Windows
        /// cannot pin the item to the taskbar
        /// </summary>
        /// <param name="path">A file path, or <see cref="AppPath"/> for a packaged app</param>
        /// <param name="api">Flow's public API, used to report failures</param>
        /// <param name="pinIcoPath">Image shown for "Pin to taskbar" when glyph icons are turned off</param>
        /// <param name="unpinIcoPath">Image shown for "Unpin from taskbar" when glyph icons are turned off</param>
        public static Result CreateContextMenuResult(string path, IPublicAPI api, string pinIcoPath, string unpinIcoPath)
        {
            var state = GetState(path);
            if (state == PinState.NotPinnable) return null;

            var pin = state == PinState.Unpinned;
            return new Result
            {
                Title = CommandText(pin),
                IcoPath = pin ? pinIcoPath : unpinIcoPath,
                Glyph = pin ? PinGlyph : UnpinGlyph,
                Action = _ =>
                {
                    // Runs after Flow's window has closed, because Explorer needs to be the foreground window
                    var thread = new Thread(() =>
                    {
                        Thread.Sleep(250);
                        var error = Toggle(path);
                        if (error != null)
                        {
                            api.ShowMsgError(CommandText(pin), error);
                        }
                    });
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.IsBackground = true;
                    thread.Start();
                    return true;
                }
            };
        }

        /// <summary>
        /// Pins the item if it is unpinned, or unpins it if it is pinned, through Explorer's context menu.
        /// Must be called on an STA thread while no other window is expected to take the keyboard focus.
        /// </summary>
        /// <param name="path">A file path, or <see cref="AppPath"/> for a packaged app</param>
        /// <returns>null on success, otherwise a description of what went wrong</returns>
        public static string Toggle(string path)
        {
            var before = GetState(path);
            if (before == PinState.NotPinnable) return "Windows cannot pin this item to the taskbar.";

            var menuText = MenuText(before == PinState.Unpinned);
            var amp = menuText.IndexOf('&');
            if (amp < 0 || amp + 1 >= menuText.Length) return "Could not find the taskbar command in Explorer's menu.";
            var accelerator = menuText[amp + 1];

            var existingWindows = ExplorerWindowHandles();
            if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return "Could not find the item.";
            try
            {
                if (SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0) != 0)
                    return "Could not open the item in Explorer.";
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }

            var window = IntPtr.Zero;
            for (var i = 0; i < 50 && window == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                window = FindExplorerWindowSelecting(path);
            }
            if (window == IntPtr.Zero) return "Explorer did not show the item.";

            var openedByUs = !existingWindows.Contains(window);
            try
            {
                if (!BringToForeground(window))
                    return "Explorer could not be brought to the front. Please try again.";

                // The keys must reach Explorer's file list with only the item selected, so check the focus once more
                PressKey(VkApps);
                var menu = IntPtr.Zero;
                for (var i = 0; i < 30; i++)
                {
                    Thread.Sleep(100);
                    menu = FindWindow("#32768", null);
                    if (menu != IntPtr.Zero && IsWindowVisible(menu)) break;
                    menu = IntPtr.Zero;
                }
                if (menu == IntPtr.Zero) return "Explorer's context menu did not open.";

                // Give shell extensions time to add their items, so the accelerator key reaches the complete menu
                Thread.Sleep(400);
                PressKey((byte)(VkKeyScan(char.ToLowerInvariant(accelerator)) & 0xFF));

                var after = before;
                for (var i = 0; i < 30 && after == before; i++)
                {
                    Thread.Sleep(100);
                    after = GetState(path);
                }

                if (after == before)
                {
                    // Close the menu if it is still open
                    if (IsWindowVisible(menu)) PressKey(VkEscape);
                    return "Windows did not change the taskbar. Please try again.";
                }

                return null;
            }
            finally
            {
                if (openedByUs) PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static string MenuText(bool pin)
        {
            // shell32 strings used by Explorer's own context menu: "Pin to tas&kbar" and "Unpin from tas&kbar"
            var module = LoadLibraryEx("shell32.dll", IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
            if (module != IntPtr.Zero)
            {
                try
                {
                    var buffer = new StringBuilder(256);
                    if (LoadString(module, pin ? 5386u : 5387u, buffer, buffer.Capacity) > 0) return buffer.ToString();
                }
                finally
                {
                    FreeLibrary(module);
                }
            }
            return pin ? "Pin to tas&kbar" : "Unpin from tas&kbar";
        }

        private static bool BringToForeground(IntPtr window)
        {
            for (var i = 0; i < 10; i++)
            {
                if (GetForegroundWindow() == window) return true;
                // A key press lets this process take the foreground from whichever window has it
                PressKey(VkMenu);
                SetForegroundWindow(window);
                Thread.Sleep(100);
            }
            return GetForegroundWindow() == window;
        }

        private static HashSet<IntPtr> ExplorerWindowHandles()
        {
            var handles = new HashSet<IntPtr>();
            foreach (dynamic window in ShellWindows())
            {
                try { handles.Add(new IntPtr((long)window.HWND)); }
                catch (Exception) { /* not an Explorer window */ }
            }
            return handles;
        }

        private static IntPtr FindExplorerWindowSelecting(string path)
        {
            foreach (dynamic window in ShellWindows())
            {
                try
                {
                    foreach (dynamic item in window.Document.SelectedItems())
                    {
                        if (SamePath((string)item.Path, path)) return new IntPtr((long)window.HWND);
                    }
                }
                catch (Exception)
                {
                    // Internet Explorer windows or windows that are still loading
                }
            }
            return IntPtr.Zero;
        }

        private static dynamic ShellWindows() => Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid));

        // Explorer reports items in shell:AppsFolder by their AppUserModelID only
        private static bool SamePath(string reported, string wanted)
        {
            if (string.Equals(reported, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            return wanted.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(reported, wanted[AppsFolderPrefix.Length..], StringComparison.OrdinalIgnoreCase);
        }

        private static IShellItemArray ShellItemArray(string path)
        {
            var itemId = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out var item);
            var arrayId = typeof(IShellItemArray).GUID;
            SHCreateShellItemArrayFromShellItem(item, ref arrayId, out var array);
            return array;
        }

        private static T RunSta<T>(Func<T> func)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return func();

            T result = default;
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { result = func(); }
                catch (Exception e) { error = e; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) throw error;
            return result;
        }

        private static void PressKey(byte key)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(30);
            keybd_event(key, 0, KeyEventKeyUp, UIntPtr.Zero);
        }

        #region Native

        private static readonly Guid TaskbandPinClsid = new("90AA3A4E-1CBA-4233-B8BB-535773D48449");
        private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

        private const uint EcsDisabled = 0x1;
        private const uint EcsHidden = 0x2;
        private const uint EcsChecked = 0x8;
        private const byte VkApps = 0x5D;
        private const byte VkMenu = 0x12;
        private const byte VkEscape = 0x1B;
        private const uint KeyEventKeyUp = 0x2;
        private const uint WmClose = 0x0010;
        private const uint LoadLibraryAsDatafile = 0x2;
        private const uint LoadLibraryAsImageResource = 0x20;

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem { }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray { }

        [ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IExplorerCommand
        {
            [PreserveSig] int GetTitle(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string title);
            [PreserveSig] int GetIcon(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string icon);
            [PreserveSig] int GetToolTip(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string tip);
            [PreserveSig] int GetCanonicalName(out Guid name);
            [PreserveSig] int GetState(IShellItemArray items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint state);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [In] ref Guid riid, out IShellItem item);

        [DllImport("shell32.dll", PreserveSig = false)]
        private static extern void SHCreateShellItemArrayFromShellItem(IShellItem item, [In] ref Guid riid, out IShellItemArray array);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint count, IntPtr pidls, uint flags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScan(char character);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int LoadString(IntPtr module, uint id, StringBuilder buffer, int bufferMax);

        #endregion
    }
}
