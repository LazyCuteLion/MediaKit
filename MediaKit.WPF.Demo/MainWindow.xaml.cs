using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MediaKit.WPF;

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

        // 先清除所有效果
        PanoEffect.SetIsEnabled(player, false);
        TileEffect.SetIsEnabled(player, false);
        AlphaEffect.SetIsEnabled(player, false);
        Panel_VR.Visibility = Visibility.Collapsed;
        Panel_Tile.Visibility = Visibility.Collapsed;
        Panel_Alpha.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "VR":
                PanoEffect.SetIsEnabled(player, true);
                PanoEffect.SetFov(player, sliderFov.Value);
                PanoEffect.SetZoom(player, sliderZoom.Value);
                Panel_VR.Visibility = Visibility.Visible;
                // 绑定 Slider → 附加属性
                sliderFov.ValueChanged -= SliderFov_ValueChanged;
                sliderFov.ValueChanged += SliderFov_ValueChanged;
                sliderZoom.ValueChanged -= SliderZoom_ValueChanged;
                sliderZoom.ValueChanged += SliderZoom_ValueChanged;
                break;
            case "Tile":
                TileEffect.SetIsEnabled(player, true);
                TileEffect.SetRows(player, sliderRows.Value);
                TileEffect.SetColumns(player, sliderColumns.Value);
                TileEffect.SetSpacing(player, sliderSpacing.Value);
                Panel_Tile.Visibility = Visibility.Visible;
                sliderRows.ValueChanged -= SliderTile_ValueChanged;
                sliderRows.ValueChanged += SliderTile_ValueChanged;
                sliderColumns.ValueChanged -= SliderTile_ValueChanged;
                sliderColumns.ValueChanged += SliderTile_ValueChanged;
                sliderSpacing.ValueChanged -= SliderTile_ValueChanged;
                sliderSpacing.ValueChanged += SliderTile_ValueChanged;
                break;
            case "Alpha":
                AlphaEffect.SetIsEnabled(player, true);
                Panel_Alpha.Visibility = Visibility.Visible;
                break;
        }
    }

    private void SliderFov_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => PanoEffect.SetFov(player, e.NewValue);

    private void SliderZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => PanoEffect.SetZoom(player, e.NewValue);

    private void SliderTile_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TileEffect.SetRows(player, sliderRows.Value);
        TileEffect.SetColumns(player, sliderColumns.Value);
        TileEffect.SetSpacing(player, sliderSpacing.Value);
    }

    private void AlphaPos_Checked(object sender, RoutedEventArgs e)
    {
        if (player == null) return;
        var rb = sender as RadioButton;
        if (Enum.TryParse<Dock>(rb?.Content?.ToString(), out var dock))
            AlphaEffect.SetPosition(player, dock);
    }
}