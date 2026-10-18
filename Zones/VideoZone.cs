using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LibVLCSharp.Shared;
using MultiZoneShell.Config;
using MultiZoneShell.Infrastructure;
using MultiZoneShell.Interop;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MultiZoneShell.Zones;

/// <summary>
/// Local files (looped) or network streams (RTSP/HTTP/UDP, auto-reconnect) via LibVLC,
/// hosted inside a hardware-clipped dual-HWND surface supporting native Pan &amp; Zoom.
/// </summary>
public sealed class VideoZone : IZoneContent
{
    private static LibVLC? _libVlc;
    private static LibVLC Lib => _libVlc ??= new LibVLC("--no-video-title-show", "--mouse-hide-timeout=0");

    private readonly ZoneConfig _cfg;
    private readonly Grid _root = new() { Background = Brushes.Black };
    private readonly VideoSurfaceHost _surface = new();
    private readonly object _playerLock = new();
    private readonly bool _isStream;
    private VlcMediaPlayer? _player;
    private bool _muted;
    private volatile bool _disposed;

    public event Action? ViewStateChanged;

    public VideoZone(ZoneConfig cfg)
    {
        _cfg = cfg;
        _muted = cfg.Muted;
        _isStream = Uri.TryCreate(cfg.Source, UriKind.Absolute, out var uri) && !uri.IsFile;

        if (!_isStream && !File.Exists(cfg.Source))
            throw new FileNotFoundException("Video file not found.", cfg.Source);

        _surface.ZoomChanged += () => ViewStateChanged?.Invoke();
        _root.Children.Add(_surface);
    }

    public FrameworkElement View => _root;
    public bool IsMuted => _muted;
    public double ZoomFactor => _surface.ZoomFactor;

    public Task StartAsync()
    {
        if (_surface.RenderHandle != IntPtr.Zero)
        {
            Start(_surface.RenderHandle);
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        Action? onReady = null;
        onReady = () =>
        {
            _surface.HandleReady -= onReady;
            try
            {
                if (!_disposed && _surface.RenderHandle != IntPtr.Zero)
                    Start(_surface.RenderHandle);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        };
        _surface.HandleReady += onReady;
        return tcs.Task;
    }

    private void Start(IntPtr renderHwnd)
    {
        if (_disposed || renderHwnd == IntPtr.Zero) return;

        lock (_playerLock)
        {
            if (_disposed || _player != null) return;

            _player = new VlcMediaPlayer(Lib)
            {
                Hwnd = renderHwnd,
                EnableHardwareDecoding = true
            };
            _player.EnableMouseInput = false;
            _player.EnableKeyInput = false;

            _player.Playing += (_, _) =>
            {
                lock (_playerLock)
                {
                    if (!_disposed && _player != null) _player.Mute = _muted;
                }
            };
            _player.EndReached += (_, _) => ScheduleRestart();
            _player.EncounteredError += (_, _) =>
            {
                Log.Error($"VLC error on {_cfg.Source}");
                ScheduleRestart();
            };

            PlayLocked();
        }
    }

    private void PlayLocked()
    {
        if (_disposed || _player == null) return;

        using var media = _isStream
            ? new Media(Lib, new Uri(_cfg.Source))
            : new Media(Lib, _cfg.Source, FromType.FromPath);

        if (_isStream)
            media.AddOption($":network-caching={Math.Max(100, _cfg.NetworkCachingMs)}");
        else if (_cfg.Loop)
            media.AddOption(":input-repeat=65535");

        _player.Play(media);
    }

    // Never call back into LibVLC from its own event thread; hop to the thread pool.
    private void ScheduleRestart()
    {
        if (_disposed || (!_isStream && !_cfg.Loop)) return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(_isStream ? 3000 : 200);
            lock (_playerLock)
            {
                if (!_disposed) PlayLocked();
            }
        });
    }

    public void FocusContent() => _root.Focus();

    public void SetMuted(bool muted)
    {
        _muted = muted;
        lock (_playerLock)
        {
            if (!_disposed && _player != null) _player.Mute = muted;
        }
        ViewStateChanged?.Invoke();
    }

    public void SetZoom(double zoom, double focalRatioX = 0.5, double focalRatioY = 0.5) =>
        _surface.SetZoom(zoom, focalRatioX, focalRatioY);

    public void PanBy(double dx, double dy) =>
        _surface.PanBy(dx, dy);

    public void ResetView() =>
        _surface.ResetView();

