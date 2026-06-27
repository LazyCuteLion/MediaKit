using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace MediaKit.WPF;

/// <summary>
/// 360 全景视频效果。在任意 UIElement 上设置 PanoEffect.IsEnabled="True" 即可启用。
/// 支持鼠标拖拽旋转 + 惯性滑动。
/// </summary>
public class PanoEffect : ShaderEffect
{
    private static readonly Dictionary<FrameworkElement, PanoEffect> _effects = new();

    private static readonly PixelShader _shader = new()
    {
        UriSource = new Uri("pack://application:,,,/MediaKit.WPF;component/Shaders/Compiled/Pano.ps")
    };

    #region Attached Properties

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(PanoEffect),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty FovProperty =
        DependencyProperty.RegisterAttached("Fov", typeof(double), typeof(PanoEffect),
            new PropertyMetadata(90.0, OnFovChanged));

    public static readonly DependencyProperty ZoomProperty =
        DependencyProperty.RegisterAttached("Zoom", typeof(double), typeof(PanoEffect),
            new PropertyMetadata(0.5, OnZoomChanged));

    public static readonly DependencyProperty RotationXProperty =
        DependencyProperty.RegisterAttached("RotationX", typeof(double), typeof(PanoEffect),
            new PropertyMetadata(0.5, OnRotationChanged));

    public static readonly DependencyProperty RotationYProperty =
        DependencyProperty.RegisterAttached("RotationY", typeof(double), typeof(PanoEffect),
            new PropertyMetadata(0.5, OnRotationChanged));

    public static bool GetIsEnabled(UIElement e) => (bool)e.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(UIElement e, bool v) => e.SetValue(IsEnabledProperty, v);

    public static double GetFov(UIElement e) => (double)e.GetValue(FovProperty);
    public static void SetFov(UIElement e, double v) => e.SetValue(FovProperty, v);

    public static double GetZoom(UIElement e) => (double)e.GetValue(ZoomProperty);
    public static void SetZoom(UIElement e, double v) => e.SetValue(ZoomProperty, v);

    public static double GetRotationX(UIElement e) => (double)e.GetValue(RotationXProperty);
    public static void SetRotationX(UIElement e, double v) => e.SetValue(RotationXProperty, v);

    public static double GetRotationY(UIElement e) => (double)e.GetValue(RotationYProperty);
    public static void SetRotationY(UIElement e, double v) => e.SetValue(RotationYProperty, v);

    /// <summary>
    /// 重置指定元素的全景参数为初始值。
    /// </summary>
    public static void Reset(UIElement e)
    {
        if (e is FrameworkElement el && _effects.TryGetValue(el, out var effect))
            effect.Reset();
    }

    #endregion

    #region Shader Register DPs

