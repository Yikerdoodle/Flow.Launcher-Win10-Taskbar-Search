using System;
using Microsoft.Win32;

namespace Flow.Launcher.Helper;

/// <summary>
/// What the Windows 10 Start menu and taskbar search panel use for their translucent background: the system's own
/// acrylic (a blur of what is behind the panel, a saturation boost, a tint and noise), per panel and per light or dark
/// mode, and a solid color when "Transparency effects" is off in Settings > Personalization > Colors.
/// <para>
/// Windows draws the panels' acrylic itself and gives other windows only an approximation of it, so Flow renders it
/// in software, as measured from the native panels (see <see cref="Win10AcrylicRenderer"/> and
/// <see cref="Win10AcrylicController"/>). This class has the system settings that decide how.
/// </para>
/// </summary>
internal static class SystemAcrylic
{
    /// <summary>The native panel a Flow layout copies.</summary>
    internal enum Panel
    {
        /// <summary>The Start menu (Flow's Start layout).</summary>
        Start,

        /// <summary>The taskbar search panel (Flow's search layout).</summary>
        Search
    }

    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// The rendering needs to capture what is behind Flow without Flow in it, which is
    /// <c>SetWindowDisplayAffinity</c> with <c>WDA_EXCLUDEFROMCAPTURE</c>: Windows 10 2004 (build 19041).
    /// </summary>
    internal static bool IsSupported => Environment.OSVersion.Version.Build >= 19041;

    /// <summary>
    /// "Transparency effects" in Settings > Personalization > Colors.
    /// </summary>
    internal static bool TransparencyEnabled => ReadFlag("EnableTransparency", true);

    /// <summary>
    /// "Choose your default Windows mode" (the taskbar, Start and search follow it; apps follow their own setting).
    /// </summary>
    internal static bool LightMode => ReadFlag("SystemUsesLightTheme", false);

    private static bool ReadFlag(string name, bool defaultValue)
    {
        try
        {
            return Registry.GetValue(PersonalizeKey, name, null) is int value ? value != 0 : defaultValue;
        }
        catch (Exception)
        {
            return defaultValue;
        }
    }
}
