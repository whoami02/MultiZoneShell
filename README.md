# MultiZoneShell (POC)

A fullscreen WPF shell that splits one display into independent zones (default 2×2). Each zone runs a **web page**, **video/stream**, **image/slideshow**, or **any Windows .exe**, controlled by one shared keyboard and mouse.

## Requirements
- Windows 10/11 x64
- .NET 8 SDK
- WebView2 Runtime (preinstalled on Windows 11)
- NuGet access. LibVLC native binaries come from the `VideoLAN.LibVLC.Windows` package, so nothing needs installing on the PC.

## Build / run
```
dotnet restore
dotnet run
```
Or open `MultiZoneShell.csproj` in Visual Studio 2022 and press F5.

The config is `zones.json` next to the exe. Pass a different file as the first argument: `MultiZoneShell.exe D:\configs\line1.json`
Logs go to `%LOCALAPPDATA%\MultiZoneShell\logs`.

## Hotkeys (global)
| Keys | Action |
|---|---|
| Ctrl+Alt+1…9 | Focus zone N |
| Ctrl+Alt+M | Maximize / restore active zone |
| Ctrl+Alt+A | Play audio from active zone only |
| Ctrl+Alt+0 | Mute all |
| Ctrl+Alt+R | Reload zones.json |
| Ctrl+Alt+F | Toggle full screen (also in the title-bar right-click menu) |
| Ctrl+Alt+Q | Quit |

Set `"Fullscreen": true` in zones.json to start in full screen (no title bar, taskbar covered).

Each zone has a header bar with its name and a maximize/restore button. Double-clicking the header also maximizes the zone. Set `"ZoneHeaders": false` to hide the headers (Ctrl+Alt+M still works), and use `Title` on a zone to rename it.

Clicking any zone makes it active (blue border). The hotkeys are global because a focused zone swallows the keyboard, and Ctrl+Alt combos avoid clashing with shortcuts inside embedded apps.

## Zone types
| Type | Source | Options |
|---|---|---|
| `web` | URL or local .html | `Muted`, `RefreshSeconds` |
| `video` | file path, `rtsp://`, `http://`, `udp://` | `Loop`, `Muted`, `NetworkCachingMs` |
| `image` | image file or folder | `SlideSeconds` |
| `exe` | path to .exe | `Args`, `ProcessName`, `WindowTitleContains`, `StartupTimeoutSeconds`, `RestartOnExit`, `KillOnShellExit`, `Fit` |

Every zone also accepts `Row`, `Col`, `RowSpan` and `ColSpan` for custom layouts. For example, 1 big + 3 small on a 3×3 grid:
```json
"Rows": 3, "Cols": 3,
"Zones": [
  { "Type": "web",   "Source": "http://server/andon", "Row": 0, "Col": 0, "RowSpan": 3, "ColSpan": 2 },
  { "Type": "video", "Source": "rtsp://cam1",          "Row": 0, "Col": 2 },
  { "Type": "video", "Source": "rtsp://cam2",          "Row": 1, "Col": 2 },
  { "Type": "image", "Source": "D:\\Slides",           "Row": 2, "Col": 2 }
]
```

## Exe zones: notes
- **Launcher apps** (the exe starts another process that owns the window): set `ProcessName` to the process that owns the window.
- **Splash screens:** set `WindowTitleContains` so the main window is embedded, not the splash.
- **Store/UWP apps** (new Calculator, Photos, etc.) can't be reparented. Use a Win32 alternative.
- **Elevated apps:** if the target runs as admin, run the shell as admin too.
- **Sizing:** `"Fit": "stretch"` (default) makes the app window exactly the zone size, never scrolled. Apps with a fixed layout (controls at fixed pixel positions) get cut off at the edge, because the shell can resize another app's window but not scale its controls; that app has to scale itself on resize. `"Fit": "scroll"` keeps the app at its own size and adds scrollbars instead (mouse wheel scrolls; Shift+wheel scrolls sideways).
- **Browsers:** use a `web` zone instead of embedding chrome.exe or msedge.exe.
- If the app closes, the watchdog relaunches it (throttled to once per 10s). If the app recreates its window, the watchdog re-attaches it.

## Kiosk setup
1. Set up auto-login for a dedicated user (`netplwiz`, or Autologon from Sysinternals).
2. Create a Task Scheduler task with trigger **At log on**, action `MultiZoneShell.exe`, and **Run with highest privileges** if any exe needs admin.
3. The shell keeps the display awake while running. Also disable Windows Update active-hours restarts.

## Known POC limits / next steps
- Opens on the primary monitor. Adding monitor selection is a small change.
- No per-app audio mute for exe zones. That needs the Core Audio session API.
- The cursor is not auto-hidden over native zones.
- A dead WebView2 browser process (rare) needs Ctrl+Alt+R to recover.
