using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MultiZoneShell.Config;
using MultiZoneShell.Infrastructure;

namespace MultiZoneShell.Zones;

public sealed class WebZone : IZoneContent
{
    // One shared browser environment (one set of Edge processes) for all web zones.
    private static Task<CoreWebView2Environment>? _env;

    private readonly ZoneConfig _cfg;
    private readonly WebView2 _web;
    private DispatcherTimer? _refreshTimer;
    private bool _muted;
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private bool _disposed;

    public event Action? ViewStateChanged;

    public WebZone(ZoneConfig cfg)
    {
        _cfg = cfg;
        _muted = cfg.Muted;
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1C, 0x1C, 0x1E) };
        _web.ZoomFactorChanged += (_, _) =>
        {
            if (_disposed) return;
            _zoom = _web.ZoomFactor;
            ViewStateChanged?.Invoke();
        };
    }

    public FrameworkElement View => _web;
    public bool IsMuted => _muted;
    public double ZoomFactor => _zoom;

    public async Task StartAsync()
    {
        _env ??= CoreWebView2Environment.CreateAsync(
            null,
            Path.Combine(AppPaths.Data, "WebView2"),
            new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required --enable-smooth-scrolling"));

        await _web.EnsureCoreWebView2Async(await _env);
        if (_disposed) return;

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPinchZoomEnabled = true;
        core.IsMuted = _muted;

        // Keep popups inside the zone instead of opening new windows.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };

        core.ProcessFailed += (_, e) =>
        {
            Log.Error($"WebView2 process failed ({e.ProcessFailedKind}) for {_cfg.Source}");
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
                _web.Dispatcher.InvokeAsync(() => { if (!_disposed) core.Reload(); });
        };

        core.NavigationCompleted += async (_, e) =>
        {
            if (e.IsSuccess || _disposed || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            Log.Error($"Navigation failed ({e.WebErrorStatus}) for {_cfg.Source}; retrying in 10s.");
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (!_disposed) Navigate();
        };

        Navigate();

        if (_cfg.RefreshSeconds > 0)
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_cfg.RefreshSeconds) };
            _refreshTimer.Tick += (_, _) => { if (!_disposed) core.Reload(); };
            _refreshTimer.Start();
        }
    }

    private void Navigate() => _web.CoreWebView2?.Navigate(ToUri(_cfg.Source).AbsoluteUri);

    private static Uri ToUri(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri : new Uri(Path.GetFullPath(source));

    public void FocusContent() => _web.Focus();

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (_web.CoreWebView2 != null) _web.CoreWebView2.IsMuted = muted;
        ViewStateChanged?.Invoke();
    }

    public void SetZoom(double zoom, double focalRatioX = 0.5, double focalRatioY = 0.5)
    {
        double clamped = Math.Clamp(Math.Round(zoom, 2), 0.25, 5.0);
        if (Math.Abs(clamped - _zoom) < 0.005) return;
        _zoom = clamped;
        if (!_disposed) _web.ZoomFactor = _zoom;
        if (Math.Abs(_zoom - 1.0) < 0.01)
        {
            _panX = 0;
            _panY = 0;
            ApplyWebPan(resetOnly: true);
        }
        ViewStateChanged?.Invoke();
    }

    public void PanBy(double dx, double dy)
    {
        if (_disposed || _web.CoreWebView2 == null) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_web);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        double dipDx = dx / scaleX;
        double dipDy = dy / scaleY;

        _panX += dipDx;
        _panY += dipDy;
        string sdx = (-dipDx).ToString("F1", CultureInfo.InvariantCulture);
        string sdy = (-dipDy).ToString("F1", CultureInfo.InvariantCulture);
        string px = _panX.ToString("F1", CultureInfo.InvariantCulture);
        string py = _panY.ToString("F1", CultureInfo.InvariantCulture);

        // Scroll naturally if the page is scrollable; otherwise translate body for fixed 100vh dashboards.
        _ = _web.CoreWebView2.ExecuteScriptAsync(
            $"(() => {{ const sx = window.scrollX, sy = window.scrollY; window.scrollBy({sdx}, {sdy}); " +
            $"if (Math.abs(window.scrollX - sx) < 0.5 && Math.abs(window.scrollY - sy) < 0.5 && document.body) {{ " +
            $"document.body.style.transform = 'translate3d({px}px, {py}px, 0)'; }} }})();");
    }

    private void ApplyWebPan(bool resetOnly)
    {
        if (_disposed || _web.CoreWebView2 == null) return;
        if (resetOnly)
        {
            _ = _web.CoreWebView2.ExecuteScriptAsync(
                "(() => { window.scrollTo({left:0, top:0, behavior:'smooth'}); if(document.body) document.body.style.transform = ''; })();");
        }
    }

    public void ResetView()
    {
        _panX = 0;
        _panY = 0;
        _zoom = 1.0;
        if (!_disposed) _web.ZoomFactor = 1.0;
        ApplyWebPan(resetOnly: true);
        ViewStateChanged?.Invoke();
    }

    public void ReloadContent()
    {
        if (_disposed) return;
        ResetView();
        _web.CoreWebView2?.Reload();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer?.Stop();
        _web.Dispose();
    }
}