    public void ReloadContent()
    {
        if (_disposed) return;
        _surface.ResetView();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_playerLock)
            {
                if (_disposed || _player == null) return;
                _player.Stop();
                PlayLocked();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        VlcMediaPlayer? player;
        lock (_playerLock)
        {
            player = _player;
            _player = null;
        }

        if (player != null)
        {
            player.Stop();
            player.Hwnd = IntPtr.Zero;
            player.Dispose();
        }
        _surface.Dispose();
    }

    public static void DisposeShared()
    {
        _libVlc?.Dispose();
        _libVlc = null;
    }

    /// <summary>
    /// Dual-HWND host: outer container clips to the quadrant bounds (WS_CLIPCHILDREN),
    /// inner render surface scales to (Viewport * Zoom) and translates by (-OffsetX, -OffsetY).
    /// </summary>
    private sealed class VideoSurfaceHost : HwndHost
    {
        private IntPtr _container;
        private IntPtr _renderHwnd;
        private double _zoom = 1.0;
        private double _offsetX;
        private double _offsetY;
        private int _vpWidth = 1;
        private int _vpHeight = 1;

        public IntPtr RenderHandle => _renderHwnd;
        public double ZoomFactor => _zoom;
        public event Action? ZoomChanged;
        public event Action? HandleReady;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            int w = Math.Max(1, (int)ActualWidth);
            int h = Math.Max(1, (int)ActualHeight);

            _container = NativeMethods.CreateWindowEx(0, "static", string.Empty,
                unchecked((int)(NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS)),
                0, 0, w, h,
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            _renderHwnd = NativeMethods.CreateWindowEx(0, "static", string.Empty,
                unchecked((int)(NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS)),
                0, 0, w, h,
                _container, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            Dispatcher.InvokeAsync(() => HandleReady?.Invoke());
            return new HandleRef(this, _container);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (_renderHwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_renderHwnd);
                _renderHwnd = IntPtr.Zero;
            }
            if (hwnd.Handle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(hwnd.Handle);
                _container = IntPtr.Zero;
            }
        }

        public void SetZoom(double zoom, double focalRatioX, double focalRatioY)
        {
            double clamped = Math.Clamp(Math.Round(zoom, 2), 1.0, 6.0);
            if (Math.Abs(clamped - _zoom) < 0.005) return;

            double fx = Math.Clamp(focalRatioX, 0.0, 1.0) * _vpWidth;
            double fy = Math.Clamp(focalRatioY, 0.0, 1.0) * _vpHeight;
            double ratio = clamped / _zoom;

            _zoom = clamped;
            _offsetX = (_offsetX + fx) * ratio - fx;
            _offsetY = (_offsetY + fy) * ratio - fy;

            ApplySurfaceTransform();
            ZoomChanged?.Invoke();
        }

        public void PanBy(double dx, double dy)
        {
            if (_zoom <= 1.005) return;
            _offsetX -= dx;
            _offsetY -= dy;
            ApplySurfaceTransform();
        }

        public void ResetView()
        {
            _zoom = 1.0;
            _offsetX = 0;
            _offsetY = 0;
            ApplySurfaceTransform();
            ZoomChanged?.Invoke();
        }

        protected override void OnWindowPositionChanged(Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);
            ApplySurfaceTransform();
        }

        private void ApplySurfaceTransform()
        {
            if (_container == IntPtr.Zero || _renderHwnd == IntPtr.Zero) return;

            NativeMethods.GetClientRect(_container, out var rc);
            _vpWidth = Math.Max(1, rc.Right - rc.Left);
            _vpHeight = Math.Max(1, rc.Bottom - rc.Top);

            int extW = Math.Max(_vpWidth, (int)Math.Round(_vpWidth * _zoom));
            int extH = Math.Max(_vpHeight, (int)Math.Round(_vpHeight * _zoom));

            double maxOffX = Math.Max(0, extW - _vpWidth);
            double maxOffY = Math.Max(0, extH - _vpHeight);
            _offsetX = Math.Clamp(_offsetX, 0, maxOffX);
            _offsetY = Math.Clamp(_offsetY, 0, maxOffY);

            NativeMethods.SetWindowPos(_renderHwnd, IntPtr.Zero,
                -(int)Math.Round(_offsetX), -(int)Math.Round(_offsetY),
                extW, extH,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOCOPYBITS | NativeMethods.SWP_DEFERERASE);
        }
    }
}
