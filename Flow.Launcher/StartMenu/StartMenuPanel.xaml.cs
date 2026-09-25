using System;
using System.Collections.Generic;
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
        FlyoutLayer.Visibility = Visibility.Collapsed;
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
        Resources["StartAccountSmallFontSize"] = 16 * scale;
        Resources["StartAccountNameFontSize"] = 18 * scale;
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
        // An open menu covers the rail; the rail stays as it is until the menu closes
        if (_railExpanded && FlyoutLayer.Visibility != Visibility.Visible) SetRailExpanded(false);
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
        if (AccountPictureBrush(AccountPicture.Width) is { } picture) AccountPicture.Fill = picture;
    }

    // The account picture for a circle of the given size. Windows keeps it at several sizes; this takes the smallest
    // that is big enough
    private Brush AccountPictureBrush(double size)
    {
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return null;

            var pixels = size * VisualTreeHelper.GetDpi(this).DpiScaleX;
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid);
            var path = new[] { 32, 40, 48, 64, 96, 192, 208, 240, 424, 448, 1080 }
                .Where(s => s >= pixels)
                .Select(s => key?.GetValue("Image" + s) as string)
                .FirstOrDefault(p => p != null && System.IO.File.Exists(p));
            if (path == null) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception e)
        {
            App.API.LogException(nameof(StartMenuPanel), "Failed to load the account picture", e);
            return null;
        }
    }

    private static string DisplayName()
    {
        var name = new StringBuilder(256);
        var length = name.Capacity;
        return GetUserNameEx(NameDisplay, name, ref length) && name.Length > 0 ? name.ToString() : Environment.UserName;
    }

    // Bottom of the rail buttons: the account button's top is 144 above the panel's bottom, Power's 48; Start opens
    // its menus 4.67 above the button
    private const double MenuGap = 4.67;

    // The account box, read fresh each time it opens: with a Microsoft account, the Microsoft logo, the account's
    // name and email and "My Microsoft account"; with a local account, just the name, and the logo's place blank
    private async void OnAccountClick(object sender, MouseButtonEventArgs e)
    {
        if (AccountBigPicture.Fill is not ImageBrush && AccountPictureBrush(AccountBigPicture.Width) is { } picture)
        {
            AccountBigPicture.Fill = picture;
        }

        var account = await AccountInfo.GetAsync();
        var microsoft = account.IsMicrosoftAccount ? Visibility.Visible : Visibility.Hidden;
        MicrosoftLogo.Visibility = microsoft;
        MicrosoftWordmark.Visibility = microsoft;
        MicrosoftAccountLink.Visibility = microsoft;
        AccountFullName.Text = account.Name;
        AccountEmail.Text = account.Email ?? string.Empty;

        Flyout.Visibility = Visibility.Collapsed;
        AccountFlyout.Visibility = Visibility.Visible;
        FlyoutLayer.Visibility = Visibility.Visible;
    }

    private void OnSignOutClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseMenu();
        ExitWindowsEx(EWX_LOGOFF, 0);
    }

    private void OnMicrosoftAccountClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseMenu();
        Launch("https://account.microsoft.com/");
    }

    // The account box's "..." menu, under the dots and lined up with the box's right edge
    private void OnAccountMoreClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var dots = (FrameworkElement)sender;
        var box = AccountFlyout.TransformToAncestor(this).Transform(new Point(0, 0));
        var below = dots.TransformToAncestor(this).Transform(new Point(0, dots.ActualHeight));
        AccountFlyout.Visibility = Visibility.Collapsed;
        ShowMenu(new Thickness(box.X + AccountFlyout.Width - Flyout.Width, below.Y, 0, 0), VerticalAlignment.Top,
            new StartFlyoutItem(null, "Change account settings", () => Launch("ms-settings:yourinfo")),
            new StartFlyoutItem(null, "Lock", () =>
            {
                HideFlow();
                LockWorkStation();
            }));
    }

    private void OnSettingsClick(object sender, MouseButtonEventArgs e) =>
        Launch(@"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel");

    // The same options as Start's Power menu, which follows "Show in Power menu" in Control Panel's power button
    // settings: Lock and Sleep unless turned off, Hibernate only when turned on
    private void OnPowerClick(object sender, MouseButtonEventArgs e)
    {
        var items = new List<StartFlyoutItem>();
        if (PowerMenuOption("ShowLockOption", true))
        {
            items.Add(new StartFlyoutItem("\uE72E", "Lock", () =>
            {
                HideFlow();
                LockWorkStation();
            }));
        }
        if (PowerMenuOption("ShowSleepOption", true))
        {
            items.Add(new StartFlyoutItem("\uE708", "Sleep", () =>
            {
                HideFlow();
                SetSuspendState(false, false, false);
            }));
        }
        if (PowerMenuOption("ShowHibernateOption", false))
        {
            items.Add(new StartFlyoutItem("\uE708", "Hibernate", () =>
            {
                HideFlow();
                SetSuspendState(true, false, false);
            }));
        }
        items.Add(new StartFlyoutItem("\uE7E8", "Shut down",
            () => Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true })));
        items.Add(new StartFlyoutItem("\uE777", "Restart",
            () => Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true })));

        ShowMenu(RailWidth + MenuGap, items.ToArray());
    }

    private static bool PowerMenuOption(string name, bool defaultValue)
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings", name, null);
            return value is int number ? number != 0 : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    // A menu above a rail button, from the panel's left edge
    private void ShowMenu(double bottom, params StartFlyoutItem[] items) =>
        ShowMenu(new Thickness(0, 0, 0, bottom), VerticalAlignment.Bottom, items);

    private void ShowMenu(Thickness margin, VerticalAlignment alignment, params StartFlyoutItem[] items)
    {
        FlyoutItems.ItemsSource = items;
        Flyout.Margin = margin;
        Flyout.VerticalAlignment = alignment;
        Flyout.Visibility = Visibility.Visible;
        FlyoutLayer.Visibility = Visibility.Visible;
    }

    private void CloseMenu()
    {
        if (FlyoutLayer.Visibility != Visibility.Visible) return;

        FlyoutLayer.Visibility = Visibility.Collapsed;
        AccountFlyout.Visibility = Visibility.Collapsed;
        FlyoutItems.ItemsSource = null;

        // The rail stays expanded under an open menu; now collapse it unless the mouse is on it
        var mouse = Mouse.GetPosition(Rail);
        var overRail = mouse.X >= 0 && mouse.Y >= 0 && mouse.X < Rail.ActualWidth && mouse.Y < Rail.ActualHeight;
        if (_railExpanded && !overRail) SetRailExpanded(false);
    }

    private void OnFlyoutLayerClick(object sender, MouseButtonEventArgs e) => CloseMenu();

    // Clicks between the menu's rows don't close it
    private void OnFlyoutClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnFlyoutItemClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not StartFlyoutItem item) return;

        CloseMenu();
        try
        {
            item.Action();
        }
        catch (Exception ex)
        {
            App.API.LogException(nameof(StartMenuPanel), $"Failed to run {item.Text}", ex);
        }
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
