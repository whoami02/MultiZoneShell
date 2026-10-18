using System.Runtime.InteropServices;

namespace MultiZoneShell.Interop;

/// <summary>
/// Low-level mouse hook: reports button-down, wheel, macOS-style pan dragging, Smart Zoom double-click,
/// and cursor position at screen coordinates (physical pixels).
/// </summary>
internal sealed class GlobalMouseHook : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _proc; // keep delegate alive for GC
    private IntPtr _hook;

    private bool _isPanning;
    private bool _isSwapDrag;
    private bool _panSwallow;
    private int _lastPanX;
    private int _lastPanY;
    private long _lastMoveTicks;
    private double _velX;
    private double _velY;

    private long _lastClickTicks;
    private int _lastClickX;
    private int _lastClickY;

    public event Action<int, int>? ButtonDown;

    /// <summary>x, y, delta (120 per notch), horizontal.</summary>
    public event Action<int, int, int, bool>? Wheel;

    /// <summary>StartX, StartY, dx, dy in physical screen pixels.</summary>
    public event Action<int, int, int, int>? PanDelta;

    /// <summary>StartX, StartY, velocityX (px/frame), velocityY (px/frame) for macOS inertia.</summary>
    public event Action<int, int, double, double>? PanEnded;

    /// <summary>Smart zoom trigger: x, y.</summary>
    public event Action<int, int>? SmartZoomRequested;

    /// <summary>Plain double-click on a zone body (no modifiers): start drag-to-swap.</summary>
    public event Action<int, int>? DoubleClick;

    /// <summary>Drag-to-swap move (current screen x, y).</summary>
    public event Action<int, int>? SwapDragMove;

    /// <summary>Drag-to-swap release (screen x, y).</summary>
    public event Action<int, int>? SwapDragEnded;

    /// <summary>Screen cursor moved (x, y) for auto-showing the macOS Dock.</summary>
    public event Action<int, int>? CursorMoved;

    /// <summary>
    /// Optional predicate evaluated on LBUTTONDOWN to check if the zone at (x, y) has the macOS Hand Tool active.
    /// </summary>
    public Func<int, int, bool>? IsHandToolActiveAt { get; set; }

    /// <summary>
    /// Optional predicate evaluated on LBUTTONDOWN to check if (x, y) is inside a zone's body (excluding headers/splitters).
    /// </summary>
    public Func<int, int, bool>? IsPointInZoneBodyAt { get; set; }

    private int _panOriginX;
    private int _panOriginY;

    public GlobalMouseHook()
    {
        _proc = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc,
            NativeMethods.GetModuleHandle(null), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            int x = data.pt.X;
            int y = data.pt.Y;

            if (msg == NativeMethods.WM_MOUSEMOVE)
            {
                CursorMoved?.Invoke(x, y);

                if (_isPanning)
                {
                    int dx = x - _lastPanX;
                    int dy = y - _lastPanY;
                    if (dx != 0 || dy != 0)
                    {
                        long now = Environment.TickCount64;
                        long dt = Math.Max(1, now - _lastMoveTicks);
                        // Exponential smoothing for velocity (normalized to ~16ms frame)
                        double instVx = dx * (16.0 / dt);
                        double instVy = dy * (16.0 / dt);
                        _velX = _velX * 0.4 + instVx * 0.6;
                        _velY = _velY * 0.4 + instVy * 0.6;
                        _lastMoveTicks = now;

                        _lastPanX = x;
                        _lastPanY = y;
                        PanDelta?.Invoke(_panOriginX, _panOriginY, dx, dy);
                    }
                }

                if (_isSwapDrag)
                    SwapDragMove?.Invoke(x, y);
            }
            else if (msg == NativeMethods.WM_LBUTTONDOWN)
            {
                ButtonDown?.Invoke(x, y);

                bool inBody = false;
                bool handActive = false;
                try
                {
                    inBody = IsPointInZoneBodyAt?.Invoke(x, y) == true;
                    handActive = inBody && IsHandToolActiveAt?.Invoke(x, y) == true;
                }
                catch
                {
                    // Never allow visual tree transient exceptions to escape WH_MOUSE_LL.
                }

                bool ctrlAltInBody = inBody && NativeMethods.IsCtrlDown() && NativeMethods.IsAltDown();

                // Check double-click for macOS Smart Zoom (Ctrl+Alt+DoubleClick or HandTool+DoubleClick),
                // and plain double-click for drag-to-swap.
                long now = Environment.TickCount64;
                bool isDblClick = (now - _lastClickTicks <= 400) &&
                                  Math.Abs(x - _lastClickX) <= 24 &&
                                  Math.Abs(y - _lastClickY) <= 24;
                _lastClickTicks = now;
                _lastClickX = x;
                _lastClickY = y;

                if (isDblClick && (ctrlAltInBody || handActive))
                {
                    _isPanning = false;
                    _lastClickTicks = 0;
                    SmartZoomRequested?.Invoke(x, y);
                    return new IntPtr(1);
                }

                if (isDblClick && inBody)
                {
                    _lastClickTicks = 0;
                    _isSwapDrag = true;
                    DoubleClick?.Invoke(x, y);
                    return new IntPtr(1);
                }

                if (ctrlAltInBody || handActive)
                {
                    StartPan(x, y, swallow: true);
                    return new IntPtr(1);
                }
            }
            else if (msg == NativeMethods.WM_MBUTTONDOWN)
            {
                ButtonDown?.Invoke(x, y);
                StartPan(x, y, swallow: false);
            }
            else if (msg == NativeMethods.WM_LBUTTONUP || msg == NativeMethods.WM_MBUTTONUP)
            {
                if (_isPanning)
                {
                    bool swallow = _panSwallow && msg == NativeMethods.WM_LBUTTONUP;
                    EndPan();
                    if (swallow) return new IntPtr(1);
                }

                if (_isSwapDrag && msg == NativeMethods.WM_LBUTTONUP)
                {
                    _isSwapDrag = false;
                    SwapDragEnded?.Invoke(x, y);
                    return new IntPtr(1);
                }
            }
            else if (msg == NativeMethods.WM_RBUTTONDOWN)
            {
                ButtonDown?.Invoke(x, y);
            }
            else if (msg == NativeMethods.WM_MOUSEWHEEL || msg == NativeMethods.WM_MOUSEHWHEEL)
            {
                int delta = (short)(data.mouseData >> 16);
                Wheel?.Invoke(x, y, delta, msg == NativeMethods.WM_MOUSEHWHEEL);
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void StartPan(int x, int y, bool swallow)
    {
        _isPanning = true;
        _panSwallow = swallow;
        _panOriginX = x;
        _panOriginY = y;
        _lastPanX = x;
        _lastPanY = y;
        _velX = 0;
        _velY = 0;
        _lastMoveTicks = Environment.TickCount64;
    }

    private void EndPan()
    {
        _isPanning = false;
        long age = Environment.TickCount64 - _lastMoveTicks;
        double vx = age < 80 ? _velX : 0;
        double vy = age < 80 ? _velY : 0;
        PanEnded?.Invoke(_panOriginX, _panOriginY, vx, vy);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
