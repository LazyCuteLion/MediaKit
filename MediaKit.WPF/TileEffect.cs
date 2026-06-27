using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MediaKit.WPF;

/// <summary>
/// 视频平铺效果。在任意 UIElement 上设置 TileEffect.IsEnabled="True" 即可启用。
/// </summary>
public class TileEffect : ShaderEffect
{
    private static readonly Dictionary<FrameworkElement, TileEffect> _effects = new();

    private static readonly PixelShader _shader = new()
    {
        UriSource = new Uri("pack://application:,,,/MediaKit.WPF;component/Shaders/Compiled/Tile.ps")
    };

    #region Attached Properties

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(TileEffect),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.RegisterAttached("Rows", typeof(double), typeof(TileEffect),
            new PropertyMetadata(1.0, OnParamChanged));

    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.RegisterAttached("Columns", typeof(double), typeof(TileEffect),
            new PropertyMetadata(1.0, OnParamChanged));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.RegisterAttached("Spacing", typeof(double), typeof(TileEffect),
            new PropertyMetadata(0.0, OnSpacingChanged));

    public static readonly DependencyProperty SpacingColorProperty =
        DependencyProperty.RegisterAttached("SpacingColor", typeof(Color), typeof(TileEffect),
            new PropertyMetadata(Colors.Transparent, OnParamChanged));

    public static bool GetIsEnabled(UIElement e) => (bool)e.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(UIElement e, bool v) => e.SetValue(IsEnabledProperty, v);

    public static double GetRows(UIElement e) => (double)e.GetValue(RowsProperty);
    public static void SetRows(UIElement e, double v) => e.SetValue(RowsProperty, v);

    public static double GetColumns(UIElement e) => (double)e.GetValue(ColumnsProperty);
    public static void SetColumns(UIElement e, double v) => e.SetValue(ColumnsProperty, v);

    public static double GetSpacing(UIElement e) => (double)e.GetValue(SpacingProperty);
    public static void SetSpacing(UIElement e, double v) => e.SetValue(SpacingProperty, v);

    public static Color GetSpacingColor(UIElement e) => (Color)e.GetValue(SpacingColorProperty);
    public static void SetSpacingColor(UIElement e, Color v) => e.SetValue(SpacingColorProperty, v);

    #endregion

    #region Shader Register DPs

    private static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(TileEffect), 0);

    // C0
    private static readonly DependencyProperty RowsValueProperty =
        DependencyProperty.Register("RowsValue", typeof(double), typeof(TileEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(0)));

    // C1
    private static readonly DependencyProperty ColumnsValueProperty =
        DependencyProperty.Register("ColumnsValue", typeof(double), typeof(TileEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(1)));

    // C2: float2 spacing (UV)
    private static readonly DependencyProperty SpacingUVProperty =
        DependencyProperty.Register("SpacingUV", typeof(Point), typeof(TileEffect),
            new UIPropertyMetadata(new Point(0, 0), PixelShaderConstantCallback(2)));

    // C3
    private static readonly DependencyProperty SpacingColorValueProperty =
        DependencyProperty.Register("SpacingColorValue", typeof(Color), typeof(TileEffect),
            new UIPropertyMetadata(Colors.Transparent, PixelShaderConstantCallback(3)));

    // C4
    private static readonly DependencyProperty AspectRatioProperty =
        DependencyProperty.Register("AspectRatio", typeof(double), typeof(TileEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(4)));

    #endregion

    #region Instance State

    private FrameworkElement? _element;
    private Size _targetSize;
    private double _spacingPx;

    #endregion

    #region Constructor

    private TileEffect()
    {
        PixelShader = _shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(RowsValueProperty);
        UpdateShaderValue(ColumnsValueProperty);
        UpdateShaderValue(SpacingUVProperty);
        UpdateShaderValue(SpacingColorValueProperty);
        UpdateShaderValue(AspectRatioProperty);
    }

    #endregion

    #region Attach / Detach

    private void Attach(FrameworkElement element)
    {
        _element = element;
        SyncParams();
        element.Effect = this;
        element.SizeChanged += OnSizeChanged;

        if (element is MediaElement me)
            me.MediaOpened += OnMediaOpened;

        FitToContainer();
        UpdateSize();
    }

    private void Detach()
    {
        if (_element == null) return;

        _element.SizeChanged -= OnSizeChanged;
        if (_element is MediaElement me)
            me.MediaOpened -= OnMediaOpened;

        _element = null;
    }

    #endregion

    #region Internal Logic

    private void SyncParams()
    {
        if (_element == null) return;
        SetValue(RowsValueProperty, GetRows(_element));
        SetValue(ColumnsValueProperty, GetColumns(_element));
        SetValue(SpacingColorValueProperty, GetSpacingColor(_element));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitToContainer();
        UpdateSize();
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e) => UpdateSize();

    private void FitToContainer()
    {
        if (_element == null) return;
        var container = _element.Parent as FrameworkElement;
        if (container != null)
        {
            _element.Width = container.ActualWidth;
            _element.Height = container.ActualHeight;
        }
    }

    private void UpdateSize()
    {
        if (_element == null) return;

        int videoW = (int)_element.ActualWidth;
        int videoH = (int)_element.ActualHeight;
        if (_element is MediaElement me && me.NaturalVideoWidth > 0)
        {
            videoW = me.NaturalVideoWidth;
            videoH = me.NaturalVideoHeight;
        }

        _targetSize = new Size(_element.ActualWidth, _element.ActualHeight);
        if (_targetSize.Width <= 0 || _targetSize.Height <= 0) return;

        _spacingPx = GetSpacing(_element);

        double targetAspect = _targetSize.Height > 0 ? _targetSize.Width / _targetSize.Height : 1;
        double videoAspect = videoH > 0 ? (double)videoW / videoH : 1;
        SetValue(AspectRatioProperty, videoAspect > 0 ? targetAspect / videoAspect : 1.0);
        UpdateSpacingUV();
    }

    private void UpdateSpacingUV()
    {
        double uvX = _targetSize.Width > 0 ? _spacingPx / _targetSize.Width : 0;
        double uvY = _targetSize.Height > 0 ? _spacingPx / _targetSize.Height : 0;
        SetValue(SpacingUVProperty, new Point(uvX, uvY));
    }

    #endregion

    #region Attached Property Callbacks

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if ((bool)e.NewValue)
        {
            var effect = new TileEffect();
            _effects[element] = effect;
            effect.Attach(element);
        }
        else
        {
            if (_effects.TryGetValue(element, out var effect))
            {
                effect.Detach();
                _effects.Remove(element);
            }
            element.Effect = null;
        }
    }

    private static void OnParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement el && _effects.TryGetValue(el, out var effect))
            effect.SyncParams();
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement el && _effects.TryGetValue(el, out var effect))
        {
            effect._spacingPx = (double)e.NewValue;
            effect.UpdateSpacingUV();
        }
    }

    #endregion
}
