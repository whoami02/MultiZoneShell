# MultiZoneShell

A fullscreen WPF shell that splits one display into independently resizable zones (default 2×2). Each zone runs a **web page**, **video/stream**, **image/slideshow**, or **any Windows .exe**, controlled by one shared keyboard and mouse. Built for control-room walls, kiosks, live demos and billboards.

## What's new in v1.1.0

- **Secondary display support** — `"Display": 1` opens the shell on the monitor you choose (0 = primary).
- **Photo-album Presentation mode** (`Ctrl+Alt+P`) — auto-rotates focus across zones for billboards/live demos (`spotlight` / `column` / `revolve`).
- **macOS Split View** — pill dividers with magnetic snapping (25% / 33% / 50% / 67% / 75%), a 4-way center handle, double-click to reset 50/50.
- **Drag-to-swap tiles** — double-click any tile to pick it up, drag, release to swap (works with title bars hidden).
- **Wayland-style Column Focus** (`Ctrl+Alt+L`) — one zone full-height on the left, the rest stacked on the right.
- **Native Pan & Zoom** — `Ctrl+Scroll` focal zoom, drag-to-pan with inertia, macOS Smart Zoom (`Ctrl+Alt+Double-click`) across web / video / image / exe zones.
- **Cleaner chrome** — on-screen zoom buttons removed; everything is keyboard-driven. Traffic-light controls, auto-hiding floating dock, dark mode.

## Requirements
- Windows 10/11 x64
- .NET 8 SDK (build only — published binaries are self-contained)
- WebView2 Runtime (preinstalled on Windows 11)
- NuGet access. LibVLC native binaries come from the `VideoLAN.LibVLC.Windows` package.

## Build / run
```
dotnet restore
dotnet run
```
Or open `MultiZoneShell.csproj` in Visual Studio 2022 and press F5.

Publish a self-contained exe:
```
dotnet publish -c Release -r win-x64 --self-contained true
```

The config is `zones.json` next to the exe. Pass a different file as the first argument: `MultiZoneShell.exe D:\configs\line1.json`. Logs go to `%LOCALAPPDATA%\MultiZoneShell\logs`.

## Hotkeys (global)
| Keys | Action |
|---|---|
| `Ctrl+Alt+1…9` | Focus zone N |
| `Ctrl+Alt+M` | Maximize / restore active zone |
| `Ctrl+Alt+L` | Column focus (1 big + stacked) |
| `Ctrl+Alt+P` | Toggle photo-album Presentation mode |
| `Ctrl+Alt+A` | Play audio from active zone only |
| `Ctrl+Alt+0` | Mute all |
| `Ctrl+Alt+F` | Toggle full screen |
| `Ctrl+Alt+H` | Toggle zone headers (clean wall) |
| `Ctrl+Alt+D` | Toggle the floating dock |
| `Ctrl+Alt++ / − / Home` | Zoom in / out / reset (active zone) |
| `Ctrl+Alt+R` | Reload zones.json |
| `Ctrl+Alt+Q` | Quit |

Mouse gestures:
- `Ctrl+Scroll` — focal-point zoom under the cursor.
- `Ctrl+Alt+Drag` or middle-drag — pan a zoomed zone (with inertia).
- `Ctrl+Alt+Double-click` — macOS Smart Zoom (100% ⇄ 200%).
- **Double-click + drag** — pick up a tile and drop it on another to swap.

## Configuration

Top-level options:
| Key | Default | Description |
|---|---|---|
| `Rows` / `Cols` | 2 / 2 | Grid dimensions |
| `Display` | 0 | Monitor index (0 = primary, 1 = secondary, …) |
| `ZoneBorder` | 3 | Border thickness between zones |
| `Topmost` | false | Keep window above others |
| `Fullscreen` | false | Start borderless fullscreen |
| `ZoneHeaders` | true | Show per-zone header bars |

Example:
```json
{
  "Rows": 2, "Cols": 2,
  "Display": 1,
  "Fullscreen": true,
  "ZoneHeaders": false,
  "Zones": [ ... ]
}
```

Clicking any zone makes it active. Hotkeys are global because a focused zone swallows the keyboard, and Ctrl+Alt combos avoid clashing with shortcuts inside embedded apps.

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

If a local video/image/exe file is missing at startup, a file-picker dialog opens so the operator can point that zone at a replacement.

## Presentation mode (photo album)

Turns the wall into a self-running gallery that cycles focus across zones — ideal for billboards and unattended live demos.

```json
{
  "Presentation": {
    "Enabled": true,
    "Mode": "spotlight",        // spotlight | column | revolve
    "IntervalSeconds": 15,
    "IdleSeconds": 30,          // auto-start after idle (0 = only manual Ctrl+Alt+P)
    "Order": "sequential",      // sequential | random
    "PauseOnInteraction": true
  }
}
```

- `spotlight` — active pane enlarges to 70/30 with an accent glow; others recede.
- `column` — each pane in turn takes the full-height left column; the rest stack right.
- `revolve` — each pane goes full-screen in sequence.

Any mouse/keyboard interaction pauses the show (when `PauseOnInteraction` is true) and returns manual control.

## Exe zones: notes
- **Launcher apps** (the exe starts another process that owns the window): set `ProcessName` to the process that owns the window.
- **Splash screens:** set `WindowTitleContains` so the main window is embedded, not the splash.
- **Store/UWP apps** (new Calculator, Photos, etc.) can't be reparented. Use a Win32 alternative.
- **Elevated apps:** if the target runs as admin, run the shell as admin too.
- **Sizing:** `"Fit": "stretch"` (default) makes the app window exactly the zone size, never scrolled. Apps with a fixed layout get cut off at the edge, because the shell can resize another app's window but not scale its controls. `"Fit": "scroll"` keeps the app at its own size and adds scrollbars instead.
- **Browsers:** use a `web` zone instead of embedding chrome.exe or msedge.exe.
- If the app closes, the watchdog relaunches it (throttled to once per 10s). If the app recreates its window, the watchdog re-attaches it.

## Kiosk setup
1. Set up auto-login for a dedicated user (`netplwiz`, or Autologon from Sysinternals).
2. Create a Task Scheduler task with trigger **At log on**, action `MultiZoneShell.exe`, and **Run with highest privileges** if any exe needs admin.
3. The shell keeps the display awake while running. Also disable Windows Update active-hours restarts.

## License
Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0). See [LICENSE](LICENSE).

## Known limits / next steps
- No per-app audio mute for exe zones (needs the Core Audio session API).
- The cursor is not auto-hidden over native zones.
- A dead WebView2 browser process (rare) needs Ctrl+Alt+R to recover.
