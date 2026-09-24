using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Flow.Launcher.Infrastructure.Storage;

namespace Flow.Launcher.StartMenu;

/// <summary>A row of the All apps list.</summary>
public abstract class AllAppsEntry : ObservableObject
{
}

/// <summary>A group header: "Recently added", a letter, "#", "&amp;" or the globe for other scripts.</summary>
public sealed class AllAppsHeader : AllAppsEntry
{
    public string Text { get; init; }

    /// <summary>The letter grid key this header jumps from (see <see cref="AllAppsList.JumpKeys"/>).</summary>
    public string Key { get; init; }
}

/// <summary>The "Expand" / "Collapse" row under Recently added.</summary>
public sealed class AllAppsExpander : AllAppsEntry
{
    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value)) OnPropertyChanged(nameof(Text));
        }
    }

    public string Text => IsExpanded ? "Collapse" : "Expand";
}

/// <summary>An app, or a Start menu folder that expands to its apps.</summary>
public sealed class AllAppsItem : AllAppsEntry
{
    private ImageSource _icon;
    private bool _isExpanded;

    public string Name { get; init; }

    /// <summary>The app, or null for a folder.</summary>
    public AppsFolderItem App { get; init; }

    /// <summary>A folder's apps, sorted by name.</summary>
    public List<AllAppsItem> Children { get; init; }

    public bool IsFolder => App == null;

    /// <summary>Whether the app is inside an expanded folder (drawn the same, but not a top level entry).</summary>
    public bool IsInFolder { get; init; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public ImageSource Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }
}

/// <summary>
/// The Windows 10 Start menu's All apps list: apps and Start menu folders from the Windows Apps folder, grouped by
/// first letter, with the most recently installed apps on top.
/// </summary>
public sealed class AllAppsList
{
    // Start shows the newest three, and the rest of the apps installed in the last two weeks after "Expand"
    private const int RecentCollapsedCount = 3;
    private static readonly TimeSpan RecentPeriod = TimeSpan.FromDays(14);

    public const string RecentKey = "recent";
    public const string OtherKey = "other";

    /// <summary>The keys of the letter grid, in its order: Recently added, &amp;, #, A to Z, other scripts.</summary>
    public static readonly IReadOnlyList<string> JumpKeys =
        new[] { RecentKey, "&", "#" }.Concat(Enumerable.Range('A', 26).Select(c => ((char)c).ToString())).Append(OtherKey).ToList();

    private readonly FlowLauncherJsonStorage<StartMenuApps> _storage = new();
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private List<AllAppsItem> _topLevel = new();
    private List<AllAppsItem> _recent = new();
    private readonly AllAppsExpander _expander = new();

    public ObservableCollection<AllAppsEntry> Entries { get; } = new();

    /// <summary>The letter grid keys that have apps.</summary>
    public HashSet<string> UsedKeys { get; } = new();

    /// <summary>Icon size in pixels.</summary>
    public int IconPixels { get; set; } = 36;

    /// <summary>
    /// Reads the Apps folder again (on a background thread) and rebuilds the list. Call on the UI thread.
    /// </summary>
    public async Task RefreshAsync()
    {
        var items = await Task.Run(() =>
        {
            var apps = AppsFolder.GetItems();
            var firstSeen = UpdateFirstSeen(apps);
            return (apps, firstSeen);
        });

        Build(items.apps, items.firstSeen);
        LoadIconsAsync();
    }

    // When Start first saw each app: Windows keeps this to itself, so Flow keeps its own record. Apps already
    // installed when the record starts get the install time their files show; apps that show up later get the time
    // they showed up, so an update that recreates a shortcut doesn't make an app new again.
    private Dictionary<string, DateTime> UpdateFirstSeen(List<AppsFolderItem> apps)
    {
        var existed = _storage.Exists();
        var data = _storage.Load();
        data.FirstSeen ??= new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var firstSeen = new Dictionary<string, DateTime>(data.FirstSeen, StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var app in apps)
        {
            // Web links never count as new apps
            if (app.AppId.Contains("://") || firstSeen.ContainsKey(app.AppId)) continue;

            firstSeen[app.AppId] = existed ? now : app.InstalledTime;
            changed = true;
        }

        if (changed || !existed)
        {
            data.FirstSeen = firstSeen;
            _storage.Save();
        }

        return firstSeen;
    }

    private void Build(List<AppsFolderItem> apps, Dictionary<string, DateTime> firstSeen)
    {
        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);

        AllAppsItem ItemFor(AppsFolderItem app, bool inFolder) => new()
        {
            Name = app.Name,
            App = app,
            IsInFolder = inFolder,
            Icon = _iconCache.GetValueOrDefault(app.AppId)
        };

        var topLevel = apps.Where(a => a.Folder == null).Select(a => ItemFor(a, false)).ToList();
        var folders = apps.Where(a => a.Folder != null)
            .GroupBy(a => a.Folder, comparer)
            .Select(g => new AllAppsItem
            {
                Name = g.Key,
                Children = g.OrderBy(a => a.Name, comparer).Select(a => ItemFor(a, true)).ToList()
            });
        topLevel.AddRange(folders);

