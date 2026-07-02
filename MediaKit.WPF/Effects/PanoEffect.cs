using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace MediaKit.WPF.Effects;

/// <summary>
/// 360 全景视频效果。将 PanoEffect 实例赋给元素的 Effect 属性并调用 Attach 即可启用。
/// 支持鼠标拖拽旋转 + 惯性滑动。基础设施（_shader / Input / Target / 构造函数 / MarkupExtension）由源生成器产出。
/// </summary>
public partial class PanoEffect : ShaderEffect
{
    #region Dependency Properties

    public static readonly DependencyProperty FovProperty =
        DependencyProperty.Register(nameof(Fov), typeof(double), typeof(PanoEffect),
            new PropertyMetadata(90.0, OnParamsChanged));

    public static readonly DependencyProperty ZoomProperty =
        DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(PanoEffect),
            new PropertyMetadata(0.5, OnParamsChanged));

    public static readonly DependencyProperty RotationXProperty =
        DependencyProperty.Register(nameof(RotationX), typeof(double), typeof(PanoEffect),
            new PropertyMetadata(0.5, OnParamsChanged));

    public static readonly DependencyProperty RotationYProperty =
        DependencyProperty.Register(nameof(RotationY), typeof(double), typeof(PanoEffect),
            new PropertyMetadata(0.5, OnParamsChanged));

    public double Fov { get => (double)GetValue(FovProperty); set => SetValue(FovProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double RotationX { get => (double)GetValue(RotationXProperty); set => SetValue(RotationXProperty, value); }
    public double RotationY { get => (double)GetValue(RotationYProperty); set => SetValue(RotationYProperty, value); }

    /// <summary>
    /// 重置指定元素的全景参数为初始值。
    /// </summary>
    public static void Reset(UIElement e)
    {
        if (e is FrameworkElement el && el.Effect is PanoEffect effect)
            effect.Reset();
    }

    #endregion

    #region Shader Register DPs（打包 / 计算，手写）

    // C0: 打包 rotationX,rotationY,zoom,fov
    private static readonly DependencyProperty ParamsProperty =
        DependencyProperty.Register("Params", typeof(Point4D), typeof(PanoEffect),
            new UIPropertyMetadata(new Point4D(0.5, 0.5, 0.5, 90.0), PixelShaderConstantCallback(0)));

    // C1: scaleX, scaleY, aspectRatio（运行时计算）
    private static readonly DependencyProperty ViewProperty =
        DependencyProperty.Register("View", typeof(Vector3D), typeof(PanoEffect),
            new UIPropertyMetadata(new Vector3D(1.0, 1.0, 1.778), PixelShaderConstantCallback(1)));

    #endregion

    #region Instance State

    private const double InertiaFriction = 0.95;
    private const double InertiaStopThreshold = 0.0000005;

    private FrameworkElement? _element;
    private FrameworkElement? _parent;
    private bool _isPressed;
    private Point _lastPos;
    private DateTimeOffset _lastTime;
    private double _velocityX;
    private double _velocityY;
    private bool _isInertiaRunning;

    // 去重
    private TimeSpan _lastRenderTime;

    private double _initialFov;
    private double _initialZoom;
    private double _initialRotationX;
    private double _initialRotationY;

    // 挂载前的原始尺寸，用于 Detach 时还原
    private double _originalWidth;
    private double _originalHeight;

    #endregion

    partial void OnConstructed()
    {
        UpdateShaderValue(ParamsProperty);
        UpdateShaderValue(ViewProperty);
    }

    #region Attach / Detach

    public void Attach(FrameworkElement element)
    {
        _element = element;
        _originalWidth = element.Width;
        _originalHeight = element.Height;

        _initialFov = Fov;
        _initialZoom = Zoom;
        _initialRotationX = RotationX;
        _initialRotationY = RotationY;

        UpdateParams();
        element.Effect = this;

        element.MouseLeftButtonDown += OnMouseDown;
        element.MouseMove += OnMouseMove;
        element.MouseLeftButtonUp += OnMouseUp;
        element.MouseWheel += OnMouseWheel;

        _parent = element.Parent as FrameworkElement;
        if (_parent != null)
            _parent.SizeChanged += OnParentSizeChanged;

        if (element is MediaElement me)
            me.MediaOpened += OnMediaOpened;
        else if (element is Image)
            DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image))
                .AddValueChanged(element, OnImageSourceChanged);

        UpdateViewport();
    }

    public void Detach()
    {
        if (_element == null) return;

        StopInertia();
        _element.MouseLeftButtonDown -= OnMouseDown;
        _element.MouseMove -= OnMouseMove;
        _element.MouseLeftButtonUp -= OnMouseUp;
        _element.MouseWheel -= OnMouseWheel;

        if (_parent != null)
        {
            _parent.SizeChanged -= OnParentSizeChanged;
            _parent = null;
        }

        if (_element is MediaElement me)
            me.MediaOpened -= OnMediaOpened;
        else if (_element is Image)
            DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image))
                .RemoveValueChanged(_element, OnImageSourceChanged);

        // 还原挂载前尺寸，避免移除效果后元素尺寸被污染
        _element.Width = _originalWidth;
        _element.Height = _originalHeight;
        _element.Effect = null;
        _element = null;
    }

    #endregion

    #region Internal Logic

    private bool _isUpdateingParams;

    private void UpdateParams()
    {
        if (_isUpdateingParams) return;
        SetValue(ParamsProperty, new Point4D(RotationX, RotationY, Zoom, Fov));
    }

    private void UpdateRotation(double rx, double ry)
    {
        _isUpdateingParams = true;
        try
        {
            RotationX = rx;
            RotationY = ry;
        }
        finally
        {
            _isUpdateingParams = false;
        }
        SetValue(ParamsProperty, new Point4D(rx, ry, Zoom, Fov));
    }

    private static double Clamp(double value, double min, double max)
        => Math.Max(min, Math.Min(max, value));

    /// <summary>
    /// 重置全景参数为初始值。
    /// </summary>
    public void Reset()
    {
        StopInertia();
        _isUpdateingParams = true;
        try
        {
            Fov = _initialFov;
            Zoom = _initialZoom;
            RotationX = _initialRotationX;
            RotationY = _initialRotationY;
        }
        finally
        {
            _isUpdateingParams = false;
        }
        this.UpdateParams();
    }

    #endregion

    #region Mouse Interaction

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_element == null) return;
        _isPressed = true;
        _lastPos = e.GetPosition(_element);
        _lastTime = DateTimeOffset.Now;
        _velocityX = 0;
        _velocityY = 0;
        StopInertia();
        _element.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_element == null || !_isPressed || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(_element);
        var now = DateTimeOffset.Now;
        var dt = Math.Max(1, (now - _lastTime).TotalMilliseconds);

        var dx = pos.X - _lastPos.X;
        var dy = pos.Y - _lastPos.Y;

        var slot = LayoutInformation.GetLayoutSlot(_element);
        var w = slot.Width;
        var h = slot.Height;
        if (w <= 0 || h <= 0) return;

        var instantVx = dx / w * 0.5 / dt;
        var instantVy = -dy / h * 0.5 / dt;
        const double alpha = 0.4;
        _velocityX = _velocityX * (1 - alpha) + instantVx * alpha;
        _velocityY = _velocityY * (1 - alpha) + instantVy * alpha;

        var rx = RotationX + dx / w * 0.5;
        var ry = RotationY - dy / h * 0.5;

        if (rx > 1.0) rx -= 1.0;
        if (rx < 0.0) rx += 1.0;
        ry = Clamp(ry, 0.01, 0.99);

        UpdateRotation(rx, ry);

        _lastPos = pos;
        _lastTime = now;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPressed = false;
        _element?.ReleaseMouseCapture();
        StartInertia();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_element == null) return;
        var zoom = Zoom + (e.Delta > 0 ? 0.05 : -0.05);
        zoom = Clamp(zoom, 0.1, 2.0);
        Zoom = zoom;
    }

    #endregion

    #region Inertia

    private void StartInertia()
    {
        var speed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);
        if (speed < InertiaStopThreshold) return;

        _lastTime = DateTimeOffset.Now;
        _isInertiaRunning = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopInertia()
    {
        if (_isInertiaRunning)
        {
            _isInertiaRunning = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_element == null) return;

        // 过滤同一帧的重复触发
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderTime) return;
            _lastRenderTime = args.RenderingTime;
        }

        var now = DateTimeOffset.Now;
        var dt = Math.Max(1, (now - _lastTime).TotalMilliseconds);
        _lastTime = now;

        // 正常处理
        var moveX = _velocityX * dt;
        var moveY = _velocityY * dt;

        var decay = Math.Pow(InertiaFriction, dt / 16.0);
        _velocityX *= decay;
        _velocityY *= decay;

        if (Math.Abs(_velocityX) < InertiaStopThreshold &&
            Math.Abs(_velocityY) < InertiaStopThreshold)
        {
            StopInertia();
            return;
        }

        var rx = RotationX + moveX;
        var ry = RotationY + moveY;

        if (rx > 1.0) rx -= 1.0;
        if (rx < 0.0) rx += 1.0;
        ry = Clamp(ry, 0.01, 0.99);

        UpdateRotation(rx, ry);
    }

    #endregion

    #region Viewport

    private void OnParentSizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewport();
    private void OnMediaOpened(object sender, RoutedEventArgs e) => UpdateViewport();
    private void OnImageSourceChanged(object? sender, EventArgs e) => UpdateViewport();

    private void UpdateViewport()
    {
        if (_element == null) return;

        var slot = LayoutInformation.GetLayoutSlot(_element);
        double vpW = slot.Width;
        double vpH = slot.Height;
        if (vpW <= 0 || vpH <= 0) return;

        double videoW = vpW;
        double videoH = vpH;
        if (_element is MediaElement me && me.NaturalVideoWidth > 0)
        {
            videoW = me.NaturalVideoWidth;
            videoH = me.NaturalVideoHeight;
        }
        else if (_element is Image img && img.Source is BitmapSource bmp && bmp.PixelWidth > 0)
        {
            videoW = bmp.PixelWidth;
            videoH = bmp.PixelHeight;
        }

        _element.Width = Math.Max(vpW, videoW);
        _element.Height = Math.Max(vpH, videoH);

        double scaleX = Math.Min(1.0, vpW / videoW);
        double scaleY = Math.Min(1.0, vpH / videoH);
        double aspectRatio = vpH > 0 ? vpW / vpH : 1.778;

        SetValue(ViewProperty, new Vector3D(scaleX, scaleY, aspectRatio));
    }

    #endregion

    #region Property Callbacks

    private static void OnParamsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PanoEffect)d).UpdateParams();

    #endregion
}
