using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiZoneShell.Config;
using MultiZoneShell.Infrastructure;

namespace MultiZoneShell.Zones;

/// <summary>Single image, or a folder of images as a slideshow, with native macOS Preview-style Pan &amp; Zoom.</summary>
public sealed class ImageZone : IZoneContent
{
    private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    private readonly ZoneConfig _cfg;
    private readonly Grid _root = new() { Background = Brushes.Black, Focusable = true, ClipToBounds = true };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0, 0) };
    private readonly ScaleTransform _scale = new(1.0, 1.0);
    private readonly TranslateTransform _translate = new(0.0, 0.0);

    private List<string> _files = new();
    private DispatcherTimer? _timer;
    private int _index;
    private double _zoom = 1.0;

    public event Action? ViewStateChanged;

    public ImageZone(ZoneConfig cfg)
    {
        _cfg = cfg;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_translate);
        _image.RenderTransform = group;

        _root.SizeChanged += (_, _) => ClampTranslation();
        _root.Children.Add(_image);
    }

    public FrameworkElement View => _root;
    public bool IsMuted => true;
    public double ZoomFactor => _zoom;

    public Task StartAsync()
    {
        LoadFileList();

        if (_files.Count == 0)
            throw new FileNotFoundException("No images found.", _cfg.Source);

        Show(0);

        if (_files.Count > 1)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, _cfg.SlideSeconds)) };
            _timer.Tick += (_, _) =>
            {
                // Pause slideshow auto-advance while operator is zoomed in inspecting an image
                if (Math.Abs(_zoom - 1.0) < 0.01)
                    Show(_index + 1);
            };
            _timer.Start();
        }

        return Task.CompletedTask;
    }

    private void LoadFileList()
    {
        if (Directory.Exists(_cfg.Source))
        {
            _files = Directory.EnumerateFiles(_cfg.Source)
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else if (File.Exists(_cfg.Source))
        {
            _files = new List<string> { _cfg.Source };
        }
    }

    private void Show(int index)
    {
        if (_files.Count == 0) return;
        _index = ((index % _files.Count) + _files.Count) % _files.Count;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file
            bmp.UriSource = new Uri(_files[_index]);
            bmp.EndInit();
            bmp.Freeze();
            _image.Source = bmp;

            var fade = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _image.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        catch (Exception ex)
        {
            Log.Error($"Image load failed: {_files[_index]}", ex);
        }
    }

    public void SetZoom(double zoom, double focalRatioX = 0.5, double focalRatioY = 0.5)
    {
        double clamped = Math.Clamp(Math.Round(zoom, 2), 0.5, 8.0);
        if (Math.Abs(clamped - _zoom) < 0.005) return;

        double w = _root.ActualWidth > 0 ? _root.ActualWidth : 400;
        double h = _root.ActualHeight > 0 ? _root.ActualHeight : 300;
        double fx = Math.Clamp(focalRatioX, 0.0, 1.0) * w;
        double fy = Math.Clamp(focalRatioY, 0.0, 1.0) * h;

        double ratio = clamped / _zoom;
        _zoom = clamped;
        _scale.ScaleX = _zoom;
        _scale.ScaleY = _zoom;

        _translate.X = fx - (fx - _translate.X) * ratio;
        _translate.Y = fy - (fy - _translate.Y) * ratio;

        ClampTranslation();
        ViewStateChanged?.Invoke();
    }

    public void PanBy(double dx, double dy)
    {
        if (Math.Abs(_zoom - 1.0) < 0.01 && _translate.X == 0 && _translate.Y == 0) return;
        var dpi = VisualTreeHelper.GetDpi(_root);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        _translate.X += dx / scaleX;
        _translate.Y += dy / scaleY;
        ClampTranslation();
    }

    public void ResetView()
    {
        _zoom = 1.0;
        _scale.ScaleX = 1.0;
        _scale.ScaleY = 1.0;
        _translate.X = 0;
        _translate.Y = 0;
        ViewStateChanged?.Invoke();
    }

    public void ReloadContent()
    {
        LoadFileList();
        ResetView();
        if (_files.Count > 0) Show(_index);
    }

    private void ClampTranslation()
    {
        double w = _root.ActualWidth;
        double h = _root.ActualHeight;
        if (w <= 0 || h <= 0) return;

        if (_zoom <= 1.0)
        {
            _translate.X = (w - w * _zoom) * 0.5;
            _translate.Y = (h - h * _zoom) * 0.5;
        }
        else
        {
            double minX = w - w * _zoom;
            double minY = h - h * _zoom;
            _translate.X = Math.Clamp(_translate.X, minX, 0);
            _translate.Y = Math.Clamp(_translate.Y, minY, 0);
        }
    }

    public void FocusContent() => _root.Focus();

    public void SetMuted(bool muted) { }

    public void Dispose()
    {
        _timer?.Stop();
        _image.Source = null;
    }
}
