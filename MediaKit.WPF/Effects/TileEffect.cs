using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MediaKit.WPF.Effects;

/// <summary>
/// 视频平铺效果。将 TileEffect 实例赋给元素的 Effect 属性并调用 Attach 即可启用。
/// 基础设施与 Rows/Columns/SpacingColor 直连寄存器 DP 由源生成器产出；
/// 此处仅保留 Spacing（换算成 UV）与 AspectRatio 等计算类逻辑。
/// </summary>
public partial class TileEffect : ShaderEffect
{
    #region Dependency Properties

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(TileEffect),
            new PropertyMetadata(0.0, OnSpacingChanged));

    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    #endregion

    #region Shader Register DPs（计算类，手写）

    // C2: float2 spacing (UV)，由 Spacing(px) 按尺寸换算
    private static readonly DependencyProperty SpacingUVProperty =
        DependencyProperty.Register("SpacingUV", typeof(Point), typeof(TileEffect),
            new UIPropertyMetadata(new Point(0, 0), PixelShaderConstantCallback(2)));

    // C4: targetAspect / videoAspect
    private static readonly DependencyProperty AspectRatioProperty =
        DependencyProperty.Register("AspectRatio", typeof(double), typeof(TileEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(4)));

    #endregion

    #region Instance State

    private FrameworkElement? _element;
    private Size _targetSize;
    private double _spacingPx;

    // 挂载前的原始尺寸，用于 Detach 时还原
    private double _originalWidth;
    private double _originalHeight;

    #endregion

    partial void OnConstructed()
    {
        UpdateShaderValue(SpacingUVProperty);
        UpdateShaderValue(AspectRatioProperty);
    }

    #region Attach / Detach

    public void Attach(FrameworkElement element)
    {
        _element = element;
        _originalWidth = element.Width;
        _originalHeight = element.Height;
        element.Effect = this;
        element.SizeChanged += OnSizeChanged;

        if (element is MediaElement me)
            me.MediaOpened += OnMediaOpened;

        FitToContainer();
        UpdateSize();
    }

    public void Detach()
    {
        if (_element == null) return;

        _element.SizeChanged -= OnSizeChanged;
        if (_element is MediaElement me)
            me.MediaOpened -= OnMediaOpened;

        // 还原挂载前尺寸，避免移除效果后元素尺寸被污染
        _element.Width = _originalWidth;
        _element.Height = _originalHeight;
        _element.Effect = null;
        _element = null;
    }

    #endregion

    #region Internal Logic

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

        _spacingPx = Spacing;

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

    #region Property Callbacks

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var effect = (TileEffect)d;
        effect._spacingPx = (double)e.NewValue;
        effect.UpdateSpacingUV();
    }

    #endregion
}
