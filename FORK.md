# Flow Launcher: Windows 10 taskbar search fork

This is a personal fork of [Flow Launcher](https://github.com/Flow-Launcher/Flow.Launcher),
based on the **v2.1.4** release. It is not affiliated with or endorsed by the Flow Launcher team.

It makes Flow Launcher work as a drop-in replacement for the Windows 10 taskbar search panel,
using [Everything](https://www.voidtools.com/) for instant file search.

All changes are on the `win10-taskbar-search` branch, one commit per feature, so they are easy
to review or rebase onto a newer Flow Launcher release.

## What's different from upstream

| Change | Where |
|---|---|
| New **Left Bottom** window position. The window sits flush in the bottom-left corner, the search box is below the results, and the results list grows upward, like the Windows 10 search panel. With an auto-hide taskbar, the window stops right above where the taskbar pops up, so the taskbar never covers the search box. | `MainWindow.xaml(.cs)`, `Settings.cs`, `Win32Helper.cs` |
| **Fix:** when more than one plugin shows home page results, their result batches no longer clear each other (upstream only showed the slowest plugin's results). | `MainViewModel.cs` |
| **Top apps** on the home page. The Program plugin shows the 6 most-launched apps, using Windows' own UserAssist launch counts. | `Plugin.Program/TopApps.cs` |
| **Recent files** on the home page. The Explorer plugin shows the 8 most recent files from the Windows Recent folder. | `Plugin.Explorer/Search/RecentFiles.cs` |
| **Pin to taskbar / Unpin from taskbar** at the top of the right-click menu of apps (desktop and Store) and pinnable files, using Windows' own wording and the Windows 10 pin/unpin symbols. Unpinning is silent, through the documented `IStartMenuPinnedList` API, unless the item also has a Start menu tile (that API would remove the tile too). Windows only allows pinning from Explorer's own menu, so Flow selects the item in an Explorer window and chooses Explorer's menu command, with the window and menu made fully transparent the moment Explorer creates them. | `Plugin/SharedCommands/TaskbarPin.cs`, Program and Explorer plugins |
| **Windows 10 Taskbar** theme: the Windows 10 Start menu's dark colors, measured to the pixel (background #2F2F2F, 10% white hover/selection, 60% white secondary text), no outline or divider lines, square corners, a taskbar-height search box with a 2 px border in the system accent color (updates when the accent color changes), and a thin Start-style scroll bar that stays hidden until the mouse is over the list or it scrolls. Follows system light/dark; light mode uses the same Windows formulas, but its background isn't measured yet. | `Themes/Win10Taskbar.xaml`, `Resources/Dark.xaml`, `Resources/Light.xaml`, `Helper/ScrollBarAutoHide.cs` |

## Settings used with this fork

These are ordinary Flow Launcher settings; nothing here is hard-coded:

- **General → Search window position:** screen *Primary*, position *Left Bottom*
- **General → Query search window:** *Empty last query* (opens blank each time)
- **Hotkey:** `LWin` (tapping the Windows key on its own opens Flow)
- **Appearance:** theme *Windows 10 Taskbar*, drop shadow off, clock/date off
- **Plugins → Explorer:** index search engine and path enumeration engine set to *Everything*
- **Plugins → Windows Settings:** action keyword `*` (Settings pages show up without a prefix)
- **Plugin priorities:** Program 4, Windows Settings 3, System Commands 2, Web Searches -5
- **Plugins → Program:** hide uninstallers
- **Plugins → Web Searches:** search suggestions on
- **Auto update plugins:** off (an update would swap the modified Program and Explorer plugins for the official ones)

## Building

Requires the .NET 9 SDK.

```powershell
dotnet build Flow.Launcher.sln -c Release
dotnet publish Flow.Launcher/Flow.Launcher.csproj -c Release /p:PublishProfile=Flow.Launcher/Properties/PublishProfiles/Net9.0-SelfContained.pubxml
```

Before building a copy to install, set the version in `SolutionAssemblyInfo.cs` and the `"Version"` in each
`Plugins/*/plugin.json` to the release this fork is based on, as upstream's CI does. With the source default of
1.0.0, Flow offers to "update" the built-in plugins to the official ones.

The self-contained app ends up in `Output/Release`. Copy that folder anywhere (for example
`%LOCALAPPDATA%\Programs\FlowLauncher`) and run `Flow.Launcher.exe`. Leave **automatic updates
off**: an official update would replace this build with upstream Flow Launcher.

## License

MIT, same as upstream. See [LICENSE](LICENSE).
