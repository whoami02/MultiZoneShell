using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static MultiZoneShell.Interop.NativeMethods;

namespace MultiZoneShell.Interop;

/// <summary>
/// HwndHost that reparents a foreign top-level window (another process) into a WPF layout slot.
/// </summary>
internal sealed class WindowEmbedHost : HwndHost
{
    private IntPtr _container;
    private IntPtr _child;
    private uint _origStyle;
    private uint _origExStyle;

    // Scrolling & Zooming (physical pixels): the child is laid out at extent size and slid by -offset
    // inside the container, which clips it (WS_CLIPCHILDREN).
    private int _naturalWidth, _naturalHeight;
    private int _offsetX, _offsetY;
    private double _zoom = 1.0;

    public IntPtr EmbeddedWindow => _child;

    /// <summary>Keep the app at least at its own size and scroll, instead of squeezing it into the zone.</summary>
    public bool ScrollWhenLarger { get; set; } = true;

    public double ZoomFactor => _zoom;
    public int ExtentWidth { get; private set; }
    public int ExtentHeight { get; private set; }
    public int ViewportWidth { get; private set; }
    public int ViewportHeight { get; private set; }
    public int OffsetX => _offsetX;
    public int OffsetY => _offsetY;

    /// <summary>Extent, viewport, zoom or offset changed.</summary>
    public event Action? ScrollChanged;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _container = CreateWindowEx(0, "static", string.Empty,
            unchecked((int)(WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS)),
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Detach();
        DestroyWindow(hwnd.Handle);
        _container = IntPtr.Zero;
    }

