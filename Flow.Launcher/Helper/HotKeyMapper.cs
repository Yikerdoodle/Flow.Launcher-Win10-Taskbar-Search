using System;
using ChefKeys;
using CommunityToolkit.Mvvm.DependencyInjection;
using Flow.Launcher.Infrastructure.Hotkey;
using Flow.Launcher.Infrastructure.DialogJump;
using Flow.Launcher.Infrastructure.UserSettings;
using Flow.Launcher.ViewModel;
using NHotkey;
using NHotkey.Wpf;

namespace Flow.Launcher.Helper;

internal static class HotKeyMapper
{
    private static readonly string ClassName = nameof(HotKeyMapper);

    private static Settings _settings;
    private static MainViewModel _mainViewModel;

    internal static void Initialize()
    {
        _mainViewModel = Ioc.Default.GetRequiredService<MainViewModel>();
        _settings = Ioc.Default.GetService<Settings>();

        SetHotkey(_settings.Hotkey, OnToggleHotkey);
        if (_settings.EnableDialogJump)
        {
            SetHotkey(_settings.DialogJumpHotkey, DialogJump.OnToggleHotkey);
        }
        LoadCustomPluginHotkey();

        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    // ChefKeys remembers which keys are down from the key presses it sees. Locking the PC switches to the lock screen
    // in the middle of a key press (the L of Win+L), so it sees that key go down but never come back up. It then
    // takes every Win press as part of a key combination and lets the Start menu open instead of Flow, until some
    // other key is released. Forgetting the remembered keys when the session is locked or unlocked fixes that.
    private static readonly string[] ChefKeysKeyState =
        { "isWinKeyDown", "nonRegisteredKeyDown", "cancelSingleKeyAction", "registeredKeyDown", "lastRegisteredDownKey" };

    private static void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is not (Microsoft.Win32.SessionSwitchReason.SessionLock
            or Microsoft.Win32.SessionSwitchReason.SessionUnlock
            or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
            or Microsoft.Win32.SessionSwitchReason.RemoteConnect)) return;

        // On the UI thread, where ChefKeys' keyboard hook runs, so this can't interleave with a key press
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                foreach (var name in ChefKeysKeyState)
                {
                    var field = typeof(ChefKeysManager).GetField(name,
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (field == null)
                    {
                        App.API.LogError(ClassName, $"ChefKeys has no field {name}; its key state was not reset");
                        continue;
                    }
                    field.SetValue(null, field.FieldType == typeof(bool) ? false : 0);
                }
                App.API.LogInfo(ClassName, $"Reset ChefKeys' key state after {e.Reason}");
            }
            catch (Exception ex)
            {
                App.API.LogException(ClassName, $"Failed to reset ChefKeys' key state after {e.Reason}", ex);
            }
        });
    }

    internal static void OnToggleHotkey(object sender, HotkeyEventArgs args)
    {
        if (!_mainViewModel.ShouldIgnoreHotkeys())
            _mainViewModel.ToggleFlowLauncher();
    }

    internal static void OnToggleHotkeyWithChefKeys()
    {
        if (!_mainViewModel.ShouldIgnoreHotkeys())
            _mainViewModel.ToggleFlowLauncher();
    }

    private static void SetHotkey(string hotkeyStr, EventHandler<HotkeyEventArgs> action)
    {
        var hotkey = new HotkeyModel(hotkeyStr);
        SetHotkey(hotkey, action);
    }

    private static void SetWithChefKeys(string hotkeyStr)
    {
        try
        {
            ChefKeysManager.RegisterHotkey(hotkeyStr, hotkeyStr, OnToggleHotkeyWithChefKeys);
            ChefKeysManager.Start();
        }
        catch (Exception e)
        {
            App.API.LogError(ClassName,
                string.Format("|HotkeyMapper.SetWithChefKeys|Error registering hotkey: {0} \nStackTrace:{1}",
                              e.Message,
                              e.StackTrace));
            string errorMsg = Localize.registerHotkeyFailed(hotkeyStr);
            string errorMsgTitle = Localize.MessageBoxTitle();
            App.API.ShowMsgBox(errorMsg, errorMsgTitle);
        }
    }

    internal static void SetHotkey(HotkeyModel hotkey, EventHandler<HotkeyEventArgs> action)
    {
        string hotkeyStr = hotkey.ToString();
        try
        {
            if (hotkeyStr == "LWin" || hotkeyStr == "RWin")
            {
                SetWithChefKeys(hotkeyStr);
                return;
            }

            HotkeyManager.Current.AddOrReplace(hotkeyStr, hotkey.CharKey, hotkey.ModifierKeys, action);
        }
        catch (Exception e)
        {
            App.API.LogError(ClassName,
                string.Format("|HotkeyMapper.SetHotkey|Error registering hotkey {2}: {0} \nStackTrace:{1}",
                              e.Message,
                              e.StackTrace,
                              hotkeyStr));
            string errorMsg = Localize.registerHotkeyFailed(hotkeyStr);
            string errorMsgTitle = Localize.MessageBoxTitle();
            App.API.ShowMsgBox(errorMsg, errorMsgTitle);
        }
    }

    internal static void RemoveHotkey(string hotkeyStr)
    {
        try
        {
            if (hotkeyStr == "LWin" || hotkeyStr == "RWin")
            {
                RemoveWithChefKeys(hotkeyStr);
                return;
            }

            if (!string.IsNullOrEmpty(hotkeyStr))
                HotkeyManager.Current.Remove(hotkeyStr);
        }
        catch (Exception e)
        {
            App.API.LogError(ClassName,
                string.Format("|HotkeyMapper.RemoveHotkey|Error removing hotkey: {0} \nStackTrace:{1}",
                              e.Message,
                              e.StackTrace));
            string errorMsg = Localize.unregisterHotkeyFailed(hotkeyStr);
            string errorMsgTitle = Localize.MessageBoxTitle();
            App.API.ShowMsgBox(errorMsg, errorMsgTitle);
        }
    }

    private static void RemoveWithChefKeys(string hotkeyStr)
    {
        ChefKeysManager.UnregisterHotkey(hotkeyStr);
        ChefKeysManager.Stop();
    }

    internal static void LoadCustomPluginHotkey()
    {
        if (_settings.CustomPluginHotkeys == null)
            return;

        foreach (CustomPluginHotkey hotkey in _settings.CustomPluginHotkeys)
        {
            SetCustomQueryHotkey(hotkey);
        }
    }

    internal static void SetCustomQueryHotkey(CustomPluginHotkey hotkey)
    {
        SetHotkey(hotkey.Hotkey, (s, e) =>
        {
            if (_mainViewModel.ShouldIgnoreHotkeys())
                return;

            App.API.ShowMainWindow();
            // Make sure to go back to the query results page first since it can cause issues if current page is context menu
            App.API.BackToQueryResults();
            App.API.ChangeQuery(hotkey.ActionKeyword, true);
        });
    }

    internal static bool CheckAvailability(HotkeyModel currentHotkey)
    {
        try
        {
            HotkeyManager.Current.AddOrReplace("HotkeyAvailabilityTest", currentHotkey.CharKey, currentHotkey.ModifierKeys, (sender, e) => { });

            return true;
        }
        catch
        {
        }
        finally
        {
            HotkeyManager.Current.Remove("HotkeyAvailabilityTest");
        }

        return false;
    }
}
