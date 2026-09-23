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
| New **Left Bottom** window position. The window sits flush in the bottom-left corner, the search box is below the results, and the results list grows upward, like the Windows 10 search panel. | `MainWindow.xaml(.cs)`, `Settings.cs` |
| **Fix:** when more than one plugin shows home page results, their result batches no longer clear each other (upstream only showed the slowest plugin's results). | `MainViewModel.cs` |
| **Top apps** on the home page. The Program plugin shows the 6 most-launched apps, using Windows' own UserAssist launch counts. | `Plugin.Program/TopApps.cs` |
| **Recent files** on the home page. The Explorer plugin shows the 8 most recent files from the Windows Recent folder. | `Plugin.Explorer/Search/RecentFiles.cs` |
| **Pin to taskbar / Unpin from taskbar** at the top of the right-click menu of apps (desktop and Store) and pinnable files, using Windows' own wording and the Windows 10 pin/unpin symbols. Windows blocks other programs from pinning, so Flow selects the item in an Explorer window and chooses Explorer's own menu command; an Explorer window flashes for about a second. | `Plugin/SharedCommands/TaskbarPin.cs`, Program and Explorer plugins |
| **Windows 10 Taskbar** theme: square corners, taskbar-height search box, search icon on the left, follows system light/dark. | `Themes/Win10Taskbar.xaml` |

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
