using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiZoneShell.Config;
using MultiZoneShell.Infrastructure;
using MultiZoneShell.Interop;
using MultiZoneShell.Setup;
using MultiZoneShell.Zones;

namespace MultiZoneShell;

public partial class MainWindow : Window
{
    // Hotkey ids
    private const int HkZoneBase = 1;      // 1..9 => Ctrl+Alt+1..9
    private const int HkMaximize = 100;    // Ctrl+Alt+M
    private const int HkAudio = 101;       // Ctrl+Alt+A
    private const int HkMuteAll = 102;     // Ctrl+Alt+0
    private const int HkReload = 103;      // Ctrl+Alt+R
    private const int HkQuit = 104;        // Ctrl+Alt+Q
    private const int HkFullscreen = 105;  // Ctrl+Alt+F
    private const int HkHeaders = 106;     // Ctrl+Alt+H
    private const int HkDock = 107;        // Ctrl+Alt+D
    private const int HkZoomIn = 108;      // Ctrl+Alt+Plus
    private const int HkZoomOut = 109;     // Ctrl+Alt+Minus
    private const int HkZoomReset = 110;   // Ctrl+Alt+Home
    private const int HkColumn = 111;      // Ctrl+Alt+L (Wayland 1+3 column focus)
    private const int HkPresentation = 112;// Ctrl+Alt+P (auto-rotating presentation)
    private const int HkSetup = 113;        // Ctrl+Alt+S (reconfigure zones)

    // Custom system-menu command id (must be below 0xF000 with the low 4 bits clear).
    private const uint SysMenuFullscreen = 0x1000;

    // Magnetic snap ratios (macOS Split View detents: 25%, 33.3%, 50%, 66.7%, 75%)
    private static readonly double[] SnapRatios = { 0.25, 1.0 / 3.0, 0.50, 2.0 / 3.0, 0.75 };
    private const double SnapThresholdPixels = 14.0;