    private static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(PanoEffect), 0);

    private static readonly DependencyProperty ParamsProperty =
        DependencyProperty.Register("Params", typeof(Point4D), typeof(PanoEffect),
            new UIPropertyMetadata(new Point4D(0.5, 0.5, 0.5, 90.0), PixelShaderConstantCallback(0)));

    private static readonly DependencyProperty ViewProperty =
        DependencyProperty.Register("View", typeof(Point4D), typeof(PanoEffect),
            new UIPropertyMetadata(new Point4D(1.0, 1.0, 1.778, 0.0), PixelShaderConstantCallback(1)));

    #endregion

    #region Instance State

    private const double InertiaFriction = 0.95;
    private const double InertiaStopThreshold = 0.0000005;

    private FrameworkElement? _element;
    private FrameworkElement? _parent;
    private bool _isPressed;
    private Point _lastPos;
    private long _lastTick;
    private double _velocityX;
    private double _velocityY;
    private bool _isInertiaRunning;

    private double _initialFov;
    private double _initialZoom;
    private double _initialRotationX;
    private double _initialRotationY;

    #endregion

    #region Constructor

    private PanoEffect()
    {
        PixelShader = _shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(ParamsProperty);
        UpdateShaderValue(ViewProperty);
    }

    #endregion

    #region Attach / Detach

    private void Attach(FrameworkElement element)
    {
        _element = element;

        _initialFov = GetFov(element);
        _initialZoom = GetZoom(element);
        _initialRotationX = GetRotationX(element);
        _initialRotationY = GetRotationY(element);

        SyncParams();
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

        UpdateViewport();
    }

    private void Detach()
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

        _element = null;
    }

    #endregion

    #region Internal Logic

    private void SyncParams()
    {
        if (_element == null) return;
        SetValue(ParamsProperty, new Point4D(
            GetRotationX(_element), GetRotationY(_element),
            GetZoom(_element), GetFov(_element)));
    }

    private void UpdateParamsRotation(double rx, double ry)
    {
        if (_element == null) return;
        SetValue(ParamsProperty, new Point4D(rx, ry, GetZoom(_element), GetFov(_element)));
    }

    private static long GetTick()
    {
#if NET6_0_OR_GREATER
        return Environment.TickCount64;
#else
        return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
#endif
    }

    private static double Clamp(double value, double min, double max)
        => Math.Max(min, Math.Min(max, value));

    /// <summary>
    /// 重置全景参数为初始值。
    /// </summary>
    public void Reset()
    {
        if (_element == null) return;
        StopInertia();
        SetFov(_element, _initialFov);
        SetZoom(_element, _initialZoom);
        SetRotationX(_element, _initialRotationX);
        SetRotationY(_element, _initialRotationY);
    }

    #endregion

    #region Mouse Interaction

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_element == null) return;
        _isPressed = true;
        _lastPos = e.GetPosition(_element);
        _lastTick = GetTick();
        _velocityX = 0;
        _velocityY = 0;
        StopInertia();
        _element.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_element == null || !_isPressed || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(_element);
        var now = GetTick();
        var dt = Math.Max(1, now - _lastTick);

        var dx = pos.X - _lastPos.X;
        var dy = pos.Y - _lastPos.Y;

        var w = _element.ActualWidth;
        var h = _element.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var instantVx = dx / w * 0.5 / dt;
        var instantVy = -dy / h * 0.5 / dt;
        const double alpha = 0.4;
        _velocityX = _velocityX * (1 - alpha) + instantVx * alpha;
        _velocityY = _velocityY * (1 - alpha) + instantVy * alpha;

        var rx = GetRotationX(_element) + dx / w * 0.5;
        var ry = GetRotationY(_element) - dy / h * 0.5;

        if (rx > 1.0) rx -= 1.0;
        if (rx < 0.0) rx += 1.0;
        ry = Clamp(ry, 0.01, 0.99);

        SetRotationX(_element, rx);
        SetRotationY(_element, ry);
        UpdateParamsRotation(rx, ry);

        _lastPos = pos;
        _lastTick = now;
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
        var zoom = GetZoom(_element) + (e.Delta > 0 ? 0.05 : -0.05);
        zoom = Clamp(zoom, 0.1, 2.0);
        SetZoom(_element, zoom);
    }

    #endregion

    #region Inertia

    private void StartInertia()
    {
        var speed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);
        if (speed < InertiaStopThreshold) return;

        _lastTick = GetTick();
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

        var now = GetTick();
        var dt = Math.Max(1, now - _lastTick);
        _lastTick = now;

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

        var rx = GetRotationX(_element) + moveX;
        var ry = GetRotationY(_element) + moveY;

        if (rx > 1.0) rx -= 1.0;
        if (rx < 0.0) rx += 1.0;
        ry = Clamp(ry, 0.01, 0.99);

        SetRotationX(_element, rx);
        SetRotationY(_element, ry);
        UpdateParamsRotation(rx, ry);
    }

    #endregion

    #region Viewport

    private void OnParentSizeChanged(object sender, SizeChangedEventArgs e) => UpdateViewport();
    private void OnMediaOpened(object sender, RoutedEventArgs e) => UpdateViewport();

    private void UpdateViewport()
    {
        if (_element == null) return;

        var slot = LayoutInformation.GetLayoutSlot(_element);
        double vpW = slot.Width;
        double vpH = slot.Height;
        if (vpW <= 0 || vpH <= 0) return;

        double videoW = _element.ActualWidth;
        double videoH = _element.ActualHeight;
        if (_element is MediaElement me && me.NaturalVideoWidth > 0)
        {
            videoW = me.NaturalVideoWidth;
            videoH = me.NaturalVideoHeight;
        }

        _element.Width = Math.Max(vpW, videoW);
        _element.Height = Math.Max(vpH, videoH);

        double boundX = Math.Min(1.0, vpW / videoW);
        double boundY = Math.Min(1.0, vpH / videoH);
        double aspectRatio = vpH > 0 ? vpW / vpH : 1.778;

        SetValue(ViewProperty, new Point4D(boundX, boundY, aspectRatio, 0));
    }

    #endregion

    #region Attached Property Callbacks

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if ((bool)e.NewValue)
        {
            var effect = new PanoEffect();
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

    private static void OnFovChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement el && _effects.TryGetValue(el, out var effect))
            effect.SyncParams();
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement el && _effects.TryGetValue(el, out var effect))
            effect.SyncParams();
    }

    private static void OnRotationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement el && _effects.TryGetValue(el, out var effect))
            effect.SyncParams();
    }

    #endregion
}
