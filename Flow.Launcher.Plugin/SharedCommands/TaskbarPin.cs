using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Flow.Launcher.Plugin.SharedCommands
{
    /// <summary>
    /// Pin and unpin items to the Windows taskbar.
    /// </summary>
    /// <remarks>
    /// Windows does not let other programs pin taskbar items: the "Taskband Pin" shell command reports success but
    /// does nothing unless it is chosen from Explorer's own context menu. So:
    /// <list type="bullet">
    /// <item>The pin state is read from the Taskband Pin command directly.</item>
    /// <item>Unpinning uses the documented IStartMenuPinnedList API (meant for uninstallers), which is silent. That API
    /// also removes Start menu tiles, so it is only used when Explorer reports that the item is not pinned to Start.</item>
    /// <item>Otherwise the item is selected in an Explorer window and "Pin to taskbar" / "Unpin from taskbar" is chosen
    /// from Explorer's own context menu. The window and the menu are made fully transparent the moment Explorer
    /// creates them, so nothing is seen, and the command is chosen by posting its access key to the menu, so the mouse
    /// cannot interfere.</item>
    /// </list>
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
        /// Windows' own localized text for the pin or unpin command, without the access key marker
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
        /// Pins the item if it is unpinned, or unpins it if it is pinned. Must be called on an STA thread.
        /// </summary>
        /// <param name="path">A file path, or <see cref="AppPath"/> for a packaged app</param>
        /// <returns>null on success, otherwise a description of what went wrong</returns>
        public static string Toggle(string path)
        {
            var before = GetState(path);
            if (before == PinState.NotPinnable) return "Windows cannot pin this item to the taskbar.";

            if (before == PinState.Pinned && IsPinnedToStart(path) == false && UnpinSilently(path))
            {
                return null;
            }

            return ChooseFromExplorerMenu(path, before);
        }

        #region Silent unpin

        private static bool UnpinSilently(string path)
        {
            try
            {
                var list = (IStartMenuPinnedList)Activator.CreateInstance(Type.GetTypeFromCLSID(StartMenuPinClsid));
                if (list.RemoveFromList(ShellItem(path)) != 0) return false;
                return WaitForState(path, PinState.Pinned, 20);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Whether Explorer offers "Unpin from Start" for the item; null when that cannot be determined
        /// </summary>
        private static bool? IsPinnedToStart(string path)
        {
            try
            {
                var unpinFromStart = LoadShell32String(UnpinFromStartId, "Un&pin from Start").Replace("&", string.Empty);
                var pinToStart = LoadShell32String(PinToStartId, "&Pin to Start").Replace("&", string.Empty);

                string folder, name;
                if (path.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    folder = "shell:AppsFolder";
                    name = path[AppsFolderPrefix.Length..];
                }
                else
                {
                    folder = Path.GetDirectoryName(path);
                    name = Path.GetFileName(path);
                }

                // The verbs are read through Explorer's own Shell.Application, because Start pinning verbs are
                // only listed inside Explorer
                dynamic item = ExplorerShellApplication().NameSpace(folder).ParseName(name);
                var offersPin = false;
                foreach (dynamic verb in item.Verbs())
                {
                    var verbName = ((string)verb.Name).Replace("&", string.Empty);
                    if (string.Equals(verbName, unpinFromStart, StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(verbName, pinToStart, StringComparison.OrdinalIgnoreCase)) offersPin = true;
                }
                return offersPin ? false : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static dynamic ExplorerShellApplication()
        {
            dynamic windows = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid));
            object location = CsidlDesktop;
            object empty = null;
            object desktop = windows.FindWindowSW(ref location, ref empty, SwcDesktop, out int _, SwfoNeedDispatch);

            var topLevelBrowser = SidSTopLevelBrowser;
            var shellBrowserId = typeof(IShellBrowser).GUID;
            ((IServiceProvider)desktop).QueryService(ref topLevelBrowser, ref shellBrowserId, out var browser);
            ((IShellBrowser)browser).QueryActiveShellView(out var view);
            var dispatchId = IidIDispatch;
            ((IShellView)view).GetItemObject(SvgioBackground, ref dispatchId, out var folderView);
            return ((dynamic)folderView).Application;
        }

        #endregion

        #region Explorer context menu

        // Explorer windows and menus created while a command is being chosen, made invisible as they appear
        private static HashSet<IntPtr> _existingExplorerWindows = new();
        private static HashSet<uint> _explorerProcessIds = new();
        private static WinEventProc _winEventProc;

        private static string ChooseFromExplorerMenu(string path, PinState before)
        {
            var menuText = MenuText(before == PinState.Unpinned);
            var amp = menuText.IndexOf('&');
            if (amp < 0 || amp + 1 >= menuText.Length) return "Could not find the taskbar command in Explorer's menu.";
            var accessKey = char.ToUpperInvariant(menuText[amp + 1]);

            _existingExplorerWindows = ExplorerWindowHandles();
            _explorerProcessIds = new HashSet<uint>();
            foreach (var process in Process.GetProcessesByName("explorer")) _explorerProcessIds.Add((uint)process.Id);
            _winEventProc = OnWinEvent;
            var hook = SetWinEventHook(EventObjectCreate, EventObjectShow, IntPtr.Zero, _winEventProc, 0, 0, WinEventOutOfContext);

            var window = IntPtr.Zero;
            try
            {
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

                for (var i = 0; i < 100 && window == IntPtr.Zero; i++)
                {
                    PumpMessages(50);
                    window = FindExplorerWindowSelecting(path);
                }
                if (window == IntPtr.Zero) return "Explorer did not show the item.";

                if (!BringToForeground(window))
                    return "Explorer could not be brought to the front. Please try again.";

                // Explorer only opens a context menu for a real key press, so press the context menu key once,
                // now that the (invisible) Explorer window is confirmed to be the foreground window
                PressKey(VkApps);

                var menu = IntPtr.Zero;
                for (var i = 0; i < 60 && menu == IntPtr.Zero; i++)
                {
                    PumpMessages(50);
                    menu = FindVisibleExplorerMenu();
                }
                if (menu == IntPtr.Zero) return "Explorer's context menu did not open.";

                // Give shell extensions time to add their items, then choose the command by its access key.
                // The key is posted to the menu itself, so it cannot reach any other window.
                PumpMessages(400);
                PostMessage(menu, WmKeyDown, new IntPtr(accessKey), new IntPtr(1));
                PostMessage(menu, WmChar, new IntPtr(char.ToLowerInvariant(accessKey)), new IntPtr(1));
                PostMessage(menu, WmKeyUp, new IntPtr(accessKey), unchecked(new IntPtr((int)0xC0000001)));

                if (WaitForState(path, before, 30)) return null;

                if (IsWindowVisible(menu)) PostMessage(menu, WmKeyDown, new IntPtr(VkEscape), new IntPtr(1));
                return "Windows did not change the taskbar. Please try again.";
            }
            finally
            {
                if (window != IntPtr.Zero && !_existingExplorerWindows.Contains(window))
                    PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
                PumpMessages(100);
                UnhookWinEvent(hook);
            }
        }

        private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
        {
            if (objectId != ObjidWindow || window == IntPtr.Zero || GetAncestor(window, GaRoot) != window) return;
            GetWindowThreadProcessId(window, out var processId);
            if (!_explorerProcessIds.Contains(processId)) return;

            var className = ClassName(window);
            var isNewExplorerWindow = className == "CabinetWClass" && !_existingExplorerWindows.Contains(window);
            if (isNewExplorerWindow || className == "#32768" || className == "SysShadow")
            {
                // Fully transparent layered windows are neither drawn nor hit by the mouse
                SetWindowLong(window, GwlExStyle, GetWindowLong(window, GwlExStyle) | WsExLayered);
                SetLayeredWindowAttributes(window, 0, 0, LwaAlpha);
            }
        }

        private static IntPtr FindVisibleExplorerMenu()
        {
            var menu = IntPtr.Zero;
            while ((menu = FindWindowEx(IntPtr.Zero, menu, "#32768", null)) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(menu, out var processId);
                if (IsWindowVisible(menu) && _explorerProcessIds.Contains(processId)) return menu;
            }
            return IntPtr.Zero;
        }

        private static bool BringToForeground(IntPtr window)
        {
            for (var i = 0; i < 10; i++)
            {
                if (GetForegroundWindow() == window) return true;
                // A key press lets this process take the foreground from whichever window has it
                PressKey(VkMenu);
                SetForegroundWindow(window);
                PumpMessages(100);
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

        #endregion

        #region Helpers

        private static string MenuText(bool pin) =>
            // Strings used by Explorer's own context menu: "Pin to tas&kbar" and "Unpin from tas&kbar"
            LoadShell32String(pin ? PinToTaskbarId : UnpinFromTaskbarId, pin ? "Pin to tas&kbar" : "Unpin from tas&kbar");

        private static string LoadShell32String(uint id, string fallback)
        {
            var module = LoadLibraryEx("shell32.dll", IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
            if (module == IntPtr.Zero) return fallback;
            try
            {
                var buffer = new StringBuilder(256);
                return LoadString(module, id, buffer, buffer.Capacity) > 0 ? buffer.ToString() : fallback;
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        private static bool WaitForState(string path, PinState before, int tries)
        {
            for (var i = 0; i < tries; i++)
            {
                PumpMessages(100);
                if (GetState(path) != before) return true;
            }
            return false;
        }

        private static IShellItem ShellItem(string path)
        {
            var itemId = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out var item);
            return item;
        }

        private static IShellItemArray ShellItemArray(string path)
        {
            var arrayId = typeof(IShellItemArray).GUID;
            SHCreateShellItemArrayFromShellItem(ShellItem(path), ref arrayId, out var array);
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

        // Waits while handling this thread's messages, which delivers the WinEvent hook callbacks
        private static void PumpMessages(int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds)
            {
                while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
                Thread.Sleep(5);
            }
        }

        private static void PressKey(byte key)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(30);
            keybd_event(key, 0, KeyEventKeyUp, UIntPtr.Zero);
        }

        private static string ClassName(IntPtr window)
        {
            var buffer = new StringBuilder(256);
            GetClassName(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        #endregion

        #region Native

        private static readonly Guid TaskbandPinClsid = new("90AA3A4E-1CBA-4233-B8BB-535773D48449");
        private static readonly Guid StartMenuPinClsid = new("a2a9545d-a0c2-42b4-9708-a0b2badd77c8");
        private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        private static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
        private static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");

        private const uint PinToTaskbarId = 5386;
        private const uint UnpinFromTaskbarId = 5387;
        private const uint PinToStartId = 51201;
        private const uint UnpinFromStartId = 51394;

        private const uint EcsDisabled = 0x1;
        private const uint EcsHidden = 0x2;
        private const uint EcsChecked = 0x8;
        private const int CsidlDesktop = 0;
        private const int SwcDesktop = 8;
        private const int SwfoNeedDispatch = 1;
        private const uint SvgioBackground = 0;
        private const byte VkApps = 0x5D;
        private const byte VkMenu = 0x12;
        private const byte VkEscape = 0x1B;
        private const uint KeyEventKeyUp = 0x2;
        private const uint WmClose = 0x0010;
        private const uint WmKeyDown = 0x0100;
        private const uint WmKeyUp = 0x0101;
        private const uint WmChar = 0x0102;
        private const uint PmRemove = 0x1;
        private const uint EventObjectCreate = 0x8000;
        private const uint EventObjectShow = 0x8002;
        private const uint WinEventOutOfContext = 0x0;
        private const int ObjidWindow = 0;
        private const uint GaRoot = 2;
        private const int GwlExStyle = -20;
        private const int WsExLayered = 0x80000;
        private const uint LwaAlpha = 0x2;
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

        [ComImport, Guid("4CD19ADA-25A5-4A32-B3B7-347BEE5BE36B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IStartMenuPinnedList
        {
            [PreserveSig] int RemoveFromList(IShellItem item);
        }

        [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            void QueryService([In] ref Guid service, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            void GetWindow(out IntPtr window);
            void ContextSensitiveHelp(bool enterMode);
            void InsertMenusSB(IntPtr sharedMenu, IntPtr menuWidths);
            void SetMenuSB(IntPtr sharedMenu, IntPtr oleMenu, IntPtr activeObject);
            void RemoveMenusSB(IntPtr sharedMenu);
            void SetStatusTextSB(IntPtr text);
            void EnableModelessSB(bool enable);
            void TranslateAcceleratorSB(IntPtr message, ushort id);
            void BrowseObject(IntPtr pidl, uint flags);
            void GetViewStateStream(uint mode, out IntPtr stream);
            void GetControlWindow(uint id, out IntPtr window);
            void SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
            void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
        }

        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellView
        {
            void GetWindow(out IntPtr window);
            void ContextSensitiveHelp(bool enterMode);
            void TranslateAccelerator(IntPtr message);
            void EnableModeless(bool enable);
            void UIActivate(uint state);
            void Refresh();
            void CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr window);
            void DestroyViewWindow();
            void GetCurrentInfo(IntPtr settings);
            void AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr lParam);
            void SaveViewState();
            void SelectItem(IntPtr pidl, uint flags);
            void GetItemObject(uint item, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Window;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [In] ref Guid riid, out IShellItem item);

        [DllImport("shell32.dll", PreserveSig = false)]
        private static extern void SHCreateShellItemArrayFromShellItem(IShellItem item, [In] ref Guid riid, out IShellItemArray array);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint count, IntPtr pidls, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out Msg message, IntPtr window, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Msg message);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr window, int index, int value);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int LoadString(IntPtr module, uint id, StringBuilder buffer, int bufferMax);

        #endregion
    }
}
