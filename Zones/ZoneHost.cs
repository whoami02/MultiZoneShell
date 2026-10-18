using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiZoneShell.Config;

namespace MultiZoneShell.Zones;

public interface IZoneContent : IDisposable
{
    FrameworkElement View { get; }
    Task StartAsync();
    void FocusContent();
    void SetMuted(bool muted);
    bool IsMuted { get; }
    double ZoomFactor { get; }
    event Action? ViewStateChanged;
    void SetZoom(double zoom, double focalRatioX = 0.5, double focalRatioY = 0.5);
    void PanBy(double dx, double dy);
    void ResetView();
    void ReloadContent();
}

public static class ZoneFactory
{
    public static IZoneContent Create(ZoneConfig cfg) => cfg.Type.Trim().ToLowerInvariant() switch
    {
        "web" => new WebZone(cfg),
        "video" => new VideoZone(cfg),
        "image" => new ImageZone(cfg),
        "exe" => new ExeZone(cfg),
        _ => throw new NotSupportedException($"Unknown zone type '{cfg.Type}'. Use web | video | image | exe.")
    };
}

/// <summary>
/// One quadrant / grid cell styled after macOS Dark Mode windows.
/// The highlight and the header bar sit outside the content area because WPF
/// cannot paint over native child windows (WebView2 / VLC / embedded exe).
/// </summary>
public sealed class ZoneHost : Border
{
    private static readonly Brush ActiveBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private static readonly Brush SwapTargetBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    private static readonly Brush InactiveBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x3A)));
    private static readonly Brush HeaderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x22)));
    private static readonly Brush ActiveHeaderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)));
    private static readonly Brush SurfaceBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)));
    private static readonly Brush PillBgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0x34, 0x37)));
    private static readonly Brush PillHoverBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4C)));
    private static readonly Brush AccentBlueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private static readonly Brush PrimaryTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
    private static readonly Brush SecondaryTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9D)));

    // macOS Traffic Light colors
    private static readonly Brush MacRedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x56)));
    private static readonly Brush MacYellowBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xBD, 0x2E)));
    private static readonly Brush MacGreenBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x27, 0xC9, 0x3F)));
    private static readonly Brush MacLightDimBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A)));

    // Status dot colors
    private static readonly Brush StatusReadyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    private static readonly Brush StatusLoadingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)));
    private static readonly Brush StatusErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));

    // Segoe MDL2 Assets glyphs
    private const string GlyphMuted = "\uE74F";
    private const string GlyphUnmuted = "\uE767";
    private const string GlyphPanHand = "\uECE9";
    private const string GlyphTileLayout = "\uECA5";

    private int _gridRow, _gridCol, _gridRowSpan, _gridColSpan;
    private readonly bool _configuredShowHeader;
    private readonly Border _body = new() { Background = SurfaceBrush };
    private readonly Border _header;
    private readonly Border _statusDot;
    private readonly TextBlock _title;
    private readonly TextBlock _zoomLabel;
    private readonly Border _panButton;
    private readonly Border _muteButton;
    private readonly TextBlock _muteGlyph;
    private readonly Border _redLight;
    private readonly Border _yellowLight;
    private readonly Border _greenLight;
    private readonly TextBlock _redGlyph;
    private readonly TextBlock _yellowGlyph;
    private readonly TextBlock _greenGlyph;

    // Inertial panning state (macOS trackpad/Magic Mouse momentum)
    private readonly DispatcherTimer _inertiaTimer;
    private double _inertiaVx;
    private double _inertiaVy;

    // Header drag-to-swap state
    private bool _headerDragPending;
    private bool _isDraggingHeader;
    private Point _headerDragStart;
    private bool _isTrafficHovered;

    private bool _isActive;
    private bool _isMaximized;

    public int Index { get; set; }
    public ZoneConfig Config { get; }
    public IZoneContent? ZoneContent { get; private set; }
    public bool IsHandPanMode { get; private set; }

    public int GridRow => _gridRow;
    public int GridCol => _gridCol;
    public int GridRowSpan => _gridRowSpan;
    public int GridColSpan => _gridColSpan;

    /// <summary>Header maximize/restore button clicked, or header double-clicked.</summary>
    public event Action<ZoneHost>? MaximizeRequested;

    /// <summary>User chose "Focus This Quadrant (70/30)" or "Reset 2x2 Split" from the macOS Green Traffic Light menu.</summary>
    public event Action<ZoneHost, string>? TilePresetRequested;

    /// <summary>Header dragged and dropped onto another quadrant to swap positions.</summary>
    public event Action<ZoneHost, Point>? HeaderDragMoved;
    public event Action<ZoneHost, Point>? HeaderDragDropped;
    public event Action<ZoneHost>? HeaderDragCancelled;

    /// <summary>User chose "Column Focus (1 big + N stacked)" from the macOS Tile Menu.</summary>
    public event Action<ZoneHost>? ColumnFocusRequested;

    public ZoneHost(int index, ZoneConfig config, int gridRow, int gridCol, int gridRowSpan, int gridColSpan, int border, bool showHeader)
    {
        Index = index;
        Config = config;
        _gridRow = gridRow;
        _gridCol = gridCol;
        _gridRowSpan = gridRowSpan;
        _gridColSpan = gridColSpan;
        _configuredShowHeader = showHeader;

        BorderThickness = new Thickness(Math.Max(1, border));
        BorderBrush = InactiveBorderBrush;
        CornerRadius = new CornerRadius(6);
        Background = SurfaceBrush;
        ClipToBounds = true;

        _inertiaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _inertiaTimer.Tick += OnInertiaTick;

        // --- Left: macOS Traffic Lights (Red = Reload, Yellow = Reset View, Green = Fullscreen / Tile Menu) ---
        _redGlyph = CreateTrafficGlyph("↻");
        _yellowGlyph = CreateTrafficGlyph("−");
        _greenGlyph = CreateTrafficGlyph("+");

        _redLight = CreateTrafficLight(MacRedBrush, _redGlyph, "Reload / Restart Zone", () => ZoneContent?.ReloadContent());
        _yellowLight = CreateTrafficLight(MacYellowBrush, _yellowGlyph, "Reset Zoom & Pan to 100%", () =>
        {
            StopInertia();
            ZoneContent?.ResetView();
        });
        _greenLight = CreateTrafficLight(MacGreenBrush, _greenGlyph, "Full Screen Quadrant (Right-click for macOS Tile Menu)", () => MaximizeRequested?.Invoke(this));
        _greenLight.ContextMenu = BuildTileContextMenu();
        _greenLight.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_greenLight.ContextMenu != null)
            {
                _greenLight.ContextMenu.PlacementTarget = _greenLight;
                _greenLight.ContextMenu.Placement = PlacementMode.Bottom;
                _greenLight.ContextMenu.IsOpen = true;
            }
        };

        var trafficCluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 10, 0),
            Background = Brushes.Transparent
        };
        trafficCluster.Children.Add(_redLight);
        trafficCluster.Children.Add(_yellowLight);
        trafficCluster.Children.Add(_greenLight);

        trafficCluster.MouseEnter += (_, _) =>
        {
            _isTrafficHovered = true;
            _redGlyph.Visibility = Visibility.Visible;
            _yellowGlyph.Visibility = Visibility.Visible;
            _greenGlyph.Visibility = Visibility.Visible;
            UpdateTrafficLightColors();
        };
        trafficCluster.MouseLeave += (_, _) =>
        {
            _isTrafficHovered = false;
            _redGlyph.Visibility = Visibility.Hidden;
            _yellowGlyph.Visibility = Visibility.Hidden;
            _greenGlyph.Visibility = Visibility.Hidden;
            UpdateTrafficLightColors();
        };

        // --- Right: macOS Pill Controls (Hand Pan Toggle, [- 100% +] Zoom Pill, Audio Mute, Tile Menu Button) ---
        var rightControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 10, 0)
        };

        // 1. Hand Pan Mode Toggle
        var panGlyph = new TextBlock
        {
            Text = GlyphPanHand,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = PrimaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _panButton = CreatePillButton(panGlyph, 30, "Toggle macOS Hand Tool (Drag inside zone to pan, Double-click for Smart Zoom)", ToggleHandPanMode);
        _panButton.Margin = new Thickness(0, 0, 6, 0);
        rightControls.Children.Add(_panButton);

        // 2. Segmented Zoom Pill: [ - | 100% | + ]
        var zoomOutBtn = CreateSegmentButton("−", 26, "Zoom Out (Ctrl+Scroll Down)", () => StepZoom(-0.25));
        _zoomLabel = new TextBlock
        {
            Text = "100%",
            Foreground = PrimaryTextBrush,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0)
        };
        var zoomResetBtn = CreateSegmentButton(_zoomLabel, 48, "Click to Reset Zoom & Pan (100%)", () =>
        {
            StopInertia();
            ZoneContent?.ResetView();
        });
        var zoomInBtn = CreateSegmentButton("+", 26, "Zoom In (Ctrl+Scroll Up)", () => StepZoom(+0.25));

        var zoomStrip = new StackPanel { Orientation = Orientation.Horizontal };
        zoomStrip.Children.Add(zoomOutBtn);
        zoomStrip.Children.Add(new Border { Width = 1, Background = InactiveBorderBrush, Margin = new Thickness(0, 4, 0, 4) });
        zoomStrip.Children.Add(zoomResetBtn);
        zoomStrip.Children.Add(new Border { Width = 1, Background = InactiveBorderBrush, Margin = new Thickness(0, 4, 0, 4) });
        zoomStrip.Children.Add(zoomInBtn);

        var zoomPill = new Border
        {
            Background = PillBgBrush,
            CornerRadius = new CornerRadius(6),
            Height = 24,
            Margin = new Thickness(0, 0, 6, 0),
            Child = zoomStrip
        };
        rightControls.Children.Add(zoomPill);

        // 3. Audio Mute Pill (Web & Video zones)
        _muteGlyph = new TextBlock
        {
            Text = config.Muted ? GlyphMuted : GlyphUnmuted,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12.5,
            Foreground = config.Muted ? SecondaryTextBrush : PrimaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _muteButton = CreatePillButton(_muteGlyph, 30, "Toggle Zone Audio Mute (Ctrl+Alt+A for solo)", () =>
        {
            if (ZoneContent != null) ZoneContent.SetMuted(!ZoneContent.IsMuted);
        });
        bool supportsAudio = config.Type.Equals("web", StringComparison.OrdinalIgnoreCase) ||
                             config.Type.Equals("video", StringComparison.OrdinalIgnoreCase);
        _muteButton.Visibility = supportsAudio ? Visibility.Visible : Visibility.Collapsed;
        _muteButton.Margin = new Thickness(0, 0, 6, 0);
        rightControls.Children.Add(_muteButton);

        // 4. macOS Tiling Preset Menu Button
        var tileGlyph = new TextBlock
        {
            Text = GlyphTileLayout,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Foreground = PrimaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Border? tileBtn = null;
        tileBtn = CreatePillButton(tileGlyph, 30, "macOS Split View & Tile Options", () =>
        {
            if (_greenLight.ContextMenu != null && tileBtn != null)
            {
                _greenLight.ContextMenu.PlacementTarget = tileBtn;
                _greenLight.ContextMenu.Placement = PlacementMode.Bottom;
                _greenLight.ContextMenu.IsOpen = true;
            }
        });
        rightControls.Children.Add(tileBtn);

        // --- Center: Status Dot + Zone Title (draggable to swap quadrants) ---
        _statusDot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = StatusLoadingBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        _title = new TextBlock
        {
            Text = TitleFor(index, config),
            Foreground = SecondaryTextBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        titleStack.Children.Add(_statusDot);
        titleStack.Children.Add(_title);

        var headerGrid = new Grid { Height = 36 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(trafficCluster, 0);
        Grid.SetColumn(titleStack, 1);
        Grid.SetColumn(rightControls, 2);

        headerGrid.Children.Add(trafficCluster);
        headerGrid.Children.Add(titleStack);
        headerGrid.Children.Add(rightControls);

        var headerContainer = new DockPanel();
        var headerBottomLine = new Border { Height = 1, Background = InactiveBorderBrush };
        DockPanel.SetDock(headerBottomLine, Dock.Bottom);
        headerContainer.Children.Add(headerBottomLine);
        headerContainer.Children.Add(headerGrid);

        _header = new Border
        {
            Background = HeaderBrush,
            Child = headerContainer,
            Cursor = Cursors.Hand,
            ToolTip = "Drag header to swap quadrant • Double-click to expand",
            Visibility = showHeader ? Visibility.Visible : Visibility.Collapsed
        };

        // Header interactions: Double-click to maximize, Drag to swap quadrants
        _header.MouseLeftButtonDown += OnHeaderMouseLeftButtonDown;
        _header.MouseMove += OnHeaderMouseMove;
        _header.MouseLeftButtonUp += OnHeaderMouseLeftButtonUp;
        _header.LostMouseCapture += OnHeaderLostMouseCapture;

        var layout = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        layout.Children.Add(_header);
        layout.Children.Add(_body);
        Child = layout;

        _body.Child = CreateStatusCard($"Zone {index + 1}", $"Launching {config.Type.ToUpperInvariant()}…", isError: false);
    }

    private ContextMenu BuildTileContextMenu()
    {
        var menu = new ContextMenu();
        AddItem("Expand / Restore Quadrant (Ctrl+Alt+M)", () => MaximizeRequested?.Invoke(this));
        AddItem("Focus This Quadrant (70% / 30% Split)", () => TilePresetRequested?.Invoke(this, "focus"));
        AddItem("Column Focus — 1 Big + Stacked (Ctrl+Alt+L)", () => ColumnFocusRequested?.Invoke(this));
        AddItem("Reset 2×2 Equal Split (50% / 50%)", () => TilePresetRequested?.Invoke(this, "equal"));
        menu.Items.Add(new Separator());
        AddItem("Smart Zoom 200%", () => ZoneContent?.SetZoom(2.0));
        AddItem("Reset Zoom & Pan (100%)", () => { StopInertia(); ZoneContent?.ResetView(); });
        menu.Items.Add(new Separator());
        AddItem("Reload Zone Content", () => ZoneContent?.ReloadContent());
        return menu;

        void AddItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _headerDragPending = false;
            MaximizeRequested?.Invoke(this);
            e.Handled = true;
            return;
        }

        _headerDragPending = true;
        _isDraggingHeader = false;
        _headerDragStart = e.GetPosition(this);
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (!_headerDragPending || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        if (!_isDraggingHeader && (Math.Abs(pos.X - _headerDragStart.X) > 10 || Math.Abs(pos.Y - _headerDragStart.Y) > 10))
        {
            _isDraggingHeader = true;
            _header.CaptureMouse();
            Opacity = 0.82;
        }

        if (_isDraggingHeader && PresentationSource.FromVisual(this) != null)
        {
            var screenPt = PointToScreen(pos);
            HeaderDragMoved?.Invoke(this, screenPt);
        }
    }

    private void OnHeaderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _headerDragPending = false;
        if (_isDraggingHeader)
        {
            _isDraggingHeader = false;
            var screenPt = PresentationSource.FromVisual(this) != null ? PointToScreen(e.GetPosition(this)) : new Point(-1, -1);
            _header.ReleaseMouseCapture();
            Opacity = 1.0;
            if (screenPt.X >= 0) HeaderDragDropped?.Invoke(this, screenPt);
            else HeaderDragCancelled?.Invoke(this);
            e.Handled = true;
        }
    }

    private void OnHeaderLostMouseCapture(object sender, MouseEventArgs e)
    {
        _headerDragPending = false;
        if (_isDraggingHeader)
        {
            _isDraggingHeader = false;
            Opacity = 1.0;
            HeaderDragCancelled?.Invoke(this);
        }
    }

    public void SetSwapHighlight(bool highlight)
    {
        if (highlight)
            BorderBrush = SwapTargetBorderBrush;
        else
            BorderBrush = _isActive ? ActiveBorderBrush : InactiveBorderBrush;
    }

    public void SetHeaderVisible(bool visible)
    {
        _header.Visibility = (visible && _configuredShowHeader) ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ToggleHandPanMode()
    {
        IsHandPanMode = !IsHandPanMode;
        _panButton.Background = IsHandPanMode ? AccentBlueBrush : PillBgBrush;
    }

    public bool IsScreenPointInBody(int screenX, int screenY)
    {
        if (!IsVisible || _body.ActualWidth <= 0 || _body.ActualHeight <= 0) return false;
        if (PresentationSource.FromVisual(_body) == null) return false;
        try
        {
            var tl = _body.PointToScreen(new Point(0, 0));
            var br = _body.PointToScreen(new Point(_body.ActualWidth, _body.ActualHeight));
            return screenX >= tl.X && screenX < br.X && screenY >= tl.Y && screenY < br.Y;
        }
        catch
        {
            return false;
        }
    }

    public (double ratioX, double ratioY) GetBodyFocalRatio(int screenX, int screenY)
    {
        if (!IsVisible || _body.ActualWidth <= 0 || _body.ActualHeight <= 0) return (0.5, 0.5);
        if (PresentationSource.FromVisual(_body) == null) return (0.5, 0.5);
        try
        {
            var tl = _body.PointToScreen(new Point(0, 0));
            var br = _body.PointToScreen(new Point(_body.ActualWidth, _body.ActualHeight));
            double rx = Math.Clamp((screenX - tl.X) / Math.Max(1.0, br.X - tl.X), 0.0, 1.0);
            double ry = Math.Clamp((screenY - tl.Y) / Math.Max(1.0, br.Y - tl.Y), 0.0, 1.0);
            return (rx, ry);
        }
        catch
        {
            return (0.5, 0.5);
        }
    }

    public void StepZoom(double delta, double focalRatioX = 0.5, double focalRatioY = 0.5)
    {
        if (ZoneContent == null) return;
        StopInertia();
        ZoneContent.SetZoom(ZoneContent.ZoomFactor + delta, focalRatioX, focalRatioY);
    }

    public void ToggleSmartZoom(double focalRatioX = 0.5, double focalRatioY = 0.5)
    {
        if (ZoneContent == null) return;
        StopInertia();
        if (Math.Abs(ZoneContent.ZoomFactor - 1.0) < 0.08)
            ZoneContent.SetZoom(2.0, focalRatioX, focalRatioY);
        else
            ZoneContent.ResetView();
    }

    public void StartInertia(double vx, double vy)
    {
        if (Math.Abs(vx) < 1.2 && Math.Abs(vy) < 1.2) return;
        _inertiaVx = Math.Clamp(vx, -80, 80);
        _inertiaVy = Math.Clamp(vy, -80, 80);
        _inertiaTimer.Start();
    }

    public void StopInertia()
    {
        _inertiaTimer.Stop();
        _inertiaVx = 0;
        _inertiaVy = 0;
    }

    private void OnInertiaTick(object? sender, EventArgs e)
    {
        if (ZoneContent == null || (Math.Abs(_inertiaVx) < 0.4 && Math.Abs(_inertiaVy) < 0.4))
        {
            StopInertia();
            return;
        }

        ZoneContent.PanBy(_inertiaVx, _inertiaVy);
        _inertiaVx *= 0.89;
        _inertiaVy *= 0.89;
    }

    public void RefreshHeaderText()
    {
        _title.Text = TitleFor(Index, Config);
    }

    private static string TitleFor(int index, ZoneConfig c)
    {
        var name = c.Title;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Uri.TryCreate(c.Source, UriKind.Absolute, out var uri) && !uri.IsFile
                ? uri.Host
                : Path.GetFileNameWithoutExtension(c.Source.TrimEnd('\\', '/'));
        }
        string typeTag = c.Type.Trim().ToUpperInvariant();
        return $"{index + 1} · {name}  ({typeTag})";
    }

    public void SetMaximized(bool maximized)
    {
        _isMaximized = maximized;
        _greenGlyph.Text = _isMaximized ? "−" : "+";
    }

    public void SetGridPlacement(int gridRow, int gridCol, int gridRowSpan, int gridColSpan)
    {
        _gridRow = gridRow;
        _gridCol = gridCol;
        _gridRowSpan = gridRowSpan;
        _gridColSpan = gridColSpan;
        ApplyPlacement();
    }

    public void ApplyPlacement()
    {
        Grid.SetRow(this, _gridRow);
        Grid.SetColumn(this, _gridCol);
        Grid.SetRowSpan(this, _gridRowSpan);
        Grid.SetColumnSpan(this, _gridColSpan);
    }

    public void SetActive(bool active)
    {
        _isActive = active;
        BorderBrush = active ? ActiveBorderBrush : InactiveBorderBrush;
        _title.Foreground = active ? PrimaryTextBrush : SecondaryTextBrush;
        _header.Background = active ? ActiveHeaderBrush : HeaderBrush;
        UpdateTrafficLightColors();
    }

    private void UpdateTrafficLightColors()
    {
        if (_isActive || _isTrafficHovered)
        {
            _redLight.Background = MacRedBrush;
            _yellowLight.Background = MacYellowBrush;
            _greenLight.Background = MacGreenBrush;
        }
        else
        {
            _redLight.Background = MacLightDimBrush;
            _yellowLight.Background = MacLightDimBrush;
            _greenLight.Background = MacLightDimBrush;
        }
    }

    public void SetContent(IZoneContent content)
    {
        DisposeContent();
        ZoneContent = content;
        ZoneContent.ViewStateChanged += SyncHeaderControls;
        _statusDot.Background = StatusReadyBrush;
        _body.Child = content.View;
        SyncHeaderControls();
    }

    private void SyncHeaderControls()
    {
        if (ZoneContent == null) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (ZoneContent == null) return;
            int pct = (int)Math.Round(ZoneContent.ZoomFactor * 100);
            _zoomLabel.Text = $"{pct}%";
            _zoomLabel.Foreground = pct != 100 ? AccentBlueBrush : PrimaryTextBrush;

            bool muted = ZoneContent.IsMuted;
            _muteGlyph.Text = muted ? GlyphMuted : GlyphUnmuted;
            _muteGlyph.Foreground = muted ? SecondaryTextBrush : AccentBlueBrush;
        });
    }

    public void ShowError(string message)
    {
        DisposeContent();
        _statusDot.Background = StatusErrorBrush;
        _body.Child = CreateStatusCard($"Zone {Index + 1} ({Config.Type.ToUpperInvariant()})", message, isError: true);
    }

    private UIElement CreateStatusCard(string heading, string detail, bool isError)
    {
        var stack = new StackPanel
        {
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24)
        };

        stack.Children.Add(new TextBlock
        {
            Text = heading,
            Foreground = isError ? StatusErrorBrush : PrimaryTextBrush,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        });

        stack.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = SecondaryTextBrush,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        return new Border
        {
            Background = Freeze(new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x26))),
            BorderBrush = isError ? StatusErrorBrush : InactiveBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(24, 18, 24, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack
        };
    }

    public void DisposeContent()
    {
        StopInertia();
        var c = ZoneContent;
        if (c != null) c.ViewStateChanged -= SyncHeaderControls;
        ZoneContent = null;
        _body.Child = null;
        c?.Dispose();
    }

    private static TextBlock CreateTrafficGlyph(string text) => new()
    {
        Text = text,
        FontSize = 9.5,
        FontWeight = FontWeights.Bold,
        Foreground = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Hidden
    };

    private static Border CreateTrafficLight(Brush color, UIElement glyph, string tooltip, Action onClick)
    {
        var light = new Border
        {
            Width = 13,
            Height = 13,
            CornerRadius = new CornerRadius(6.5),
            Background = color,
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = glyph
        };
        light.MouseLeftButtonDown += (_, e) => e.Handled = true;
        light.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return light;
    }

    private static Border CreatePillButton(UIElement child, double width, string tooltip, Action onClick)
    {
        var b = new Border
        {
            Width = width,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = PillBgBrush,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = child
        };
        b.MouseEnter += (_, _) =>
        {
            if (b.Background != AccentBlueBrush) b.Background = PillHoverBrush;
        };
        b.MouseLeave += (_, _) =>
        {
            if (b.Background != AccentBlueBrush) b.Background = PillBgBrush;
        };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return b;
    }

    private static Border CreateSegmentButton(object content, double width, string tooltip, Action onClick)
    {
        UIElement child = content is UIElement el ? el : new TextBlock
        {
            Text = content.ToString(),
            Foreground = PrimaryTextBrush,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var b = new Border
        {
            Width = width,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = child
        };
        b.MouseEnter += (_, _) => b.Background = PillHoverBrush;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return b;
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