        // Folders go before an app of the same name, as in Start
        _topLevel = topLevel.OrderBy(i => i.Name, comparer).ThenBy(i => i.IsFolder ? 0 : 1).ToList();

        var cutoff = DateTime.UtcNow - RecentPeriod;
        _recent = apps
            .Select(a => (app: a, seen: firstSeen.GetValueOrDefault(a.AppId, DateTime.MinValue)))
            .Where(x => x.seen > cutoff)
            .OrderByDescending(x => x.seen)
            .Select(x => ItemFor(x.app, false))
            .ToList();

        _expander.IsExpanded = false;
        Rebuild();
    }

    /// <summary>Lays the entries out again after a folder or the Recently added group is expanded or collapsed.</summary>
    public void Rebuild()
    {
        var entries = new List<AllAppsEntry>();
        UsedKeys.Clear();

        if (_recent.Count > 0)
        {
            entries.Add(new AllAppsHeader { Text = "Recently added", Key = RecentKey });
            UsedKeys.Add(RecentKey);
            entries.AddRange(_expander.IsExpanded ? _recent : _recent.Take(RecentCollapsedCount));
            if (_recent.Count > RecentCollapsedCount) entries.Add(_expander);
        }

        string currentKey = null;
        foreach (var item in _topLevel)
        {
            var key = KeyOf(item.Name);
            if (key != currentKey)
            {
                currentKey = key;
                UsedKeys.Add(key);
                entries.Add(new AllAppsHeader { Text = HeaderText(key), Key = key });
            }

            entries.Add(item);
            if (item.IsFolder && item.IsExpanded) entries.AddRange(item.Children);
        }

        Entries.Clear();
        foreach (var entry in entries) Entries.Add(entry);
    }

    public void ToggleRecent()
    {
        _expander.IsExpanded = !_expander.IsExpanded;
        Rebuild();
    }

    public void ToggleFolder(AllAppsItem folder)
    {
        folder.IsExpanded = !folder.IsExpanded;
        Rebuild();
    }

    /// <summary>The index of the header for a letter grid key, or -1.</summary>
    public int IndexOfKey(string key)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i] is AllAppsHeader header && header.Key == key) return i;
        }
        return -1;
    }

    /// <summary>
    /// The letter grid key of a name: its first letter without accents for Latin letters, "#" for digits, "&amp;"
    /// for symbols, and <see cref="OtherKey"/> for other scripts.
    /// </summary>
    public static string KeyOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return "&";

        var first = name.Normalize(System.Text.NormalizationForm.FormD)[0];
        if (char.IsDigit(first)) return "#";
        var upper = char.ToUpperInvariant(first);
        if (upper is >= 'A' and <= 'Z') return upper.ToString();
        return char.IsLetter(first) ? OtherKey : "&";
    }

    private static string HeaderText(string key) => key == OtherKey ? "" : key;

    private void LoadIconsAsync()
    {
        var missing = Entries.OfType<AllAppsItem>()
            .Concat(_topLevel.Where(i => i.IsFolder).SelectMany(i => i.Children))
            .Concat(_topLevel)
            .Concat(_recent)
            .Where(i => i.App != null && i.Icon == null)
            .ToList();
        if (missing.Count == 0) return;

        var dispatcher = Application.Current.Dispatcher;
        Task.Run(() =>
        {
            foreach (var group in missing.GroupBy(i => i.App.AppId, StringComparer.OrdinalIgnoreCase))
            {
                var icon = LoadIcon(group.First().App.ParsingPath);
                if (icon == null) continue;

                dispatcher.BeginInvoke(() =>
                {
                    _iconCache[group.Key] = icon;
                    foreach (var item in group) item.Icon = icon;
                });
            }
        });
    }

    private ImageSource LoadIcon(string parsingPath)
    {
        var bitmap = AppsFolder.GetIconBitmap(parsingPath, IconPixels);
        if (bitmap == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            DeleteObject(bitmap);
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}

/// <summary>A cell of the letter grid.</summary>
public sealed class JumpCell
{
    public string Key { get; init; }

    public string Text => Key switch
    {
        AllAppsList.RecentKey => "",
        AllAppsList.OtherKey => "",
        _ => Key
    };

    /// <summary>Whether the cell is a symbol (the clock and the globe) rather than a letter.</summary>
    public bool IsGlyph => Key is AllAppsList.RecentKey or AllAppsList.OtherKey;

    /// <summary>Whether the list has apps under it; other cells are grayed out and do nothing.</summary>
    public bool IsEnabled { get; init; }
}

/// <summary>Stored data of the Start menu: when each app was first seen, for Recently added.</summary>
public class StartMenuApps
{
    public Dictionary<string, DateTime> FirstSeen { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
