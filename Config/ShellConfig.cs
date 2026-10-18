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
    /// <summary>Header bar per zone with its name and a maximize/restore button.</summary>
    public bool ZoneHeaders { get; set; } = true;
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

        if (cfg.Zones.Count == 0)
            throw new InvalidDataException("zones.json has no zones.");

        return cfg;
    }
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
