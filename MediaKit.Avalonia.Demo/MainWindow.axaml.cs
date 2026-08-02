using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Rendering;
using Avalonia.Threading;
using MediaKit.Avalonia.Effects;

namespace MediaKit.Avalonia.Demo;

public partial class MainWindow : Window
{
    private string? _filePath;

    public MainWindow()
    {
        InitializeComponent();
        this.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.Fps;
        effectList.ItemsSource = ShaderEffectConverter.Names;
    }

    private void BtnInvertProbe_Click(object? sender, RoutedEventArgs e)
    {
        new InvertProbeWindow().Show(this);
    }

    private async void BtnOpen_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select File",
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"] },
                new FilePickerFileType("Shaders") { Patterns = ["*.sksl"] },
                new FilePickerFileType("All") { Patterns = ["*.*"] }
            ],
            AllowMultiple = false
        });

        if (files.Count == 0) return;
        _filePath = files[0].Path.LocalPath;

        if (_filePath.EndsWith(".sksl", StringComparison.OrdinalIgnoreCase))
        {
            ApplyEffect(null);
            // 任意文件统一按自生成处理；若该着色器靠目标像素取源，attach 时会报明确的错
            var effect = new ShaderPainter(new Uri(_filePath));
            ShaderEffect.SetEffect(rect, effect);
            effect.StartAnimation();
            return;
        }

        var current = ShaderEffect.GetEffect(rect);
        if (current is PanoEffect pano)
        {
            pano.Image = new Uri(_filePath);
        }
        else
        {
            ApplyEffect(_selectedEffectName);
        }
    }

    private string? _selectedEffectName;

    private void EffectRadioButton_Checked(object? sender, RoutedEventArgs e)
    {
        if (rect == null) return;
        if (sender is not RadioButton rb || rb.IsChecked != true) return;
        var name = rb.Content as string;
        _selectedEffectName = name;
        ApplyEffect(name);
    }

    private void ApplyEffect(string? name)
    {
        ShaderEffect.SetEffect(rect, null);
        PanelVR.IsVisible = false;
        PanelRipple.IsVisible = false;

        if (name == null)
        {
            rightPanel.Width = 0;
            return;
        }

        if (string.Equals(name, "Pano", StringComparison.OrdinalIgnoreCase))
        {
            if (_filePath == null)
            {
                rightPanel.Width = 0;
                return;
            }
            PanelVR.IsVisible = true;
            rightPanel.Width = 204;
            var path = _filePath;
            Dispatcher.UIThread.Post(() =>
            {
                rect.Fill = Brushes.Black;
                var pano = new PanoEffect { Image = new Uri(path) };
                ShaderEffect.SetEffect(rect, pano);
            });
        }
        else if (string.Equals(name, "Ripple", StringComparison.OrdinalIgnoreCase))
        {
            if (_filePath == null)
                rect.Fill = Brushes.Gray;
            else
                rect.Fill = CreateFill();
            var ripple = new RippleEffect();
            ShaderEffect.SetEffect(rect, ripple);
            PanelRipple.IsVisible = true;
            rightPanel.Width = 204;
        }
        else
        {
            rightPanel.Width = 0;
            var effect = ShaderEffectConverter.Create(name);
            if (effect != null)
                ShaderEffect.SetEffect(rect, effect);
        }
    }

    private ImageBrush CreateFill()
    {
        var width = Math.Max(1, (int)rect.Bounds.Width);
        using var stream = File.OpenRead(_filePath!);
        return new ImageBrush(Bitmap.DecodeToWidth(stream, width)) { Stretch = Stretch.Uniform };
    }
}