    private readonly List<ZoneHost> _zones = new();
    private readonly List<UIElement> _splitters = new();
    private ZoneHost? _swapSource;
    private ShellConfig _cfg = new();
    private IntPtr _hwnd;
    private HwndSource? _source;
    private GlobalMouseHook? _mouseHook;
    private DispatcherTimer? _dockHideTimer;
    private DispatcherTimer? _presentationTimer;
    private DispatcherTimer? _idleTimer;
    private DateTime _lastInput = DateTime.UtcNow;
    private int _presentationIdx = -1;
    private bool _presentationActive;
    private int _active = -1;
    private bool _maximized;
    private bool _building;
    private bool _fullscreen;
    private bool _headersVisible = true;
    private bool _columnMode;
    private WindowState _preFullscreenState = WindowState.Maximized;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => RepositionMacDock();
        LocationChanged += (_, _) => RepositionMacDock();
        Deactivated += (_, _) => HideMacDock();
        // Unhook global mouse first, then dispose zones while our HWND still exists so embedded apps are detached, not destroyed.
        Closing += (_, _) =>
        {
            _mouseHook?.Dispose();
            _mouseHook = null;
            DisposeZones();
        };
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);

        var sysMenu = NativeMethods.GetSystemMenu(_hwnd, false);
        NativeMethods.AppendMenu(sysMenu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
        NativeMethods.AppendMenu(sysMenu, NativeMethods.MF_STRING, (UIntPtr)SysMenuFullscreen, "Full screen\tCtrl+Alt+F");

        RegisterHotkeys();
        BuildMacDockUi();

        _dockHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _dockHideTimer.Tick += (_, _) =>
        {
            if (!MacDockBorder.IsMouseOver)
            {
                MacDockPopup.IsOpen = false;
                _dockHideTimer.Stop();
            }
        };

        _mouseHook = new GlobalMouseHook
        {
            IsPointInZoneBodyAt = (x, y) =>
            {
                var z = ZoneAt(x, y);
                return z != null && z.IsScreenPointInBody(x, y);
            },
            IsHandToolActiveAt = (x, y) =>
            {
                var z = ZoneAt(x, y);
                return z != null && z.IsHandPanMode && z.IsScreenPointInBody(x, y);
            }
        };
        _mouseHook.ButtonDown += (x, y) => Dispatcher.InvokeAsync(() => OnGlobalClick(x, y));
        _mouseHook.Wheel += (x, y, delta, horizontal) => Dispatcher.InvokeAsync(() => OnGlobalWheel(x, y, delta, horizontal));
        _mouseHook.PanDelta += (ox, oy, dx, dy) => Dispatcher.InvokeAsync(() => OnGlobalPanDelta(ox, oy, dx, dy));
        _mouseHook.PanEnded += (ox, oy, vx, vy) => Dispatcher.InvokeAsync(() => OnGlobalPanEnded(ox, oy, vx, vy));
        _mouseHook.SmartZoomRequested += (x, y) => Dispatcher.InvokeAsync(() => OnGlobalSmartZoom(x, y));
        _mouseHook.CursorMoved += (x, y) => Dispatcher.InvokeAsync(() => OnGlobalCursorMoved(x, y));
        _mouseHook.DoubleClick += (x, y) => Dispatcher.InvokeAsync(() => OnSwapDragStart(x, y));
        _mouseHook.SwapDragMove += (x, y) => Dispatcher.InvokeAsync(() => OnSwapDragMove(x, y));
        _mouseHook.SwapDragEnded += (x, y) => Dispatcher.InvokeAsync(() => OnSwapDragEnd(x, y));

        NativeMethods.KeepDisplayOn();
        await BuildAsync();
        StartPresentationWatcher();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _dockHideTimer?.Stop();
        _presentationTimer?.Stop();
        _idleTimer?.Stop();
        MacDockPopup.IsOpen = false;
        UnregisterHotkeys();
        _mouseHook?.Dispose();
        _source?.RemoveHook(WndProc);
        NativeMethods.AllowSleep();
    }

    // ---------------- Layout & macOS Split View ----------------

    private async Task BuildAsync()
    {
        if (_building) return;
        _building = true;
        try
        {
            try
            {
                _cfg = ShellConfig.Load(App.ConfigPath);
            }
            catch (Exception ex)
            {
                Log.Error("Config load failed", ex);
                ZoneGrid.Children.Add(new TextBlock
                {
                    Text = $"Config error:\n{App.ConfigPath}\n\n{ex.Message}",
                    Foreground = Brushes.OrangeRed,
                    FontSize = 20,
                    Margin = new Thickness(40),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            if (_cfg.Setup || _cfg.Zones.Count == 0)
            {
                if (!RunSetup(_cfg))
                {
                    Application.Current.Shutdown();
                    return;
                }
                ShellConfig.Save(App.ConfigPath, _cfg);
            }

            Topmost = _cfg.Topmost;
            _headersVisible = _cfg.ZoneHeaders;
            ApplyDisplay(_cfg.Display);
            if (_cfg.Fullscreen) SetFullscreen(true);
            else WindowState = WindowState.Maximized;

            ZoneGrid.Children.Clear();
            _splitters.Clear();

            for (int i = 0; i < _cfg.Zones.Count; i++)
            {
                var zc = _cfg.Zones[i];
                var host = new ZoneHost(i, zc, 0, 0, 1, 1, _cfg.ZoneBorder, _cfg.ZoneHeaders);
                host.MaximizeRequested += OnZoneMaximizeRequested;
                host.TilePresetRequested += OnZoneTilePresetRequested;
                host.HeaderDragMoved += OnZoneHeaderDragMoved;
                host.HeaderDragDropped += OnZoneHeaderDragDropped;
                host.HeaderDragCancelled += _ => ClearSwapHighlights();
                host.ColumnFocusRequested += OnColumnFocusRequested;
                ZoneGrid.Children.Add(host);
                _zones.Add(host);
            }

            RebuildLayout();

            if (_zones.Count > 0) SetActive(_zones[0].Index, focus: false);

            // Start all zones in parallel so a slow exe doesn't hold up the others.
            await Task.WhenAll(_zones.Select(StartZoneAsync));
        }
        finally
        {
            _building = false;
        }
    }

    // Rebuilds the grid tracks and repositions existing zones (no content restart).
    // Supports two layouts: the config-driven grid, and a Wayland-style "1 big + N-1 stacked" column.
    private void RebuildLayout()
    {
        if (_zones.Count == 0) return;

        int rows, cols;
        if (_columnMode)
        {
            cols = 2;
            rows = Math.Max(1, _zones.Count - 1);
        }
        else
        {
            rows = Math.Max(1, _cfg.Rows);
            cols = Math.Max(1, _cfg.Cols);
        }

        ZoneGrid.RowDefinitions.Clear();
        ZoneGrid.ColumnDefinitions.Clear();
        foreach (var s in _splitters) ZoneGrid.Children.Remove(s);
        _splitters.Clear();

        // Interleave content rows (even indices) and splitter rows (odd indices).
        for (int r = 0; r < rows; r++)
        {
            ZoneGrid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star),
                MinHeight = 100
            });
            if (r < rows - 1)
                ZoneGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int c = 0; c < cols; c++)
        {
            ZoneGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 140
            });
            if (c < cols - 1)
                ZoneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        if (_columnMode) PlaceColumnMode(rows);
        else PlaceGridMode(rows, cols);

        BuildSplitViewDividers(rows, cols);
    }

    private void PlaceGridMode(int rows, int cols)
    {
        int auto = 0;
        foreach (var host in _zones.OrderBy(z => z.Index))
        {
            if (host.Index >= _cfg.Zones.Count)
            {
                host.Visibility = Visibility.Collapsed;
                continue;
            }

            var zc = _cfg.Zones[host.Index];
            int row, col;
            if (zc.Row.HasValue && zc.Col.HasValue)
            {
                row = zc.Row.Value;
                col = zc.Col.Value;
            }
            else
            {
                row = auto / cols;
                col = auto % cols;
                auto++;
            }

            if (row >= rows || col >= cols)
            {
                Log.Error($"Zone {host.Index + 1} does not fit the {rows}x{cols} grid; skipped.");
                host.Visibility = Visibility.Collapsed;
                continue;
            }

            int rSpan = Math.Clamp(zc.RowSpan, 1, rows - row);
            int cSpan = Math.Clamp(zc.ColSpan, 1, cols - col);
            host.Visibility = Visibility.Visible;
            host.SetGridPlacement(row * 2, col * 2, rSpan * 2 - 1, cSpan * 2 - 1);
        }
    }

    private void PlaceColumnMode(int rows)
    {
        foreach (var z in _zones) z.Visibility = Visibility.Visible;

        var big = _zones.FirstOrDefault(z => z.Index == _active) ?? _zones[0];
        big.SetGridPlacement(0, 0, rows * 2 - 1, 1);

        var others = _zones.Where(z => z != big).OrderBy(z => z.Index).ToList();
        for (int i = 0; i < others.Count && i < rows; i++)
            others[i].SetGridPlacement(i * 2, 2, 1, 1);
    }

    private void OnColumnFocusRequested(ZoneHost host)
    {
        if (_maximized) ToggleMaximize();
        SetActive(host.Index, focus: false);
        _columnMode = true;
        RebuildLayout();
        SetActive(_active, focus: true);
    }

    private void ToggleColumnFocus()
    {
        if (_zones.Count < 2) return;
        if (_maximized) ToggleMaximize();
        _columnMode = !_columnMode;
        RebuildLayout();
        SetActive(_active, focus: true);
    }

    // ---------------- Presentation (photo-album / attract mode) ----------------

    private void StartPresentationWatcher()
    {
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleTimer.Tick += (_, _) =>
        {
            if (_presentationActive || !_cfg.Presentation.Enabled || _cfg.Presentation.IdleSeconds <= 0) return;
            if ((DateTime.UtcNow - _lastInput).TotalSeconds >= _cfg.Presentation.IdleSeconds)
                StartPresentation();
        };
        _idleTimer.Start();
    }

    private void TogglePresentation()
    {
        if (!_cfg.Presentation.Enabled || _zones.Count == 0) return;
        if (_presentationActive) StopPresentation();
        else StartPresentation();
    }

    private void StartPresentation()
    {
        if (_presentationActive || _zones.Count == 0) return;
        _presentationActive = true;
        _presentationIdx = -1;

        int secs = Math.Max(3, _cfg.Presentation.IntervalSeconds);
        _presentationTimer ??= new DispatcherTimer();
        _presentationTimer.Interval = TimeSpan.FromSeconds(secs);
        _presentationTimer.Tick -= OnPresentationTick;
        _presentationTimer.Tick += OnPresentationTick;
        _presentationTimer.Start();

        AdvancePresentation();
        Log.Info($"Presentation mode started ({_cfg.Presentation.Mode}, {secs}s interval).");
    }

    private void StopPresentation()
    {
        if (!_presentationActive) return;
        _presentationActive = false;
        _presentationTimer?.Stop();

        if (_maximized) ToggleMaximize();
        if (_columnMode) { _columnMode = false; RebuildLayout(); }
        ApplyLayoutPreset("equal");

        Log.Info("Presentation mode stopped.");
    }

    private void OnPresentationTick(object? sender, EventArgs e) => AdvancePresentation();

    private void AdvancePresentation()
    {
        if (_zones.Count == 0) return;

        if (_cfg.Presentation.Order.Trim().Equals("random", StringComparison.OrdinalIgnoreCase))
            _presentationIdx = Random.Shared.Next(_zones.Count);
        else
            _presentationIdx = (_presentationIdx + 1) % _zones.Count;

        ApplyPresentationEmphasis(_zones[_presentationIdx]);
    }

    private void ApplyPresentationEmphasis(ZoneHost zone)
    {
        switch (_cfg.Presentation.Mode.Trim().ToLowerInvariant())
        {
            case "column":
                ResetToGrid();
                SetActive(zone.Index, focus: false);
                _columnMode = true;
                RebuildLayout();
                SetActive(zone.Index, focus: true);
                break;

            case "revolve":
                ResetToGrid();
                SetActive(zone.Index, focus: false);
                ToggleMaximize();
                break;

            case "spotlight":
            default:
                ApplyLayoutPreset("focus", zone);
                break;
        }
    }

    private void ResetToGrid()
    {
        if (_maximized) ToggleMaximize();
        if (_columnMode) { _columnMode = false; RebuildLayout(); }
    }

    private void OnUserInput()
    {
        _lastInput = DateTime.UtcNow;
        if (_presentationActive && _cfg.Presentation.PauseOnInteraction)
            StopPresentation();
    }

    private void BuildSplitViewDividers(int rows, int cols)
    {
        int totalGridRows = ZoneGrid.RowDefinitions.Count;
        int totalGridCols = ZoneGrid.ColumnDefinitions.Count;

        var vStyle = TryFindResource("MacVerticalSplitterStyle") as Style;
        var hStyle = TryFindResource("MacHorizontalSplitterStyle") as Style;

        // Vertical Splitters (odd column indices)
        for (int c = 0; c < cols - 1; c++)
        {
            int splitCol = c * 2 + 1;
            int leftCol = c * 2;
            int rightCol = (c + 1) * 2;

            var vSplitter = new Thumb
            {
                Style = vStyle,
                ToolTip = "Drag to resize columns (snaps to 25% / 33% / 50% / 67% / 75%) • Double-click for 50/50"
            };
            Grid.SetRow(vSplitter, 0);
            Grid.SetColumn(vSplitter, splitCol);
            Grid.SetRowSpan(vSplitter, totalGridRows);
            Panel.SetZIndex(vSplitter, 5);

            vSplitter.DragDelta += (_, e) => ResizeColumnPairByDelta(leftCol, rightCol, e.HorizontalChange);
            vSplitter.MouseDoubleClick += (_, e) =>
            {
                e.Handled = true;
                ResetPairColumns(leftCol, rightCol);
            };

            ZoneGrid.Children.Add(vSplitter);
            _splitters.Add(vSplitter);
        }

        // Horizontal Splitters (odd row indices)
        for (int r = 0; r < rows - 1; r++)
        {
            int splitRow = r * 2 + 1;
            int topRow = r * 2;
            int bottomRow = (r + 1) * 2;

            var hSplitter = new Thumb
            {
                Style = hStyle,
                ToolTip = "Drag to resize rows (snaps to 25% / 33% / 50% / 67% / 75%) • Double-click for 50/50"
            };
            Grid.SetRow(hSplitter, splitRow);
            Grid.SetColumn(hSplitter, 0);
            Grid.SetColumnSpan(hSplitter, totalGridCols);
            Panel.SetZIndex(hSplitter, 5);

            hSplitter.DragDelta += (_, e) => ResizeRowPairByDelta(topRow, bottomRow, e.VerticalChange);
            hSplitter.MouseDoubleClick += (_, e) =>
            {
                e.Handled = true;
                ResetPairRows(topRow, bottomRow);
            };

            ZoneGrid.Children.Add(hSplitter);
            _splitters.Add(hSplitter);
        }

        // 4-Way Center Intersection Handles (simultaneous X + Y quadrant resize)
        for (int r = 0; r < rows - 1; r++)
        {
            for (int c = 0; c < cols - 1; c++)
            {
                int splitRow = r * 2 + 1;
                int splitCol = c * 2 + 1;
                int topRow = r * 2;
                int bottomRow = (r + 1) * 2;
                int leftCol = c * 2;
                int rightCol = (c + 1) * 2;

                var centerThumb = CreateIntersectionThumb(leftCol, rightCol, topRow, bottomRow);
                Grid.SetRow(centerThumb, splitRow);
                Grid.SetColumn(centerThumb, splitCol);
                Panel.SetZIndex(centerThumb, 15);

                ZoneGrid.Children.Add(centerThumb);
                _splitters.Add(centerThumb);
            }
        }
    }

    private Thumb CreateIntersectionThumb(int leftCol, int rightCol, int topRow, int bottomRow)
    {
        var thumb = new Thumb
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(-6),
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag to resize all 4 quadrants simultaneously • Double-click to reset 2×2 equal grid",
            Template = BuildIntersectionThumbTemplate()
        };

        thumb.DragDelta += (_, e) =>
        {
            ResizeColumnPairByDelta(leftCol, rightCol, e.HorizontalChange);
            ResizeRowPairByDelta(topRow, bottomRow, e.VerticalChange);
        };

        thumb.MouseDoubleClick += (_, e) =>
        {
            e.Handled = true;
            ResetPairColumns(leftCol, rightCol);
            ResetPairRows(topRow, bottomRow);
        };

        return thumb;
    }

    private static ControlTemplate BuildIntersectionThumbTemplate()
    {
        var outerBg = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0x2C, 0x2C, 0x2E)));
        var outerBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x63, 0x63, 0x66)));
        var innerBg = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
        var accentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));

        var template = new ControlTemplate(typeof(Thumb));
        var outer = new FrameworkElementFactory(typeof(Border));
        outer.Name = "Outer";
        outer.SetValue(Border.WidthProperty, 20.0);
        outer.SetValue(Border.HeightProperty, 20.0);
        outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        outer.SetValue(Border.BackgroundProperty, outerBg);
        outer.SetValue(Border.BorderBrushProperty, outerBorder);
        outer.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));

        var inner = new FrameworkElementFactory(typeof(Border));
        inner.Name = "Inner";
        inner.SetValue(Border.WidthProperty, 6.0);
        inner.SetValue(Border.HeightProperty, 6.0);
        inner.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        inner.SetValue(Border.BackgroundProperty, innerBg);
        inner.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        inner.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

        outer.AppendChild(inner);
        template.VisualTree = outer;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, accentBrush, "Outer"));
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, accentBrush, "Inner"));
        template.Triggers.Add(hoverTrigger);

        var dragTrigger = new Trigger { Property = Thumb.IsDraggingProperty, Value = true };
        dragTrigger.Setters.Add(new Setter(Border.BackgroundProperty, accentBrush, "Outer"));
        dragTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.White, "Outer"));
        template.Triggers.Add(dragTrigger);

        return template;
    }

    private void ResizeColumnPairByDelta(int leftCol, int rightCol, double dx)
    {
        if (leftCol < 0 || rightCol >= ZoneGrid.ColumnDefinitions.Count) return;
        var colA = ZoneGrid.ColumnDefinitions[leftCol];
        var colB = ZoneGrid.ColumnDefinitions[rightCol];
        double total = colA.ActualWidth + colB.ActualWidth;
        if (total <= 20) return;

        double starSum = (colA.Width.IsStar ? colA.Width.Value : 1.0) + (colB.Width.IsStar ? colB.Width.Value : 1.0);
        if (starSum <= 0.001) starSum = 2.0;

        double minW = Math.Max(colA.MinWidth, 120);
        double newA = Math.Clamp(colA.ActualWidth + dx, minW, Math.Max(minW, total - minW));
        double ratio = SnapRatioIfClose(newA / total, total);

        colA.Width = new GridLength(ratio * starSum, GridUnitType.Star);
        colB.Width = new GridLength((1.0 - ratio) * starSum, GridUnitType.Star);
    }

    private void ResizeRowPairByDelta(int topRow, int bottomRow, double dy)
    {
        if (topRow < 0 || bottomRow >= ZoneGrid.RowDefinitions.Count) return;
        var rowA = ZoneGrid.RowDefinitions[topRow];
        var rowB = ZoneGrid.RowDefinitions[bottomRow];
        double total = rowA.ActualHeight + rowB.ActualHeight;
        if (total <= 20) return;

        double starSum = (rowA.Height.IsStar ? rowA.Height.Value : 1.0) + (rowB.Height.IsStar ? rowB.Height.Value : 1.0);
        if (starSum <= 0.001) starSum = 2.0;

        double minH = Math.Max(rowA.MinHeight, 90);
        double newA = Math.Clamp(rowA.ActualHeight + dy, minH, Math.Max(minH, total - minH));
        double ratio = SnapRatioIfClose(newA / total, total);

        rowA.Height = new GridLength(ratio * starSum, GridUnitType.Star);
        rowB.Height = new GridLength((1.0 - ratio) * starSum, GridUnitType.Star);
    }

    private static double SnapRatioIfClose(double ratio, double totalPixels)
    {
        foreach (double target in SnapRatios)
        {
            if (Math.Abs(ratio - target) * totalPixels <= SnapThresholdPixels)
                return target;
        }
        return ratio;
    }

    private void ResetPairColumns(int leftCol, int rightCol)
    {
        if (leftCol < 0 || rightCol >= ZoneGrid.ColumnDefinitions.Count) return;
        var colA = ZoneGrid.ColumnDefinitions[leftCol];
        var colB = ZoneGrid.ColumnDefinitions[rightCol];
        double halfSum = ((colA.Width.IsStar ? colA.Width.Value : 1.0) + (colB.Width.IsStar ? colB.Width.Value : 1.0)) * 0.5;
        if (halfSum <= 0.001) halfSum = 1.0;
        colA.Width = new GridLength(halfSum, GridUnitType.Star);
        colB.Width = new GridLength(halfSum, GridUnitType.Star);
    }

    private void ResetPairRows(int topRow, int bottomRow)
    {
        if (topRow < 0 || bottomRow >= ZoneGrid.RowDefinitions.Count) return;
        var rowA = ZoneGrid.RowDefinitions[topRow];
        var rowB = ZoneGrid.RowDefinitions[bottomRow];
        double halfSum = ((rowA.Height.IsStar ? rowA.Height.Value : 1.0) + (rowB.Height.IsStar ? rowB.Height.Value : 1.0)) * 0.5;
        if (halfSum <= 0.001) halfSum = 1.0;
        rowA.Height = new GridLength(halfSum, GridUnitType.Star);
        rowB.Height = new GridLength(halfSum, GridUnitType.Star);
    }

    private void ApplyLayoutPreset(string preset, ZoneHost? targetZone = null)
    {
        if (_maximized) ToggleMaximize();
        if (_columnMode) { _columnMode = false; RebuildLayout(); }

        targetZone ??= _zones.FirstOrDefault(z => z.Index == _active) ?? _zones.FirstOrDefault();

        switch (preset)
        {
            case "equal":
                for (int r = 0; r < ZoneGrid.RowDefinitions.Count; r += 2)
                    ZoneGrid.RowDefinitions[r].Height = new GridLength(1, GridUnitType.Star);
                for (int c = 0; c < ZoneGrid.ColumnDefinitions.Count; c += 2)
                    ZoneGrid.ColumnDefinitions[c].Width = new GridLength(1, GridUnitType.Star);
                break;

            case "focus" when targetZone != null:
                for (int r = 0; r < ZoneGrid.RowDefinitions.Count; r += 2)
                {
                    bool isTargetRow = r >= targetZone.GridRow && r < targetZone.GridRow + targetZone.GridRowSpan;
                    ZoneGrid.RowDefinitions[r].Height = new GridLength(isTargetRow ? 0.70 : 0.30, GridUnitType.Star);
                }
                for (int c = 0; c < ZoneGrid.ColumnDefinitions.Count; c += 2)
                {
                    bool isTargetCol = c >= targetZone.GridCol && c < targetZone.GridCol + targetZone.GridColSpan;
                    ZoneGrid.ColumnDefinitions[c].Width = new GridLength(isTargetCol ? 0.70 : 0.30, GridUnitType.Star);
                }
                SetActive(targetZone.Index, focus: true);
                break;

            case "left-wide":
                for (int r = 0; r < ZoneGrid.RowDefinitions.Count; r += 2)
                    ZoneGrid.RowDefinitions[r].Height = new GridLength(1, GridUnitType.Star);
                for (int c = 0; c < ZoneGrid.ColumnDefinitions.Count; c += 2)
                    ZoneGrid.ColumnDefinitions[c].Width = new GridLength(c == 0 ? 0.65 : 0.35, GridUnitType.Star);
                break;
        }
    }

    private void OnZoneTilePresetRequested(ZoneHost host, string preset) => ApplyLayoutPreset(preset, host);

    // ---------------- Drag Header to Swap Quadrants ----------------

    private void ClearSwapHighlights()
    {
        foreach (var z in _zones) z.SetSwapHighlight(false);
    }

    private void OnZoneHeaderDragMoved(ZoneHost source, Point screenPt)
    {
        var over = ZoneAt((int)screenPt.X, (int)screenPt.Y);
        foreach (var z in _zones)
            z.SetSwapHighlight(z != source && z == over);
    }

    private void OnZoneHeaderDragDropped(ZoneHost source, Point screenPt)
    {
        ClearSwapHighlights();
        if (_maximized) return;

        var target = ZoneAt((int)screenPt.X, (int)screenPt.Y);
        if (target == null || target == source) return;
        SwapZones(source, target);
    }

    private void SwapZones(ZoneHost source, ZoneHost target)
    {
        int sRow = source.GridRow, sCol = source.GridCol, sRSpan = source.GridRowSpan, sCSpan = source.GridColSpan;
        int tRow = target.GridRow, tCol = target.GridCol, tRSpan = target.GridRowSpan, tCSpan = target.GridColSpan;

        source.SetGridPlacement(tRow, tCol, tRSpan, tCSpan);
        target.SetGridPlacement(sRow, sCol, sRSpan, sCSpan);

        (source.Index, target.Index) = (target.Index, source.Index);
        source.RefreshHeaderText();
        target.RefreshHeaderText();
        SetActive(source.Index, focus: true);
    }

    // Body-level drag-to-swap: double-click any tile to pick it up, drag to another tile, release to swap.
    private void OnSwapDragStart(int x, int y)
    {
        OnUserInput();
        var z = ZoneAt(x, y);
        if (z == null) return;
        _swapSource = z;
        z.StopInertia();
        z.Opacity = 0.55;
    }

    private void OnSwapDragMove(int x, int y)
    {
        if (_swapSource == null) return;
        var over = ZoneAt(x, y);
        foreach (var z in _zones)
            z.SetSwapHighlight(z != _swapSource && z == over);
    }

    private void OnSwapDragEnd(int x, int y)
    {
        var source = _swapSource;
        _swapSource = null;
        if (source == null) return;

        source.Opacity = 1.0;
        ClearSwapHighlights();

        if (_maximized) return;
        var target = ZoneAt(x, y);
        if (target == null || target == source) return;
        SwapZones(source, target);
    }

    // ---------------- Zone Lifecycle ----------------

    private async Task StartZoneAsync(ZoneHost host)
    {
        try
        {
            ResolveMissingSource(host.Config);
            var content = ZoneFactory.Create(host.Config);
            host.SetContent(content);
            await content.StartAsync();
            Log.Info($"Zone {host.Index + 1} started ({host.Config.Type}: {host.Config.Source})");
        }
        catch (Exception ex)
        {
            Log.Error($"Zone {host.Index + 1} failed", ex);
            host.ShowError($"{host.Config.Source}\n\n{ex.Message}");
        }
    }

    // If a local video / image / exe source no longer exists at startup, pop a file picker
    // so the operator can point the zone at a replacement without editing zones.json.
    private void ResolveMissingSource(ZoneConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Source)) return;

        // Network URLs / RTSP streams have no local file to browse for.
        if (Uri.TryCreate(cfg.Source, UriKind.Absolute, out var uri) && !uri.IsFile)
            return;

        string src = cfg.Source;
        if (File.Exists(src) || Directory.Exists(src)) return;

        string type = cfg.Type.Trim().ToLowerInvariant();
        var dlg = new OpenFileDialog
        {
            Title = $"Source not found — select a replacement ({type})",
            CheckFileExists = true,
            Multiselect = false,
            Filter = type switch
            {
                "video" => "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.ts;*.webm;*.mpg;*.mpeg|All files|*.*",
                "image" => "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*",
                "exe" => "Programs|*.exe;*.bat;*.cmd|All files|*.*",
                _ => "All files|*.*"
            }
        };

        try
        {
            string? dir = Path.GetDirectoryName(src);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            string? name = Path.GetFileName(src);
            if (!string.IsNullOrEmpty(name)) dlg.FileName = name;
        }
        catch
        {
            // Fall through with defaults.
        }

        bool? result = null;
        try { result = dlg.ShowDialog(this); }
        catch { /* window not ready yet */ }

        if (result == true && File.Exists(dlg.FileName))
        {
            Log.Info($"Zone source '{cfg.Source}' missing; using '{dlg.FileName}' instead.");
            cfg.Source = dlg.FileName;
        }
    }

    private void DisposeZones()
    {
        _presentationActive = false;
        _presentationTimer?.Stop();
        _presentationIdx = -1;

        foreach (var z in _zones)
        {
            try { z.DisposeContent(); }
            catch (Exception ex) { Log.Error($"Zone {z.Index + 1} dispose failed", ex); }
        }
        _zones.Clear();
        _splitters.Clear();
        ZoneGrid.Children.Clear();
        _active = -1;
        _maximized = false;
    }

    private async Task ReloadAsync()
    {
        if (_building) return;
        Log.Info("Reloading config.");
        DisposeZones();
        await BuildAsync();
    }

    // Show the Netflix-style setup wizard; returns false if the operator cancelled.
    private bool RunSetup(ShellConfig cfg)
    {
        int count = cfg.Zones.Count > 0 ? cfg.Zones.Count : Math.Clamp(cfg.Rows * cfg.Cols, 1, 9);
        var setup = new SetupWindow(count) { Owner = this };
        if (setup.ShowDialog() == true && setup.SelectedZones is { Count: > 0 })
        {
            cfg.Zones = setup.SelectedZones;
            cfg.Setup = false;
            return true;
        }
        return false;
    }

    private async Task ReconfigureAsync()
    {
        if (_building) return;
        _presentationActive = false;
        _presentationTimer?.Stop();

        int count = _cfg.Zones.Count > 0 ? _cfg.Zones.Count : Math.Clamp(_cfg.Rows * _cfg.Cols, 1, 9);
        var setup = new SetupWindow(count) { Owner = this };
        if (setup.ShowDialog() == true && setup.SelectedZones is { Count: > 0 })
        {
            _cfg.Zones = setup.SelectedZones;
            _cfg.Setup = false;
            ShellConfig.Save(App.ConfigPath, _cfg);
            DisposeZones();
            await BuildAsync();
        }
    }

    // ---------------- Active zone / focus & Global Pan/Zoom ----------------

    private void SetActive(int index, bool focus)
    {
        var target = _zones.FirstOrDefault(z => z.Index == index);
        if (target == null) return;

        _active = index;
        foreach (var z in _zones) z.SetActive(z.Index == index);

        if (focus)
        {
            Activate();
            target.ZoneContent?.FocusContent();
        }
    }

    private void OnGlobalClick(int x, int y)
    {
        OnUserInput();
        var z = ZoneAt(x, y);
        if (z != null)
        {
            z.StopInertia();
            if (z.Index != _active) SetActive(z.Index, focus: false);
        }
    }

    private void OnGlobalWheel(int x, int y, int delta, bool horizontal)
    {
        OnUserInput();
        var z = ZoneAt(x, y);
        if (z?.ZoneContent == null) return;

        z.StopInertia();

        // Ctrl + Wheel (or Ctrl + Alt + Wheel) zooms any zone (macOS focal-point zoom)
        if (NativeMethods.IsCtrlDown())
        {
            var (rx, ry) = z.GetBodyFocalRatio(x, y);
            double step = (delta / 120.0) * 0.15;
            z.StepZoom(step, rx, ry);
            return;
        }

        bool shift = NativeMethods.IsShiftDown();

        if (z.ZoneContent is ExeZone exe)
        {
            exe.OnMouseWheel(delta, horizontal || shift);
        }
        else if (z.ZoneContent.ZoomFactor > 1.005)
        {
            // When an Image or Video zone is zoomed in, wheel scrolls smoothly across the zoomed surface
            double panDelta = (delta / 120.0) * 48.0;
            if (horizontal || shift) z.ZoneContent.PanBy(-panDelta, 0);
            else z.ZoneContent.PanBy(0, panDelta);
        }
    }

    private void OnGlobalPanDelta(int originX, int originY, int dx, int dy)
    {
        OnUserInput();
        var z = ZoneAt(originX, originY);
        if (z?.ZoneContent == null) return;
        z.StopInertia();
        z.ZoneContent.PanBy(dx, dy);
    }

    private void OnGlobalPanEnded(int originX, int originY, double vx, double vy)
    {
        var z = ZoneAt(originX, originY);
        z?.StartInertia(vx, vy);
    }

    private void OnGlobalSmartZoom(int x, int y)
    {
        var z = ZoneAt(x, y);
        if (z == null) return;
        var (rx, ry) = z.GetBodyFocalRatio(x, y);
        z.ToggleSmartZoom(rx, ry);
    }

    private void OnGlobalCursorMoved(int x, int y)
    {
        OnUserInput();
        if (!IsVisible || WindowState == WindowState.Minimized || RootContainer.ActualWidth <= 0) return;
        if (PresentationSource.FromVisual(RootContainer) == null) return;

        Point tl, br;
        try
        {
            tl = RootContainer.PointToScreen(new Point(0, 0));
            br = RootContainer.PointToScreen(new Point(RootContainer.ActualWidth, RootContainer.ActualHeight));
        }
        catch
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        bool nearBottomCenter = x >= tl.X + (br.X - tl.X) * 0.20 &&
                                x <= tl.X + (br.X - tl.X) * 0.80 &&
                                y >= br.Y - 22 * dpiY && y <= br.Y + 6 * dpiY;

        if (nearBottomCenter)
        {
            ShowMacDock();
        }
        else if (MacDockPopup.IsOpen && !MacDockBorder.IsMouseOver && y < br.Y - 105 * dpiY)
        {
            if (_dockHideTimer is { IsEnabled: false })
                _dockHideTimer.Start();
        }
    }

    private ZoneHost? ZoneAt(int x, int y)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return null;
        foreach (var z in _zones)
        {
            if (!z.IsVisible || z.ActualWidth <= 0) continue;
            if (PresentationSource.FromVisual(z) == null) continue;
            try
            {
                var tl = z.PointToScreen(new Point(0, 0));
                var br = z.PointToScreen(new Point(z.ActualWidth, z.ActualHeight));
                if (x >= tl.X && x < br.X && y >= tl.Y && y < br.Y) return z;
            }
            catch
            {
                // Ignore transient visual states
            }
        }
        return null;
    }

    private void OnZoneMaximizeRequested(ZoneHost host)
    {
        if (!_maximized) SetActive(host.Index, focus: false);
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        if (_active < 0) return;
        _maximized = !_maximized;

        foreach (var s in _splitters)
            s.Visibility = _maximized ? Visibility.Collapsed : Visibility.Visible;

        foreach (var z in _zones)
        {
            z.SetMaximized(_maximized && z.Index == _active);
            if (!_maximized)
            {
                z.ApplyPlacement();
                z.Visibility = Visibility.Visible;
            }
            else if (z.Index == _active)
            {
                Grid.SetRow(z, 0);
                Grid.SetColumn(z, 0);
                Grid.SetRowSpan(z, Math.Max(1, ZoneGrid.RowDefinitions.Count));
                Grid.SetColumnSpan(z, Math.Max(1, ZoneGrid.ColumnDefinitions.Count));
                z.Visibility = Visibility.Visible;
            }
            else
            {
                z.Visibility = Visibility.Collapsed;
            }
        }

        SetActive(_active, focus: true);
    }

    private void ToggleZoneHeaders()
    {
        _headersVisible = !_headersVisible;
        foreach (var z in _zones)
            z.SetHeaderVisible(_headersVisible);
    }

    private void ResetAllViews()
    {
        foreach (var z in _zones)
        {
            z.StopInertia();
            z.ZoneContent?.ResetView();
        }
    }

    // ---------------- macOS Floating Dock ----------------

    private void BuildMacDockUi()
    {
        MacDockStrip.Children.Clear();
        var pillBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0x34, 0x38)));
        var hoverBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
        var textBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
        var sepBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A)));

        AddDockButton("2×2 Equal", "Reset all quadrants to equal 50/50 split", () => ApplyLayoutPreset("equal"));
        AddDockButton("Column Focus", "Wayland-style: active quadrant full height, others stacked right (Ctrl+Alt+L)", ToggleColumnFocus);
        AddDockButton("Focus Active", "Expand active quadrant to 70/30 split", () => ApplyLayoutPreset("focus"));
        AddDockButton("65 / 35 Split", "Wide left quadrants, compact right quadrants", () => ApplyLayoutPreset("left-wide"));
        AddDockSeparator();
        AddDockButton("Solo Audio", "Play audio from active quadrant only (Ctrl+Alt+A)", RouteAudioToActive);
        AddDockButton("Mute All", "Mute all quadrants (Ctrl+Alt+0)", MuteAll);
        AddDockSeparator();
        AddDockButton("Clean Wall", "Show/Hide macOS window headers (Ctrl+Alt+H)", ToggleZoneHeaders);
        AddDockButton("Full Screen", "Toggle borderless fullscreen (Ctrl+Alt+F)", () => SetFullscreen(!_fullscreen));
        AddDockButton("Presentation", "Auto-rotate focus across zones like a photo album (Ctrl+Alt+P)", TogglePresentation);
        AddDockButton("Setup", "Reconfigure what plays in each zone (Ctrl+Alt+S)", () => _ = ReconfigureAsync());

        void AddDockButton(string label, string tooltip, Action onClick)
        {
            var txt = new TextBlock
            {
                Text = label,
                Foreground = textBrush,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var btn = new Border
            {
                Background = pillBrush,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Child = txt
            };

            btn.MouseEnter += (_, _) => btn.Background = hoverBrush;
            btn.MouseLeave += (_, _) => btn.Background = pillBrush;
            btn.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };

            MacDockStrip.Children.Add(btn);
        }

        void AddDockSeparator()
        {
            MacDockStrip.Children.Add(new Border
            {
                Width = 1,
                Height = 20,
                Background = sepBrush,
                Margin = new Thickness(6, 0, 6, 0)
            });
        }
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private void ShowMacDock()
    {
        _dockHideTimer?.Stop();
        if (!MacDockPopup.IsOpen)
        {
            MacDockPopup.IsOpen = true;
            Dispatcher.InvokeAsync(RepositionMacDock, DispatcherPriority.Loaded);
        }
        _dockHideTimer?.Start();
    }

    private void HideMacDock()
    {
        _dockHideTimer?.Stop();
        MacDockPopup.IsOpen = false;
    }

    private void RepositionMacDock()
    {
        if (!MacDockPopup.IsOpen) return;
        double dockWidth = MacDockBorder.ActualWidth > 0 ? MacDockBorder.ActualWidth + 16 : 860;
        MacDockPopup.HorizontalOffset = Math.Max(0, (RootContainer.ActualWidth - dockWidth) * 0.5);
    }

    private ZoneHost? ActiveZone() => _zones.FirstOrDefault(z => z.Index == _active);

    // Move the shell window onto the requested monitor (0 = primary, 1 = secondary, …).
    private void ApplyDisplay(int displayIndex)
    {
        var monitors = NativeMethods.GetMonitorBounds();
        if (monitors.Length == 0) return;

        int idx = Math.Clamp(displayIndex, 0, monitors.Length - 1);
        var m = monitors[idx];

        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        WindowState = WindowState.Normal;
        Left = m.Left / sx;
        Top = m.Top / sy;
        Width = (m.Right - m.Left) / sx;
        Height = (m.Bottom - m.Top) / sy;

        Log.Info($"Display {idx + 1}/{monitors.Length} selected: {m.Right - m.Left}x{m.Bottom - m.Top} @ ({m.Left},{m.Top})");
    }

    // Borderless + maximized covers the whole monitor, taskbar included.
    // WindowState goes through Normal around the style change, otherwise Windows keeps the
    // previous maximized bounds (framed/work-area size) instead of recomputing them.
    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;

        if (on)
        {
            _preFullscreenState = WindowState == WindowState.Minimized ? WindowState.Maximized : WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _preFullscreenState;
        }

        Activate();
    }

    private void RouteAudioToActive()
    {
        foreach (var z in _zones) z.ZoneContent?.SetMuted(z.Index != _active);
    }

    private void MuteAll()
    {
        foreach (var z in _zones) z.ZoneContent?.SetMuted(true);
    }

    // ---------------- Hotkeys ----------------
    // Global hotkeys are used because keyboard input goes to the focused child
    // HWND, so WPF KeyDown never fires while a zone has focus.

    private void RegisterHotkeys()
    {
        const uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;

        for (int i = 0; i < 9; i++) Register(HkZoneBase + i, mods, (uint)('1' + i));
        Register(HkMaximize, mods, 'M');
        Register(HkAudio, mods, 'A');
        Register(HkMuteAll, mods, '0');
        Register(HkReload, mods, 'R');
        Register(HkQuit, mods, 'Q');
        Register(HkFullscreen, mods, 'F');
        Register(HkHeaders, mods, 'H');
        Register(HkDock, mods, 'D');
        Register(HkZoomIn, mods, NativeMethods.VK_OEM_PLUS);
        Register(HkZoomOut, mods, NativeMethods.VK_OEM_MINUS);
        Register(HkZoomReset, mods, NativeMethods.VK_HOME);
        Register(HkColumn, mods, 'L');
        Register(HkPresentation, mods, 'P');
        Register(HkSetup, mods, 'S');

        void Register(int id, uint m, uint vk)
        {
            if (!NativeMethods.RegisterHotKey(_hwnd, id, m, vk))
                Log.Error($"Hotkey registration failed (id {id}, vk {vk}). Already in use?");
        }
    }

    private void UnregisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        for (int i = 0; i < 9; i++) NativeMethods.UnregisterHotKey(_hwnd, HkZoneBase + i);
        foreach (var id in new[] { HkMaximize, HkAudio, HkMuteAll, HkReload, HkQuit, HkFullscreen, HkHeaders, HkDock, HkZoomIn, HkZoomOut, HkZoomReset, HkColumn, HkPresentation, HkSetup })
            NativeMethods.UnregisterHotKey(_hwnd, id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SysMenuFullscreen)
        {
            handled = true;
            SetFullscreen(!_fullscreen);
            return IntPtr.Zero;
        }

        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;

        int id = wParam.ToInt32();
        handled = true;

        if (id >= HkZoneBase && id < HkZoneBase + 9)
        {
            int index = id - HkZoneBase;
            if (_zones.Any(z => z.Index == index))
            {
                if (_maximized && index != _active) ToggleMaximize(); // restore grid first
                SetActive(index, focus: true);
            }
        }
        else switch (id)
        {
            case HkMaximize: ToggleMaximize(); break;
            case HkAudio: RouteAudioToActive(); break;
            case HkMuteAll: MuteAll(); break;
            case HkReload: _ = ReloadAsync(); break;
            case HkQuit: Close(); break;
            case HkFullscreen: SetFullscreen(!_fullscreen); break;
            case HkHeaders: ToggleZoneHeaders(); break;
            case HkDock:
                if (MacDockPopup.IsOpen) HideMacDock();
                else ShowMacDock();
                break;
            case HkZoomIn: ActiveZone()?.StepZoom(+0.25); break;
            case HkZoomOut: ActiveZone()?.StepZoom(-0.25); break;
            case HkZoomReset: ActiveZone()?.ZoneContent?.ResetView(); break;
            case HkColumn: ToggleColumnFocus(); break;
            case HkPresentation: TogglePresentation(); break;
            case HkSetup: _ = ReconfigureAsync(); break;
            default: handled = false; break;
        }

        return IntPtr.Zero;
    }
}
