using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MediaKit.WPF;
using MediaKit.WPF.Effects;

namespace MediaKit.WPF.Demo;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog();
        dialog.Filter = "Video|*.mp4;*.mkv;*.avi;*.webm|All|*.*";
        if (dialog.ShowDialog() == true)
        {
            player.Source = new Uri(dialog.FileName, UriKind.Absolute);
            player.Play();
        }
    }

    private void RadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (player == null) return;
        var rb = sender as RadioButton;
        var tag = rb?.Content?.ToString();

        // 先清除当前效果
        DetachCurrentEffect();
        Panel_VR.Visibility = Visibility.Collapsed;
        Panel_Tile.Visibility = Visibility.Collapsed;
        Panel_Alpha.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "VR":
                var pano = new PanoEffect { Fov = sliderFov.Value, Zoom = sliderZoom.Value };
                pano.Attach(player);
                Panel_VR.Visibility = Visibility.Visible;
                // 绑定 Slider → 实例属性
                sliderFov.ValueChanged -= SliderFov_ValueChanged;
                sliderFov.ValueChanged += SliderFov_ValueChanged;
                sliderZoom.ValueChanged -= SliderZoom_ValueChanged;
                sliderZoom.ValueChanged += SliderZoom_ValueChanged;
                break;
            case "Tile":
                var tile = new TileEffect
                {
                    Rows = sliderRows.Value,
                    Columns = sliderColumns.Value,
                    Spacing = sliderSpacing.Value
                };
                tile.Attach(player);
                Panel_Tile.Visibility = Visibility.Visible;
                sliderRows.ValueChanged -= SliderTile_ValueChanged;
                sliderRows.ValueChanged += SliderTile_ValueChanged;
                sliderColumns.ValueChanged -= SliderTile_ValueChanged;
                sliderColumns.ValueChanged += SliderTile_ValueChanged;
                sliderSpacing.ValueChanged -= SliderTile_ValueChanged;
                sliderSpacing.ValueChanged += SliderTile_ValueChanged;
                break;
            case "Alpha":
                var alpha = new AlphaEffect();
                alpha.Attach(player);
                Panel_Alpha.Visibility = Visibility.Visible;
                break;
        }
    }

    private void DetachCurrentEffect()
    {
        switch (player.Effect)
        {
            case PanoEffect pano: pano.Detach(); break;
            case TileEffect tile: tile.Detach(); break;
            case AlphaEffect alpha: alpha.Detach(); break;
        }
        player.Effect = null;
    }

    private void SliderFov_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (player.Effect is PanoEffect pano) pano.Fov = e.NewValue;
    }

    private void SliderZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (player.Effect is PanoEffect pano) pano.Zoom = e.NewValue;
    }

    private void SliderTile_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (player.Effect is TileEffect tile)
        {
            tile.Rows = sliderRows.Value;
            tile.Columns = sliderColumns.Value;
            tile.Spacing = sliderSpacing.Value;
        }
    }

    private void AlphaPos_Checked(object sender, RoutedEventArgs e)
    {
        if (player == null) return;
        var rb = sender as RadioButton;
        if (player.Effect is AlphaEffect alpha &&
            Enum.TryParse<Dock>(rb?.Content?.ToString(), out var dock))
            alpha.Position = dock;
    }

    // 当前背景效果的卸载委托（背景效果均为 @register 自包含类，含 Attach/Detach）
    private Action? _detachBg;

    private void BgEffect_Checked(object sender, RoutedEventArgs e)
    {
        if (bgLayer == null) return;

        _detachBg?.Invoke();
        _detachBg = null;

        var tag = (sender as RadioButton)?.Content?.ToString();
        switch (tag)
        {
            case "bg1": { var fx = new Bg1Effect(); fx.Attach(bgLayer); _detachBg = fx.Detach; break; }
            case "bg2": { var fx = new Bg2Effect(); fx.Attach(bgLayer); _detachBg = fx.Detach; break; }
            case "bg3": { var fx = new Bg3Effect(); fx.Attach(bgLayer); _detachBg = fx.Detach; break; }
            case "bg4": { var fx = new Bg4Effect(); fx.Attach(bgLayer); _detachBg = fx.Detach; break; }
            case "bg5": { var fx = new Bg5Effect(); fx.Attach(bgLayer); _detachBg = fx.Detach; break; }
            case "bg6": { var fx = new Bg6Effect(); fx.Attach(bgLayer); _detachBg = fx.Detach; break; }
            // GlassRain 采样输入图像（雨滴玻璃扭曲），挂到视频元素上更直观
            case "GlassRain": { var fx = new GlassRainEffect(); fx.Attach(player); _detachBg = fx.Detach; break; }
            case "Ripple": { var fx = new RippleEffect(); fx.Attach(player); _detachBg = fx.Detach; break; }
        }
    }
}