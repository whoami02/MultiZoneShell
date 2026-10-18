using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MultiZoneShell.Config;
using MultiZoneShell.Infrastructure;
using MultiZoneShell.Interop;

namespace MultiZoneShell.Zones;

/// <summary>
/// Launches any Win32 app and reparents its main window into the zone.
/// A watchdog re-attaches if the app recreates its window and relaunches it if it exits.
/// </summary>
public sealed class ExeZone : IZoneContent
{
    private readonly ZoneConfig _cfg;
    private readonly Grid _root = new() { Background = Brushes.Black };
    private readonly TextBlock _status;
    private readonly Grid _viewport = new() { Visibility = Visibility.Collapsed }; // host + scrollbars
    private readonly WindowEmbedHost _host = new();
    private readonly ScrollBar _hBar = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly ScrollBar _vBar = new() { Orientation = Orientation.Vertical, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(2) };

    private Process? _process;
    private IntPtr _window;
    private IntPtr _blockedWindow; // embedding refused by Windows (elevated app); don't retry every tick
    private DateTime _lastLaunch = DateTime.MinValue;
    private bool _busy;
    private bool _disposed;
    private double _panRemainderX;
    private double _panRemainderY;

    private static readonly Brush SurfaceBrush = CreateFrozenBrush(0x1C, 0x1C, 0x1E);
    private static readonly Brush StatusTextBrush = CreateFrozenBrush(0x98, 0x98, 0x9D);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public event Action? ViewStateChanged;