    public void Embed(IntPtr window)
    {
        if (_container == IntPtr.Zero) throw new InvalidOperationException("Container window not created yet.");
        Detach();

        // A maximized/minimized window keeps its own placement logic and fights the child layout.
        if (IsZoomed(window) || IsIconic(window)) ShowWindow(window, SW_RESTORE);

        // The app's own client size (frame is stripped below) is the size it was designed for.
        GetClientRect(window, out var natural);
        _naturalWidth = natural.Right - natural.Left;
        _naturalHeight = natural.Bottom - natural.Top;
        _offsetX = _offsetY = 0;

        _child = window;
        _origStyle = GetStyle(window, GWL_STYLE);
        _origExStyle = GetStyle(window, GWL_EXSTYLE);

        uint style = (_origStyle & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX |
                                     WS_MAXIMIZE | WS_MINIMIZE))
                     | WS_CHILD | WS_VISIBLE;
        uint exStyle = _origExStyle & ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE |
                                        WS_EX_DLGMODALFRAME | WS_EX_STATICEDGE | WS_EX_TOPMOST);

        SetStyle(window, GWL_STYLE, style);
        SetStyle(window, GWL_EXSTYLE, exStyle);
        SetParent(window, _container);
        int error = Marshal.GetLastWin32Error();

        if (GetParent(window) != _container)
        {
            _child = IntPtr.Zero;
            SetStyle(window, GWL_STYLE, _origStyle);
            SetStyle(window, GWL_EXSTYLE, _origExStyle);

            // UIPI: a non-elevated process cannot reparent/restyle windows of an elevated one.
            if (error == ERROR_ACCESS_DENIED)
                throw new UnauthorizedAccessException(
                    "This app runs as administrator, so Windows blocks embedding it.\nRun MultiZoneShell as administrator.");
            throw new Win32Exception(error);
        }

        ResizeChild();
    }

    /// <summary>Return the window to the desktop with its original frame.</summary>
    public void Detach(bool hide = false)
    {
        var child = _child;
        _child = IntPtr.Zero;
        if (child == IntPtr.Zero) return;

        _offsetX = _offsetY = 0;
        ExtentWidth = ExtentHeight = 0;
        ScrollChanged?.Invoke();
        if (!IsWindow(child)) return;

        if (hide) ShowWindow(child, SW_HIDE);
        SetParent(child, IntPtr.Zero);
        SetStyle(child, GWL_STYLE, _origStyle);
        SetStyle(child, GWL_EXSTYLE, _origExStyle);
        SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>True while <paramref name="window"/> is still parented to this host (apps can pull themselves back out).</summary>
    public bool IsHolding(IntPtr window) =>
        window != IntPtr.Zero && window == _child && IsWindow(window) && GetParent(window) == _container;

    /// <summary>Re-apply the layout if the app moved/resized its own window.</summary>
    public void Fit()
    {
        if (_child == IntPtr.Zero || !IsWindow(_child)) return;
        GetWindowRect(_container, out var container);
        GetWindowRect(_child, out var have);
        if (have.Left != container.Left - _offsetX || have.Top != container.Top - _offsetY ||
            have.Right - have.Left != ExtentWidth || have.Bottom - have.Top != ExtentHeight)
            ResizeChild();
    }

    public void ScrollTo(int x, int y)
    {
        _offsetX = Math.Clamp(x, 0, Math.Max(0, ExtentWidth - ViewportWidth));
        _offsetY = Math.Clamp(y, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (_child != IntPtr.Zero && IsWindow(_child))
            SetWindowPos(_child, IntPtr.Zero, -_offsetX, -_offsetY, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS | SWP_DEFERERASE);
        ScrollChanged?.Invoke();
    }

    public void ScrollBy(int dx, int dy) => ScrollTo(_offsetX + dx, _offsetY + dy);

    public void SetZoom(double zoom, double focalRatioX = 0.5, double focalRatioY = 0.5)
    {
        double clamped = Math.Clamp(Math.Round(zoom, 2), 1.0, 4.0);
        if (Math.Abs(clamped - _zoom) < 0.005) return;

        double prevZoom = _zoom;
        _zoom = clamped;

        if (ViewportWidth > 0 && ViewportHeight > 0)
        {
            double fx = Math.Clamp(focalRatioX, 0.0, 1.0) * ViewportWidth;
            double fy = Math.Clamp(focalRatioY, 0.0, 1.0) * ViewportHeight;
            double scaleRatio = _zoom / prevZoom;
            _offsetX = (int)Math.Round((_offsetX + fx) * scaleRatio - fx);
            _offsetY = (int)Math.Round((_offsetY + fy) * scaleRatio - fy);
        }

        ResizeChild();
    }

    public void ResetZoomAndPan()
    {
        _zoom = 1.0;
        _offsetX = 0;
        _offsetY = 0;
        ResizeChild();
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizeChild();
    }

    private void ResizeChild()
    {
        if (_child == IntPtr.Zero || _container == IntPtr.Zero || !IsWindow(_child))
        {
            ScrollChanged?.Invoke();
            return;
        }
        GetClientRect(_container, out var rc);
        ViewportWidth = Math.Max(1, rc.Right - rc.Left);
        ViewportHeight = Math.Max(1, rc.Bottom - rc.Top);

        int baseW = ScrollWhenLarger ? Math.Max(ViewportWidth, _naturalWidth) : ViewportWidth;
        int baseH = ScrollWhenLarger ? Math.Max(ViewportHeight, _naturalHeight) : ViewportHeight;

        int width = Math.Max(ViewportWidth, (int)Math.Round(baseW * _zoom));
        int height = Math.Max(ViewportHeight, (int)Math.Round(baseH * _zoom));

        _offsetX = Math.Clamp(_offsetX, 0, Math.Max(0, width - ViewportWidth));
        _offsetY = Math.Clamp(_offsetY, 0, Math.Max(0, height - ViewportHeight));

        SetWindowPos(_child, IntPtr.Zero, -_offsetX, -_offsetY, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOCOPYBITS | SWP_DEFERERASE);

        // Apps can enforce their own minimum size; allow panning whenever zoomed or in scroll mode.
        GetWindowRect(_child, out var actual);
        bool canPan = ScrollWhenLarger || _zoom > 1.005;
        ExtentWidth = canPan ? Math.Max(width, actual.Right - actual.Left) : ViewportWidth;
        ExtentHeight = canPan ? Math.Max(height, actual.Bottom - actual.Top) : ViewportHeight;

        ScrollTo(_offsetX, _offsetY); // re-clamp after a resize, raises ScrollChanged
    }
}
