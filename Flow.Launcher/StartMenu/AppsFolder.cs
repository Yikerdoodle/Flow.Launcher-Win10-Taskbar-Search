using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Flow.Launcher.StartMenu;

/// <summary>
/// An app in the Windows Apps folder (<c>shell:AppsFolder</c>), the list the Windows 10 Start menu's All apps is
/// made from.
/// </summary>
public sealed class AppsFolderItem
{
    /// <summary>Display name, as Start shows it.</summary>
    public string Name { get; init; }

    /// <summary>
    /// The app's ID in the Apps folder: an AppUserModelID for Store apps and apps that set one, otherwise the target
    /// path (with a known-folder GUID in place of Program Files and the like). <c>shell:AppsFolder\{AppId}</c> opens it.
    /// </summary>
    public string AppId { get; init; }

    /// <summary>
    /// The Start menu folder the app is in (the Programs subfolder of its shortcut), or null at the top level.
    /// </summary>
    public string Folder { get; init; }

    /// <summary>Whether the app is a packaged (Store) app.</summary>
    public bool IsPackaged { get; init; }

    /// <summary>
    /// When the app was installed, as far as files show: when a packaged app first got its app data folder (updates
    /// don't change that), otherwise the earlier of when its shortcut and its program's folder were created.
    /// <see cref="DateTime.MinValue"/> when unknown, and for web links.
    /// </summary>
    public DateTime InstalledTime { get; init; }

    public string ParsingPath => @"shell:AppsFolder\" + AppId;
}

