using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Flow.Launcher.StartMenu;

/// <summary>
/// A copy of the Windows 10 Start menu (with "Show app list in Start menu" off): the rail with the Pinned tiles and
/// All apps views, the account, Settings and Power, and the All apps list with its letter grid.
/// </summary>
public partial class StartMenuPanel : UserControl
{
    private const double RailWidth = 48;
    private const double ExpandedRailWidth = 256;
    private static readonly TimeSpan RailHoverDelay = TimeSpan.FromMilliseconds(400);
    private static readonly Duration RailAnimation = new(TimeSpan.FromMilliseconds(150));

    private readonly AllAppsList _allApps = new();
    private readonly DispatcherTimer _railHoverTimer;
    private bool _railExpanded;
    private bool _refreshing;

    private enum View
    {
        PinnedTiles,
        AllApps
    }

    private View _view = View.AllApps;

    public StartMenuPanel()
    {
        InitializeComponent();

        AllAppsItems.ItemsSource = _allApps.Entries;
        _railHoverTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = RailHoverDelay };
        _railHoverTimer.Tick += (_, _) =>
        {
            _railHoverTimer.Stop();
            SetRailExpanded(true);
        };

        ApplyTextScale();
        ShowView(_view);
        Loaded += (_, _) => LoadAccount();
    }

    /// <summary>
    /// Called each time Flow opens in Start mode: back to the top of the list with the rail collapsed, and the list
    /// read again so newly installed apps show up.
    /// </summary>
    public async void Reset()
    {
        _railHoverTimer.Stop();
        SetRailExpanded(false, animate: false);
        JumpGrid.Visibility = Visibility.Collapsed;
        AllAppsScroller.Visibility = Visibility.Visible;
        AllAppsScroller.ScrollToTop();
        ApplyTextScale();

        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _allApps.IconPixels = (int)Math.Round(24 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            await _allApps.RefreshAsync();
        }
        catch (Exception e)
        {
            App.API.LogException(nameof(StartMenuPanel), "Failed to read the Apps folder", e);
        }
        finally
        {
            _refreshing = false;
        }
    }

    // Clicks here are for the Start menu, not for dragging the window (Flow drags the window from anywhere)
    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    #region Text size

    // Start scales its text with the Windows text size setting (Settings > Ease of Access > Display), but by less
    // than the setting says: at 175% its 15 DIP text is 20.5 DIP, a 37% increase, and the other sizes scale alike
    private void ApplyTextScale()
    {
        var percent = 100;
        try
        {
            percent = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "TextScaleFactor", 100)
                is int value ? value : 100;
        }
        catch
        {
            // Keep 100%
        }

        var scale = 1 + (Math.Clamp(percent, 100, 225) / 100.0 - 1) * 0.489;
        Resources["StartFontSize"] = 15 * scale;
        Resources["StartRailFontSize"] = 17 * scale;
        Resources["StartJumpFontSize"] = 20 * scale;
        Resources["StartChevronSize"] = 13 * scale;
    }

    #endregion

    #region Views and rail

    private void ShowView(View view)
    {
        _view = view;
        var pinned = view == View.PinnedTiles;
        PinnedView.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        AllAppsScroller.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        JumpGrid.Visibility = Visibility.Collapsed;

        MarkRailButton(PinnedBar, PinnedGlyph, PinnedLabel, pinned);
        MarkRailButton(AllAppsBar, AllAppsGlyph, AllAppsLabel, !pinned);
    }

    private void MarkRailButton(UIElement bar, TextBlock glyph, TextBlock label, bool selected)
    {
        bar.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        if (selected)
        {
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "StartSelectedBrush");
            label.SetResourceReference(TextBlock.ForegroundProperty, "StartSelectedBrush");
        }
        else
        {
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Win10StartText");
            label.SetResourceReference(TextBlock.ForegroundProperty, "Win10StartText");
        }
    }

    private void SetRailExpanded(bool expanded, bool animate = true)
    {
        _railExpanded = expanded;
        if (expanded)
        {
            Rail.SetResourceReference(Border.BackgroundProperty, "Win10StartPaneBackground");
        }
        else
        {
            Rail.Background = Brushes.Transparent;
        }

        var target = expanded ? ExpandedRailWidth : RailWidth;
        if (animate)
        {
            Rail.BeginAnimation(WidthProperty, new DoubleAnimation(target, RailAnimation)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            Rail.BeginAnimation(WidthProperty, null);
            Rail.Width = target;
        }
    }

    private void OnRailMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_railExpanded) _railHoverTimer.Start();
    }

    private void OnRailMouseLeave(object sender, MouseEventArgs e)
    {
        _railHoverTimer.Stop();
        if (_railExpanded) SetRailExpanded(false);
    }

    private void OnMenuClick(object sender, MouseButtonEventArgs e)
    {
        _railHoverTimer.Stop();
        SetRailExpanded(!_railExpanded);
    }

    private void OnPinnedClick(object sender, MouseButtonEventArgs e)
    {
        ShowView(View.PinnedTiles);
        CollapseRailAfterClick();
    }

    private void OnAllAppsClick(object sender, MouseButtonEventArgs e)
    {
        ShowView(View.AllApps);
        CollapseRailAfterClick();
    }

    private void CollapseRailAfterClick()
    {
        _railHoverTimer.Stop();
        if (_railExpanded) SetRailExpanded(false);
    }

    #endregion

    #region All apps

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AllAppsItem item) return;

        if (item.IsFolder)
        {
            _allApps.ToggleFolder(item);
        }
        else
        {
            Launch(item.App.ParsingPath);
        }
    }

    private void OnItemRightClick(object sender, MouseButtonEventArgs e)
    {
        // The app context menu comes with the next step
    }

    private void OnExpanderClick(object sender, MouseButtonEventArgs e) => _allApps.ToggleRecent();

    private void OnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        JumpItems.ItemsSource = AllAppsList.JumpKeys
            .Select(key => new JumpCell { Key = key, IsEnabled = _allApps.UsedKeys.Contains(key) })
            .ToList();
        AllAppsScroller.Visibility = Visibility.Collapsed;
        JumpGrid.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnJumpCellClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not JumpCell { IsEnabled: true } cell) return;

        CloseJumpGrid();
        var index = _allApps.IndexOfKey(cell.Key);
        if (index < 0) return;

        // Scroll the header to the top once the list is laid out again
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (AllAppsItems.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
            {
                var top = container.TransformToAncestor(AllAppsItems).Transform(new Point(0, 0)).Y;
                AllAppsScroller.ScrollToVerticalOffset(top);
            }
        });
    }

    private void OnJumpGridBackgroundClick(object sender, MouseButtonEventArgs e) => CloseJumpGrid();

    private void CloseJumpGrid()
    {
        JumpGrid.Visibility = Visibility.Collapsed;
        AllAppsScroller.Visibility = Visibility.Visible;
    }

    #endregion

    #region Account, Settings and Power

    private void LoadAccount()
    {
        AccountName.Text = DisplayName();

        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return;

            // Windows keeps the account picture at several sizes; take the smallest that is big enough
            var pixels = 20 * VisualTreeHelper.GetDpi(this).DpiScaleX;
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid);
            var path = new[] { 32, 40, 48, 64, 96, 192, 208, 240, 424, 448, 1080 }
                .Where(size => size >= pixels)
                .Select(size => key?.GetValue("Image" + size) as string)
                .FirstOrDefault(p => p != null && System.IO.File.Exists(p));
            if (path == null) return;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            AccountPicture.Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception e)
        {
            App.API.LogException(nameof(StartMenuPanel), "Failed to load the account picture", e);
        }
    }

    private static string DisplayName()
    {
        var name = new StringBuilder(256);
        var length = name.Capacity;
        return GetUserNameEx(NameDisplay, name, ref length) && name.Length > 0 ? name.ToString() : Environment.UserName;
    }

    private void OnAccountClick(object sender, MouseButtonEventArgs e)
    {
        ShowMenu((FrameworkElement)sender,
            ("Change account settings", () => Launch("ms-settings:yourinfo")),
            ("Lock", () =>
            {
                HideFlow();
                LockWorkStation();
            }),
            ("Sign out", () => ExitWindowsEx(EWX_LOGOFF, 0)));
    }

    private void OnSettingsClick(object sender, MouseButtonEventArgs e) =>
        Launch(@"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel");

    private void OnPowerClick(object sender, MouseButtonEventArgs e)
    {
        ShowMenu((FrameworkElement)sender,
            ("Sleep", () =>
            {
                HideFlow();
                SetSuspendState(false, false, false);
            }),
            ("Shut down", () => Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true })),
            ("Restart", () => Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true })));
    }

    private void ShowMenu(FrameworkElement target, params (string Text, Action Action)[] items)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top
        };
        foreach (var (text, action) in items)
        {
            var menuItem = new MenuItem { Header = text };
            menuItem.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    App.API.LogException(nameof(StartMenuPanel), $"Failed to run {text}", e);
                }
            };
            menu.Items.Add(menuItem);
        }
        menu.IsOpen = true;
    }

    #endregion

    // Starts an app (a shell:AppsFolder path) or a URI through Explorer, like Start, then closes Flow
    private static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            App.API.LogException(nameof(StartMenuPanel), $"Failed to open {target}", e);
            return;
        }

        HideFlow();
    }

    private static void HideFlow() => App.API.HideMainWindow();

    private const int NameDisplay = 3;
    private const uint EWX_LOGOFF = 0;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserNameEx(int format, StringBuilder name, ref int length);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