    public ExeZone(ZoneConfig cfg)
    {
        _cfg = cfg;
        _root.Background = SurfaceBrush;
        _status = new TextBlock
        {
            Foreground = StatusTextBrush,
            FontSize = 15,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Scrollbars live in their own cells: WPF cannot draw on top of the native app window.
        _host.ScrollWhenLarger = cfg.Fit.Trim().Equals("scroll", StringComparison.OrdinalIgnoreCase);
        _viewport.ColumnDefinitions.Add(new ColumnDefinition());
        _viewport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _viewport.RowDefinitions.Add(new RowDefinition());
        _viewport.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(_vBar, 1);
        Grid.SetRow(_hBar, 1);
        _viewport.Children.Add(_host);
        _viewport.Children.Add(_vBar);
        _viewport.Children.Add(_hBar);

        _hBar.Scroll += (_, e) => _host.ScrollTo((int)e.NewValue, _host.OffsetY);
        _vBar.Scroll += (_, e) => _host.ScrollTo(_host.OffsetX, (int)e.NewValue);
        _host.ScrollChanged += () =>
        {
            SyncScrollBars();
            ViewStateChanged?.Invoke();
        };

        _root.Children.Add(_status);
        _root.Children.Add(_viewport);
        _watchdog.Tick += async (_, _) => await WatchdogAsync();
    }

    public FrameworkElement View => _root;
    public bool IsMuted => true;
    public double ZoomFactor => _host.ZoomFactor;

    /// <summary>Mouse wheel over the zone (from the global hook). Shift or tilt-wheel scrolls horizontally.</summary>
    public void OnMouseWheel(int delta, bool horizontal)
    {
        if (_window == IntPtr.Zero) return;
        if (horizontal) _host.ScrollBy(delta, 0); // tilt right = positive = scroll right
        else _host.ScrollBy(0, -delta);           // wheel up = positive = scroll up
    }

    public void SetZoom(double zoom, double focalRatioX = 0.5, double focalRatioY = 0.5) =>
        _host.SetZoom(zoom, focalRatioX, focalRatioY);

    public void PanBy(double dx, double dy)
    {
        if (_window == IntPtr.Zero) return;
        _panRemainderX += dx;
        _panRemainderY += dy;
        int ix = (int)Math.Truncate(_panRemainderX);
        int iy = (int)Math.Truncate(_panRemainderY);
        if (ix != 0 || iy != 0)
        {
            _panRemainderX -= ix;
            _panRemainderY -= iy;
            _host.ScrollBy(-ix, -iy);
        }
    }

    public void ResetView()
    {
        _panRemainderX = _panRemainderY = 0;
        _host.ResetZoomAndPan();
    }

    public void ReloadContent()
    {
        if (_disposed) return;
        ResetView();
        if (_window != IntPtr.Zero && NativeMethods.IsWindow(_window))
        {
            _host.Fit();
        }
        else
        {
            _ = LaunchAndEmbedAsync();
        }
    }

    private void SyncScrollBars()
    {
        Sync(_hBar, _host.ExtentWidth, _host.ViewportWidth, _host.OffsetX);
        Sync(_vBar, _host.ExtentHeight, _host.ViewportHeight, _host.OffsetY);

        static void Sync(ScrollBar bar, int extent, int viewport, int offset)
        {
            int max = Math.Max(0, extent - viewport);
            bar.Visibility = max > 0 ? Visibility.Visible : Visibility.Collapsed;
            bar.Maximum = max;
            bar.ViewportSize = viewport;
            bar.LargeChange = Math.Max(1, viewport * 0.9);
            bar.SmallChange = 40;
            bar.Value = offset;
        }
    }

    public async Task StartAsync()
    {
        // The HwndHost creates its container window once it is in a live visual tree.
        for (int i = 0; i < 100 && _host.Handle == IntPtr.Zero && !_disposed; i++)
            await Task.Delay(50);
        if (_host.Handle == IntPtr.Zero)
            throw new InvalidOperationException("Embed container was not created.");

        await LaunchAndEmbedAsync();
        _watchdog.Start();
    }

    private async Task LaunchAndEmbedAsync()
    {
        _lastLaunch = DateTime.UtcNow;

        // Adopt an instance that is already running (left over from a previous shell, or started by hand)
        // instead of launching a duplicate that would float on the desktop.
        var existing = NativeMethods.FindMainWindow(CandidatePids(), _cfg.WindowTitleContains);
        if (existing != IntPtr.Zero && existing != _blockedWindow)
        {
            Log.Info($"Adopting running instance of {_cfg.Source}");
            Attach(existing);
            return;
        }

        SetStatus($"Starting {Path.GetFileName(_cfg.Source)}…");

        try
        {
            var psi = new ProcessStartInfo(_cfg.Source, _cfg.Args ?? string.Empty) { UseShellExecute = true };
            var dir = Path.GetDirectoryName(_cfg.Source);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;

            // Can be null if the shell hands off to an already-running instance.
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error($"Launch failed: {_cfg.Source}", ex);
            SetStatus($"Launch failed:\n{ex.Message}");
            return;
        }

        var window = await WaitForWindowAsync();
        if (window == IntPtr.Zero)
        {
            SetStatus("Application window not found.\nSet ProcessName / WindowTitleContains in zones.json.");
            Log.Error($"Window not found for {_cfg.Source}");
            return;
        }

        Attach(window);
    }

    private async Task<IntPtr> WaitForWindowAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(3, _cfg.StartupTimeoutSeconds));
        while (DateTime.UtcNow < deadline && !_disposed)
        {
            var h = NativeMethods.FindMainWindow(CandidatePids(), _cfg.WindowTitleContains);
            if (h != IntPtr.Zero)
            {
                await Task.Delay(300); // let the app finish creating its window
                if (NativeMethods.IsWindow(h)) return h;
            }
            await Task.Delay(250);
        }
        return IntPtr.Zero;
    }

    private HashSet<int> CandidatePids()
    {
        var pids = new HashSet<int>();

        try
        {
            if (_process is { HasExited: false }) pids.Add(_process.Id);
        }
        catch { /* access denied etc. */ }

        // Launcher apps (and shell hand-offs) create the window in a different process.
        var name = !string.IsNullOrWhiteSpace(_cfg.ProcessName)
            ? _cfg.ProcessName
            : Path.GetFileNameWithoutExtension(_cfg.Source);

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        foreach (var p in Process.GetProcessesByName(name))
        {
            pids.Add(p.Id);
            p.Dispose();
        }

        return pids;
    }

    private void Attach(IntPtr window)
    {
        try
        {
            _host.Embed(window);
            _window = window;
            _viewport.Visibility = Visibility.Visible;
            _status.Visibility = Visibility.Collapsed;
            Log.Info($"Embedded '{NativeMethods.GetTitle(window)}' from {_cfg.Source}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _blockedWindow = window;
            _window = IntPtr.Zero;
            Log.Error($"Embed blocked for {_cfg.Source}: {ex.Message}");
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"Embed failed for {_cfg.Source}", ex);
            SetStatus($"Embed failed:\n{ex.Message}");
        }
    }

    private async Task WatchdogAsync()
    {
        if (_disposed || _busy) return;

        _busy = true;
        try
        {
            if (_window != IntPtr.Zero && NativeMethods.IsWindow(_window))
            {
                if (!_host.IsHolding(_window))
                {
                    // The app put its window back on the desktop (WPF re-applies its own styles/parent).
                    Log.Info($"Window escaped, re-embedding: {_cfg.Source}");
                    Attach(_window);
                    return;
                }

                if ((NativeMethods.GetStyle(_window, NativeMethods.GWL_STYLE) & NativeMethods.WS_VISIBLE) != 0)
                {
                    _host.Fit();
                    return;
                }

                // Embedded window was hidden, not destroyed (splash/login -> main window pattern):
                // switch to whichever top-level window the app shows now.
                var next = NativeMethods.FindMainWindow(CandidatePids(), _cfg.WindowTitleContains);
                if (next != IntPtr.Zero)
                {
                    Log.Info($"Embedded window hidden, switching to '{NativeMethods.GetTitle(next)}'");
                    Attach(next);
                }
                return;
            }

            if (_window != IntPtr.Zero)
            {
                _host.Detach();
                _window = IntPtr.Zero;
                Log.Info($"Window lost for {_cfg.Source}");
            }

            var pids = CandidatePids();
            if (pids.Count > 0)
            {
                // App still running: it may have recreated its main window.
                var h = NativeMethods.FindMainWindow(pids, _cfg.WindowTitleContains);
                if (h == _blockedWindow && h != IntPtr.Zero) return; // keep showing the elevation message
                if (h != IntPtr.Zero) Attach(h);
                else SetStatus("Waiting for application window…");
                return;
            }

            if (!_cfg.RestartOnExit)
            {
                SetStatus("Application closed.");
                return;
            }

            if (DateTime.UtcNow - _lastLaunch < TimeSpan.FromSeconds(10)) return; // throttle relaunches
            await LaunchAndEmbedAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.Visibility = Visibility.Visible;
        _viewport.Visibility = Visibility.Collapsed;
    }

    public void FocusContent()
    {
        if (_window != IntPtr.Zero && NativeMethods.IsWindow(_window))
            NativeMethods.FocusForeignChild(_window);
    }

    public void SetMuted(bool muted)
    {
        // Per-app muting of external processes needs the Windows audio session API; not in this POC.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdog.Stop();

        var window = _window;
        _window = IntPtr.Zero;
        int windowPid = window != IntPtr.Zero && NativeMethods.IsWindow(window) ? NativeMethods.GetProcessId(window) : 0;

        // Always give the window back to the desktop before the container is destroyed.
        _host.Detach(hide: _cfg.KillOnShellExit);

        if (_cfg.KillOnShellExit)
        {
            Kill(windowPid);
            try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
            catch (Exception ex) { Log.Error($"Kill failed for {_cfg.Source}", ex); }
        }

        _process?.Dispose();
        _host.Dispose();
    }

    private static void Kill(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
    }
}