/// <summary>
/// Reads the Windows Apps folder through the shell.
/// </summary>
public static class AppsFolder
{
    public static List<AppsFolderItem> GetItems()
    {
        var items = new List<AppsFolderItem>();

        var suiteKey = PropertyKey("System.Tile.SuiteDisplayName");
        var familyNameKey = PropertyKey("System.AppUserModel.PackageFamilyName");
        var bestShortcutKey = PropertyKey("System.AppUserModel.BestShortcut");
        var targetKey = PropertyKey("System.Link.TargetParsingPath");

        var shellItemId = typeof(IShellItem2).GUID;
        SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref shellItemId, out var folder);
        try
        {
            var enumId = typeof(IEnumShellItems).GUID;
            var bhidEnumItems = BHID_EnumItems;
            folder.BindToHandler(IntPtr.Zero, ref bhidEnumItems, ref enumId, out var enumObject);
            var enumerator = (IEnumShellItems)enumObject;
            try
            {
                while (enumerator.Next(1, out var item, out var fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        var shellItem = (IShellItem2)item;
                        var name = GetDisplayName(shellItem, SIGDN_NORMALDISPLAY);
                        var appId = GetDisplayName(shellItem, SIGDN_PARENTRELATIVEPARSING);
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(appId)) continue;

                        var familyName = GetString(shellItem, familyNameKey);
                        var packaged = !string.IsNullOrEmpty(familyName);
                        items.Add(new AppsFolderItem
                        {
                            Name = name,
                            AppId = appId,
                            Folder = NullIfEmpty(GetString(shellItem, suiteKey)),
                            IsPackaged = packaged,
                            InstalledTime = packaged
                                ? DirectoryCreationTime(PackageDataFolder(familyName))
                                : DesktopAppInstallTime(shellItem, appId, bestShortcutKey, targetKey)
                        });
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }

        return items;
    }

    /// <summary>
    /// The app's icon at the given size in pixels, as a GDI bitmap the caller must free with DeleteObject, or zero.
    /// </summary>
    public static IntPtr GetIconBitmap(string parsingPath, int size)
    {
        var imageFactoryId = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingNameImage(parsingPath, IntPtr.Zero, ref imageFactoryId, out var factory) != 0 ||
            factory == null)
        {
            return IntPtr.Zero;
        }

        try
        {
            return factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out var bitmap) == 0
                ? bitmap
                : IntPtr.Zero;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static string NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string PackageDataFolder(string familyName) =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", familyName);

    // The earlier of when the shortcut and the folder of the program it opens were created: updates often recreate the
    // shortcut, but seldom the program's folder. Web links are never counted as installed apps.
    private static DateTime DesktopAppInstallTime(IShellItem2 item, string appId, PROPERTYKEY bestShortcutKey, PROPERTYKEY targetKey)
    {
        if (appId.Contains("://")) return DateTime.MinValue;

        var shortcutTime = ShortcutCreationTime(item, bestShortcutKey);
        var target = GetString(item, targetKey);
        var folderTime = string.IsNullOrEmpty(target) ? DateTime.MinValue : DirectoryCreationTime(System.IO.Path.GetDirectoryName(target));

        if (shortcutTime == DateTime.MinValue) return folderTime;
        if (folderTime == DateTime.MinValue) return shortcutTime;
        return folderTime < shortcutTime ? folderTime : shortcutTime;
    }

    private static DateTime DirectoryCreationTime(string path)
    {
        try
        {
            return System.IO.Directory.Exists(path) ? System.IO.Directory.GetCreationTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    // The app's best shortcut is stored as an ID list; its file's creation time is when the app was added to Start
    private static DateTime ShortcutCreationTime(IShellItem2 item, PROPERTYKEY bestShortcutKey)
    {
        var value = new PROPVARIANT();
        try
        {
            if (item.GetProperty(ref bestShortcutKey, ref value) != 0 || value.vt != VT_VECTOR_UI1) return DateTime.MinValue;

            var path = new System.Text.StringBuilder(MAX_PATH_LONG);
            if (!SHGetPathFromIDListEx(value.pointer2, path, path.Capacity, 0)) return DateTime.MinValue;
            return System.IO.File.GetCreationTimeUtc(path.ToString());
        }
        catch
        {
            return DateTime.MinValue;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static string GetDisplayName(IShellItem2 item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out var pointer) != 0 || pointer == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static string GetString(IShellItem2 item, PROPERTYKEY key)
    {
        return item.GetString(ref key, out var pointer) == 0 && pointer != IntPtr.Zero
            ? TakeString(pointer)
            : null;
    }

    private static string TakeString(IntPtr pointer)
    {
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static PROPERTYKEY PropertyKey(string canonicalName)
    {
        Marshal.ThrowExceptionForHR(PSGetPropertyKeyFromName(canonicalName, out var key));
        return key;
    }

    #region Interop

    private const uint SIGDN_NORMALDISPLAY = 0;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    private const int SIIGBF_ICONONLY = 0x4;
    private const ushort VT_VECTOR_UI1 = 0x1000 | 17;
    private const int MAX_PATH_LONG = 32768;
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // A PROPVARIANT for the vector case: vt, three reserved words, then the count and the element pointer
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr pointer1;
        public IntPtr pointer2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // IShellItem
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IntPtr other, uint hint, out int order);

        // IShellItem2
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr store);
        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr createObject, ref Guid riid, out IntPtr store);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr keys, uint count, int flags, ref Guid riid, out IntPtr store);
        [PreserveSig] int GetPropertyDescriptionList(ref PROPERTYKEY type, ref Guid riid, out IntPtr list);
        [PreserveSig] int Update(IntPtr bindContext);
        [PreserveSig] int GetProperty(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid clsid);
        [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out long fileTime);
        [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int value);
        [PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr value);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out object item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems copy);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItem2 item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHCreateItemFromParsingName")]
    private static extern int SHCreateItemFromParsingNameImage(string path, IntPtr bindContext, ref Guid riid, out IShellItemImageFactory item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListEx(IntPtr idList, System.Text.StringBuilder path, int length, int options);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    private static extern int PSGetPropertyKeyFromName(string name, out PROPERTYKEY key);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);

    #endregion
}
