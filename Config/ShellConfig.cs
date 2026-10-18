using System.IO;
using System.Text.Json;

namespace MultiZoneShell.Config;

public sealed class ShellConfig
{
    public int Rows { get; set; } = 2;
    public int Cols { get; set; } = 2;
    public int ZoneBorder { get; set; } = 3;
    public bool Topmost { get; set; }
    public bool Fullscreen { get; set; }
    /// <summary>0-based index of the monitor the shell opens on (0 = primary, 1 = secondary, …).</summary>
    public int Display { get; set; }
    /// <summary>Show the Netflix-style zone setup wizard on launch instead of using Zones.</summary>
    public bool Setup { get; set; }
    /// <summary>Header bar per zone with its name and a maximize/restore button.</summary>
    public bool ZoneHeaders { get; set; } = true;
    /// <summary>Auto-rotating "photo album" presentation mode (billboards / live demos).</summary>
    public PresentationConfig Presentation { get; set; } = new();
    public List<ZoneConfig> Zones { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ShellConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("zones.json not found.", path);

        var cfg = JsonSerializer.Deserialize<ShellConfig>(File.ReadAllText(path), JsonOptions)
                  ?? throw new InvalidDataException("zones.json is empty.");

        return cfg;
    }

    public static void Save(string path, ShellConfig cfg)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class PresentationConfig
{
    /// <summary>Master switch for attract/billboard mode (also toggled with Ctrl+Alt+P).</summary>
    public bool Enabled { get; set; }
    /// <summary>spotlight (70/30 emphasis) | column (full-height left) | revolve (fullscreen cycle).</summary>
    public string Mode { get; set; } = "spotlight";
    /// <summary>Seconds each pane holds the spotlight before advancing.</summary>
    public int IntervalSeconds { get; set; } = 15;
    /// <summary>Auto-start after this many seconds of inactivity (0 = only manual Ctrl+Alt+P).</summary>
    public int IdleSeconds { get; set; } = 30;
    /// <summary>sequential | random.</summary>
    public string Order { get; set; } = "sequential";
    /// <summary>Pause the show the moment the operator touches the mouse or keyboard.</summary>
    public bool PauseOnInteraction { get; set; } = true;
}

public sealed class ZoneConfig
{
    /// <summary>web | video | image | exe</summary>
    public string Type { get; set; } = "web";
    public string Source { get; set; } = string.Empty;
    /// <summary>Header text; defaults to the host name / file name of Source.</summary>
    public string? Title { get; set; }

    // Placement (optional; auto-filled if Row/Col omitted)
    public int? Row { get; set; }
    public int? Col { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;

    // web / video
    public bool Muted { get; set; } = true;

    // web
    public int RefreshSeconds { get; set; }

    // video
    public bool Loop { get; set; } = true;
    public int NetworkCachingMs { get; set; } = 500;

    // image
    public int SlideSeconds { get; set; } = 10;

    // exe
    public string? Args { get; set; }
    public string? ProcessName { get; set; }
    public string? WindowTitleContains { get; set; }
    public int StartupTimeoutSeconds { get; set; } = 20;
    public bool RestartOnExit { get; set; } = true;
    public bool KillOnShellExit { get; set; } = true;
    /// <summary>stretch: window is always exactly the zone size | scroll: fill the zone but never shrink the app below its own size (scrollbars instead).</summary>
    public string Fit { get; set; } = "stretch";
}
