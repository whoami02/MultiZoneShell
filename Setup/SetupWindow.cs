using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using MultiZoneShell.Config;

namespace MultiZoneShell.Setup;

/// <summary>
/// Netflix-style "what should each zone play?" wizard shown at launch.
/// Lets the operator pick a website, video, image folder, document (PPT/PDF/anything)
/// or application for every zone, then hands the result back to the shell.
/// </summary>
public sealed class SetupWindow : Window
{
    private static readonly Brush BgTop = Freeze(new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0F)));
    private static readonly Brush BgBottom = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x22)));
    private static readonly Brush NetflixRed = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x09, 0x14)));
    private static readonly Brush CardBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x34)));
    private static readonly Brush CardHoverBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private static readonly Brush TextPrimary = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
    private static readonly Brush TextSecondary = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6)));
    private static readonly Brush OverlayBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x00, 0x00, 0x00)));
    private static readonly Brush PanelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x20)));
    private static readonly Brush PanelBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x40)));
    private static readonly Brush OptionHover = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x30)));

    private static readonly (string Type, string Label, string Glyph)[] ContentTypes =
    {
        ("web",    "Website",     "\uE774"),
        ("video",  "Video",       "\uE714"),
        ("image",  "Images",      "\uEB9F"),
        ("file",   "Document",    "\uE8A5"),
        ("exe",    "Application", "\uE8FC")
    };

    private readonly ZoneConfig?[] _zones;
    private readonly Border[] _cards;
    private readonly TextBlock[] _cardGlyphs;
    private readonly TextBlock[] _cardLabels;
    private readonly Border _startButton;
    private readonly TextBlock _startText;
    private readonly Grid _root;
    private Border? _modal;

    /// <summary>Non-null zones the operator chose (may be fewer than the card count).</summary>
    public List<ZoneConfig>? SelectedZones { get; private set; }

    public SetupWindow(int zoneCount)
    {
        zoneCount = Math.Clamp(zoneCount, 1, 9);
        _zones = new ZoneConfig?[zoneCount];
        _cards = new Border[zoneCount];
        _cardGlyphs = new TextBlock[zoneCount];
        _cardLabels = new TextBlock[zoneCount];

        Title = "MultiZone — Setup";
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Background = new LinearGradientBrush(Color.FromRgb(0x0B, 0x0B, 0x0F), Color.FromRgb(0x1A, 0x1A, 0x22), 90.0);

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = _root;

        _root.Children.Add(BuildHeader());
        _root.Children.Add(BuildCardRow(zoneCount));
        _startButton = BuildFooter(out _startText);
        _root.Children.Add(_startButton);

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _modal == null) DialogResult = false; };
    }

    private UIElement BuildHeader()
    {
        var logo = new TextBlock
        {
            Text = "MULTIZONE",
            Foreground = NetflixRed,
            FontSize = 34,
            FontWeight = FontWeights.Black,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(48, 0, 0, 8)
        };

        var title = new TextBlock
        {
            Text = "What should play on the wall?",
            Foreground = TextPrimary,
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(48, 0, 0, 0)
        };

        var subtitle = new TextBlock
        {
            Text = "Pick a website, video, images, document (PPT / PDF / …) or application for each zone.",
            Foreground = TextSecondary,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(48, 0, 0, 0)
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(logo);
        stack.Children.Add(title);
        stack.Children.Add(subtitle);

        Grid.SetRow(stack, 0);
        return stack;
    }

    private UIElement BuildCardRow(int count)
    {
        var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            _cards[idx] = BuildCard(idx);
            wrap.Children.Add(_cards[idx]);
        }
        Grid.SetRow(wrap, 1);
        return wrap;
    }

    private Border BuildCard(int index)
    {
        var glyph = new TextBlock
        {
            Text = "+",
            FontSize = 52,
            Foreground = TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _cardGlyphs[index] = glyph;

        var zoneLabel = new TextBlock
        {
            Text = $"ZONE {index + 1}",
            Foreground = TextPrimary,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var status = new TextBlock
        {
            Text = "Choose content",
            Foreground = TextSecondary,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };
        _cardLabels[index] = status;

        var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        inner.Children.Add(glyph);
        inner.Children.Add(zoneLabel);
        inner.Children.Add(status);

        var card = new Border
        {
            Width = 260,
            Height = 300,
            CornerRadius = new CornerRadius(16),
            Background = new LinearGradientBrush(Color.FromRgb(0x18, 0x18, 0x20), Color.FromRgb(0x20, 0x20, 0x28), 90.0),
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(14),
            Cursor = Cursors.Hand,
            Child = inner,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1.0, 1.0)
        };

        card.MouseEnter += (_, _) =>
        {
            ((ScaleTransform)card.RenderTransform).ScaleX = 1.05;
            ((ScaleTransform)card.RenderTransform).ScaleY = 1.05;
            card.BorderBrush = CardHoverBorder;
            card.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, Opacity = 0.7, ShadowDepth = 0 };
        };
        card.MouseLeave += (_, _) =>
        {
            ((ScaleTransform)card.RenderTransform).ScaleX = 1.0;
            ((ScaleTransform)card.RenderTransform).ScaleY = 1.0;
            card.BorderBrush = CardBorderBrush;
            card.Effect = null;
        };
        card.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowPicker(index); };

        return card;
    }

    private Border BuildFooter(out TextBlock text)
    {
        text = new TextBlock
        {
            Text = "Start the wall",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var btn = new Border
        {
            Background = NetflixRed,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(40, 14, 40, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Opacity = 0.5,
            Child = text
        };

        btn.MouseEnter += (_, _) => { if (btn.Opacity >= 1.0) btn.Background = Brushes.IndianRed; };
        btn.MouseLeave += (_, _) => { if (btn.Opacity >= 1.0) btn.Background = NetflixRed; };
        btn.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var chosen = _zones.Where(z => z != null).Select(z => z!).ToList();
            if (chosen.Count == 0) return;
            SelectedZones = chosen;
            DialogResult = true;
        };

        Grid.SetRow(btn, 2);
        return btn;
    }

    private void RefreshStartButton()
    {
        int n = _zones.Count(z => z != null);
        _startButton.Opacity = n > 0 ? 1.0 : 0.5;
        _startText.Text = n > 0 ? $"Start the wall ({n})" : "Start the wall";
    }

    private void CloseModal()
    {
        if (_modal != null)
        {
            _root.Children.Remove(_modal);
            _modal = null;
        }
    }

    private void ShowPicker(int index)
    {
        CloseModal();
        var overlay = new Border { Background = OverlayBrush, Child = BuildPickerPanel(index) };
        overlay.MouseLeftButtonDown += (_, e) => e.Handled = true;
        Grid.SetRowSpan(overlay, 3);
        Panel.SetZIndex(overlay, 100);
        _modal = overlay;
        _root.Children.Add(overlay);
    }

    private UIElement BuildPickerPanel(int index)
    {
        var title = new TextBlock
        {
            Text = $"ZONE {index + 1} — choose content",
            Foreground = TextPrimary,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var options = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (type, label, glyph) in ContentTypes)
            options.Children.Add(BuildOptionButton(type, label, glyph, index));

        var urlBox = new TextBox
        {
            Visibility = Visibility.Collapsed,
            Width = 400,
            Height = 40,
            FontSize = 15,
            Foreground = TextPrimary,
            Background = PanelBrush,
            BorderBrush = PanelBorderBrush,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        urlBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(urlBox.Text))
            {
                Assign(index, new ZoneConfig { Type = "web", Source = urlBox.Text.Trim(), Muted = true });
                CloseModal();
            }
        };

        var addBtn = BuildTextButton("Add", () =>
        {
            if (!string.IsNullOrWhiteSpace(urlBox.Text))
            {
                Assign(index, new ZoneConfig { Type = "web", Source = urlBox.Text.Trim(), Muted = true });
                CloseModal();
            }
        });
        addBtn.Visibility = Visibility.Collapsed;

        var removeBtn = BuildTextButton("Remove", () =>
        {
            _zones[index] = null;
            UpdateCard(index);
            RefreshStartButton();
            CloseModal();
        });
        removeBtn.Visibility = _zones[index] != null ? Visibility.Visible : Visibility.Collapsed;

        var cancelBtn = BuildTextButton("Cancel", CloseModal);

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        };
        actionRow.Children.Add(addBtn);
        actionRow.Children.Add(removeBtn);
        actionRow.Children.Add(cancelBtn);

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(options);
        stack.Children.Add(urlBox);
        stack.Children.Add(actionRow);

        // Keep a reference for URL reveal.
        urlBox.Tag = addBtn;

        return new Border
        {
            Background = PanelBrush,
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(40, 32, 40, 32),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 40, Opacity = 0.8, ShadowDepth = 0 }
        };
    }

    private Border BuildOptionButton(string type, string label, string glyph, int index)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 30,
            Foreground = TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var text = new TextBlock
        {
            Text = label,
            Foreground = TextPrimary,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon);
        stack.Children.Add(text);

        var btn = new Border
        {
            Width = 150,
            Height = 110,
            CornerRadius = new CornerRadius(12),
            Background = PanelBrush,
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8),
            Cursor = Cursors.Hand,
            Child = stack
        };

        btn.MouseEnter += (_, _) => btn.Background = OptionHover;
        btn.MouseLeave += (_, _) => btn.Background = PanelBrush;
        btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; HandleChoice(type, index, btn); };

        return btn;
    }

    private void HandleChoice(string type, int index, Border optionButton)
    {
        switch (type)
        {
            case "web": RevealUrlBox(optionButton); break;
            case "video": BrowseFile(index, type, "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.ts;*.webm;*.mpg;*.mpeg|All files|*.*"); break;
            case "image": BrowseImage(index); break;
            case "file": BrowseFile(index, type, "Documents|*.ppt;*.pptx;*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.txt;*.csv;*.html;*.htm|All files|*.*"); break;
            case "exe": BrowseFile(index, type, "Applications|*.exe;*.bat;*.cmd|All files|*.*"); break;
        }
    }

    private void RevealUrlBox(Border optionButton)
    {
        var options = optionButton.Parent as StackPanel;
        var stack = options?.Parent as StackPanel;
        var urlBox = stack?.Children.OfType<TextBox>().FirstOrDefault();
        if (urlBox == null) return;

        urlBox.Text = string.Empty;
        urlBox.Visibility = Visibility.Visible;
        urlBox.Focus();

        if (urlBox.Tag is Border addBtn) addBtn.Visibility = Visibility.Visible;
    }

    private void BrowseImage(int index)
    {
        var choice = MessageBox.Show(this,
            "Click Yes to pick a folder (slideshow), or No to pick a single image.",
            "Images", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (choice == MessageBoxResult.Yes)
        {
            var dlg = new OpenFolderDialog { Title = "Select an image folder (slideshow)" };
            if (dlg.ShowDialog(this) == true)
                Assign(index, new ZoneConfig { Type = "image", Source = dlg.FolderName, SlideSeconds = 10 });
        }
        else if (choice == MessageBoxResult.No)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select an image",
                Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*"
            };
            if (dlg.ShowDialog(this) == true)
                Assign(index, new ZoneConfig { Type = "image", Source = dlg.FileName });
        }
    }

    private void BrowseFile(int index, string type, string filter)
    {
        var dlg = new OpenFileDialog { Title = "Choose file", Filter = filter };
        if (dlg.ShowDialog(this) == true)
            Assign(index, new ZoneConfig { Type = type, Source = dlg.FileName, Loop = true, Muted = true });
    }

    private void Assign(int index, ZoneConfig zone)
    {
        _zones[index] = zone;
        UpdateCard(index);
        RefreshStartButton();
    }

    private void UpdateCard(int index)
    {
        var z = _zones[index];
        if (z == null)
        {
            _cardGlyphs[index].Text = "+";
            _cardGlyphs[index].Foreground = TextSecondary;
            _cardLabels[index].Text = "Choose content";
            return;
        }

        var (type, label, glyph) = ContentTypes.FirstOrDefault(c => c.Type == z.Type);
        _cardGlyphs[index].Text = glyph;
        _cardGlyphs[index].FontFamily = new FontFamily("Segoe MDL2 Assets");
        _cardGlyphs[index].Foreground = NetflixRed;

        string name = Uri.TryCreate(z.Source, UriKind.Absolute, out var u) && !u.IsFile
            ? u.Host
            : Path.GetFileName(z.Source);
        _cardLabels[index].Text = $"{label} · {name}";
    }

    private static Border BuildTextButton(string label, Action onClick)
    {
        var text = new TextBlock
        {
            Text = label,
            Foreground = TextPrimary,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var btn = new Border
        {
            Background = OptionHover,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(8, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = text
        };
        btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return btn;
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
