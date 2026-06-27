using System;
using System.Numerics;
using System.Reflection;
using System.Windows.Input;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace MediaKit.WinUI3;

/// <summary>
/// 360 全景视频播放控件。
/// 基于 Composition API + Win2D PixelShaderEffect 实现球面投影渲染。
/// 内置鼠标拖拽旋转和滚轮缩放交互。
/// </summary>
public sealed class PanoMediaElement : Panel
{
    // Composition
    private Compositor? _compositor;
    private SpriteVisual? _spriteVisual;
    private CompositionDrawingSurface? _drawingSurface;
    private CompositionGraphicsDevice? _graphicsDevice;

    // Win2D
    private CanvasDevice? _canvasDevice;
    private CanvasRenderTarget? _videoFrameBuffer;
    private byte[]? _shaderBytes;

    // Media
    private MediaPlayer? _player;
    private int _videoWidth;
    private int _videoHeight;
    private bool _isInternalPositionUpdate;
    private long _lastExternalSeekTick; // 防抖：最近一次外部 Seek 的时间戳

    // Interaction
    private bool _isPointerPressed;
    private Windows.Foundation.Point _lastPointerPos;
    private long _lastPointerTick;
    private double _velocityX;
    private double _velocityY;
    private bool _isInertiaRunning;

    /// <summary>初始化 PanoMediaElement 实例。</summary>
    public PanoMediaElement()
    {
        PlayCommand = new RelayCommand(Play);
        PauseCommand = new RelayCommand(Pause);
        StopCommand = new RelayCommand(Stop);
        ResetCommand = new RelayCommand(Reset);

        Background = new SolidColorBrush(Colors.Black);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
    }

    #region Dependency Properties

    /// <summary>视频源 URI。</summary>
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(Uri), typeof(PanoMediaElement),
            new PropertyMetadata(null, OnSourceChanged));

    /// <summary>水平旋转 [0,1]，0.5 为初始正前方。</summary>
    public static readonly DependencyProperty RotationXProperty =
        DependencyProperty.Register(nameof(RotationX), typeof(double), typeof(PanoMediaElement),
            new PropertyMetadata(0.5));

    /// <summary>垂直旋转 [0,1]，0.5 为水平视线。</summary>
    public static readonly DependencyProperty RotationYProperty =
        DependencyProperty.Register(nameof(RotationY), typeof(double), typeof(PanoMediaElement),
            new PropertyMetadata(0.5));

    /// <summary>缩放级别，默认 0.5。</summary>
    public static readonly DependencyProperty ZoomProperty =
        DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(PanoMediaElement),
            new PropertyMetadata(0.5));

    /// <summary>视场角（度），默认 90。</summary>
    public static readonly DependencyProperty FovProperty =
        DependencyProperty.Register(nameof(Fov), typeof(double), typeof(PanoMediaElement),
            new PropertyMetadata(90.0));

    /// <summary>当前播放位置。外部设置时自动执行 Seek 并启动拖拽防护。</summary>
    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(nameof(Position), typeof(TimeSpan), typeof(PanoMediaElement),
            new PropertyMetadata(TimeSpan.Zero, OnPositionPropertyChanged));

    /// <summary>媒体总时长（只读）。</summary>
    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(TimeSpan), typeof(PanoMediaElement),
            new PropertyMetadata(TimeSpan.Zero));

    /// <summary>是否正在播放。</summary>
    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(PanoMediaElement),
            new PropertyMetadata(false));

    /// <summary>音量 [0.0, 1.0]，默认 1.0。</summary>
    public static readonly DependencyProperty VolumeProperty =
        DependencyProperty.Register(nameof(Volume), typeof(double), typeof(PanoMediaElement),
            new PropertyMetadata(1.0, OnVolumeChanged));

    /// <summary>是否静音。</summary>
    public static readonly DependencyProperty IsMutedProperty =
        DependencyProperty.Register(nameof(IsMuted), typeof(bool), typeof(PanoMediaElement),
            new PropertyMetadata(false, OnIsMutedChanged));

    /// <summary>播放速率，默认 1.0。</summary>
    public static readonly DependencyProperty PlaybackRateProperty =
        DependencyProperty.Register(nameof(PlaybackRate), typeof(double), typeof(PanoMediaElement),
            new PropertyMetadata(1.0, OnPlaybackRateChanged));

    /// <summary>Source 设置后是否自动播放，默认 true。</summary>
    public static readonly DependencyProperty AutoPlayProperty =
        DependencyProperty.Register(nameof(AutoPlay), typeof(bool), typeof(PanoMediaElement),
            new PropertyMetadata(true));

    /// <summary>是否循环播放。</summary>
    public static readonly DependencyProperty IsLoopingProperty =
        DependencyProperty.Register(nameof(IsLooping), typeof(bool), typeof(PanoMediaElement),
            new PropertyMetadata(false, OnIsLoopingChanged));

    /// <summary>视频原始宽度（只读）。</summary>
    public static readonly DependencyProperty NaturalVideoWidthProperty =
        DependencyProperty.Register(nameof(NaturalVideoWidth), typeof(int), typeof(PanoMediaElement),
            new PropertyMetadata(0));

    /// <summary>视频原始高度（只读）。</summary>
    public static readonly DependencyProperty NaturalVideoHeightProperty =
        DependencyProperty.Register(nameof(NaturalVideoHeight), typeof(int), typeof(PanoMediaElement),
            new PropertyMetadata(0));

    /// <summary>播放进度百分比 [0, 100]。可直接 TwoWay 绑定 Slider.Value，外部设置时自动 Seek + 防抖。</summary>
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(PanoMediaElement),
            new PropertyMetadata(0.0, OnProgressChanged));

    /// <inheritdoc cref="SourceProperty"/>
    public Uri? Source { get => (Uri?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    /// <inheritdoc cref="RotationXProperty"/>
    public double RotationX { get => (double)GetValue(RotationXProperty); set => SetValue(RotationXProperty, value); }
    /// <inheritdoc cref="RotationYProperty"/>
    public double RotationY { get => (double)GetValue(RotationYProperty); set => SetValue(RotationYProperty, value); }
    /// <inheritdoc cref="ZoomProperty"/>
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    /// <inheritdoc cref="FovProperty"/>
    public double Fov { get => (double)GetValue(FovProperty); set => SetValue(FovProperty, value); }
    /// <inheritdoc cref="PositionProperty"/>
    public TimeSpan Position { get => (TimeSpan)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    /// <inheritdoc cref="DurationProperty"/>
    public TimeSpan Duration { get => (TimeSpan)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    /// <inheritdoc cref="IsPlayingProperty"/>
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    /// <inheritdoc cref="VolumeProperty"/>
    public double Volume { get => (double)GetValue(VolumeProperty); set => SetValue(VolumeProperty, value); }
    /// <inheritdoc cref="IsMutedProperty"/>
    public bool IsMuted { get => (bool)GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }
    /// <inheritdoc cref="PlaybackRateProperty"/>
    public double PlaybackRate { get => (double)GetValue(PlaybackRateProperty); set => SetValue(PlaybackRateProperty, value); }
    /// <inheritdoc cref="AutoPlayProperty"/>
    public bool AutoPlay { get => (bool)GetValue(AutoPlayProperty); set => SetValue(AutoPlayProperty, value); }
    /// <inheritdoc cref="IsLoopingProperty"/>
    public bool IsLooping { get => (bool)GetValue(IsLoopingProperty); set => SetValue(IsLoopingProperty, value); }
    /// <inheritdoc cref="NaturalVideoWidthProperty"/>
    public int NaturalVideoWidth { get => (int)GetValue(NaturalVideoWidthProperty); private set => SetValue(NaturalVideoWidthProperty, value); }
    /// <inheritdoc cref="NaturalVideoHeightProperty"/>
    public int NaturalVideoHeight { get => (int)GetValue(NaturalVideoHeightProperty); private set => SetValue(NaturalVideoHeightProperty, value); }
    /// <inheritdoc cref="ProgressProperty"/>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    #endregion

    #region Events

    /// <summary>媒体打开完成。</summary>
    public event EventHandler? MediaOpened;
    /// <summary>播放结束。</summary>
    public event EventHandler? MediaEnded;
    /// <summary>媒体加载或播放失败。</summary>
    public event EventHandler<string>? MediaFailed;
    /// <summary>播放位置变化（已节流到 UI 线程）。</summary>
    public event EventHandler<TimeSpan>? PositionChanged;

    #endregion

    #region Public Methods

    /// <summary>播放命令。</summary>
    public ICommand PlayCommand { get; }
    /// <summary>暂停命令。</summary>
    public ICommand PauseCommand { get; }
    /// <summary>停止命令。</summary>
    public ICommand StopCommand { get; }
    /// <summary>重置命令（恢复 FOV/Zoom/RotationX/RotationY 为默认值）。</summary>
    public ICommand ResetCommand { get; }

    /// <summary>开始播放。</summary>
    public void Play()
    {
        _player?.Play();
        IsPlaying = true;
    }

    /// <summary>暂停播放。</summary>
    public void Pause()
    {
        _player?.Pause();
        IsPlaying = false;
    }

    /// <summary>停止并重置到起始位置。</summary>
    public void Stop()
    {
        if (_player != null)
        {
            _player.Pause();
            _player.PlaybackSession.Position = TimeSpan.Zero;
            IsPlaying = false;
            _isInternalPositionUpdate = true;
            Position = TimeSpan.Zero;
            _isInternalPositionUpdate = false;
        }
    }

    /// <summary>跳转到指定位置。</summary>
    public void Seek(TimeSpan position)
    {
        if (_player != null && position <= Duration)
        {
            _player.PlaybackSession.Position = position;
            _isInternalPositionUpdate = true;
            Position = position;
            _isInternalPositionUpdate = false;
        }
    }

    /// <summary>重置视角参数为默认值。</summary>
    public void Reset()
    {
        Fov = 90.0;
        Zoom = 0.5;
        RotationX = 0.5;
        RotationY = 0.5;
        StopInertia();
        if (!IsPlaying) RenderFrame();
    }

    #endregion

    #region Property Change Callbacks

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PanoMediaElement c && e.NewValue is Uri uri)
            c.ApplySource(uri);
    }

    /// <summary>
    /// Position 属性变更回调。外部设置时自动 Seek + 防抖。
    /// </summary>
    private static void OnPositionPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PanoMediaElement c && !c._isInternalPositionUpdate)
        {
            var position = (TimeSpan)e.NewValue;
            if (c._player != null && c.Duration.TotalSeconds > 0 && position <= c.Duration)
                c._player.PlaybackSession.Position = position;

            c._lastExternalSeekTick = Environment.TickCount64;

            // 同步 Progress
            c._isInternalPositionUpdate = true;
            if (c.Duration.TotalSeconds > 0)
                c.Progress = position.TotalSeconds / c.Duration.TotalSeconds * 100.0;
            c._isInternalPositionUpdate = false;
        }
    }

    /// <summary>
    /// Progress 属性变更回调。外部设置时计算目标位置并 Seek。
    /// </summary>
    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PanoMediaElement c && !c._isInternalPositionUpdate)
        {
            var percent = Math.Clamp((double)e.NewValue, 0.0, 100.0);
            if (c._player != null && c.Duration.TotalSeconds > 0)
            {
                var target = TimeSpan.FromSeconds(percent / 100.0 * c.Duration.TotalSeconds);
                c._player.PlaybackSession.Position = target;

                c._isInternalPositionUpdate = true;
                c.Position = target;
                c._isInternalPositionUpdate = false;
            }

            c._lastExternalSeekTick = Environment.TickCount64;
        }
    }

    private static void OnVolumeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PanoMediaElement c && c._player != null)
            c._player.Volume = Math.Clamp((double)e.NewValue, 0.0, 1.0);
    }

    private static void OnIsMutedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PanoMediaElement c && c._player != null)
            c._player.IsMuted = (bool)e.NewValue;
    }

    private static void OnPlaybackRateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PanoMediaElement c && c._player != null)
            c._player.PlaybackSession.PlaybackRate = (double)e.NewValue;
    }

    private static void OnIsLoopingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PanoMediaElement c && c._player != null)
            c._player.IsLoopingEnabled = (bool)e.NewValue;
    }

    #endregion

    #region Lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeRendering();
        if (ActualWidth > 0 && ActualHeight > 0)
            ResizeDrawingSurface(ActualWidth, ActualHeight);
        if (Source != null)
            ApplySource(Source);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CleanUp();
    }

    private void InitializeRendering()
    {
        // 从嵌入资源加载预编译着色器
        using (var stream = typeof(PanoMediaElement).Assembly
            .GetManifestResourceStream("Pano.cso"))
        {
            if (stream != null)
            {
                _shaderBytes = new byte[stream.Length];
                stream.ReadExactly(_shaderBytes);
            }
        }

        _canvasDevice = CanvasDevice.GetSharedDevice();

        var elementVisual = ElementCompositionPreview.GetElementVisual(this);
        _compositor = elementVisual.Compositor;
        _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(_compositor, _canvasDevice);

        _drawingSurface = _graphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(1, 1),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        var surfaceBrush = _compositor.CreateSurfaceBrush(_drawingSurface);
        surfaceBrush.Stretch = CompositionStretch.Fill;

        _spriteVisual = _compositor.CreateSpriteVisual();
        _spriteVisual.Brush = surfaceBrush;
        _spriteVisual.Size = new Vector2((float)ActualWidth, (float)ActualHeight);

        ElementCompositionPreview.SetElementChildVisual(this, _spriteVisual);

        _player = new MediaPlayer();
        _player.IsVideoFrameServerEnabled = true;
        _player.Volume = Volume;
        _player.IsMuted = IsMuted;
        _player.IsLoopingEnabled = IsLooping;
        _player.PlaybackSession.PlaybackRate = PlaybackRate;
        _player.VideoFrameAvailable += OnVideoFrameAvailable;
        _player.MediaOpened += OnPlayerMediaOpened;
        _player.MediaEnded += OnPlayerMediaEnded;
        _player.MediaFailed += OnPlayerMediaFailed;
        _player.PlaybackSession.PositionChanged += OnPlaybackPositionChanged;
    }

    private void CleanUp()
    {
        if (_player != null)
        {
            _player.PlaybackSession.PositionChanged -= OnPlaybackPositionChanged;
            _player.VideoFrameAvailable -= OnVideoFrameAvailable;
            _player.MediaOpened -= OnPlayerMediaOpened;
            _player.MediaEnded -= OnPlayerMediaEnded;
            _player.MediaFailed -= OnPlayerMediaFailed;
            _player.Dispose();
            _player = null;
        }
        _videoFrameBuffer?.Dispose();
        _videoFrameBuffer = null;
    }

    #endregion

    #region Source Handling

    private void ApplySource(Uri uri)
    {
        if (_player == null) return;
        _player.Source = MediaSource.CreateFromUri(uri);
        if (AutoPlay) Play();
    }

    #endregion

    #region Media Events

    private void OnPlaybackPositionChanged(MediaPlaybackSession sender, object args)
    {
        // 外部 Seek 后 300ms 内抑制回写，避免播放器报告的位置覆盖滑块
        if (Environment.TickCount64 - _lastExternalSeekTick < 300) return;

        DispatcherQueue?.TryEnqueue(() =>
        {
            if (Environment.TickCount64 - _lastExternalSeekTick < 300) return;
            _isInternalPositionUpdate = true;
            Position = sender.Position;
            if (Duration.TotalSeconds > 0)
                Progress = sender.Position.TotalSeconds / Duration.TotalSeconds * 100.0;
            _isInternalPositionUpdate = false;
            PositionChanged?.Invoke(this, sender.Position);
        });
    }

    private void OnPlayerMediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            _videoWidth = (int)sender.PlaybackSession.NaturalVideoWidth;
            _videoHeight = (int)sender.PlaybackSession.NaturalVideoHeight;
            if (_videoWidth == 0 || _videoHeight == 0)
            {
                var scale = XamlRoot?.RasterizationScale ?? 1.0;
                _videoWidth = Math.Max(1, (int)(ActualWidth * scale));
                _videoHeight = Math.Max(1, (int)(ActualHeight * scale));
            }

            NaturalVideoWidth = _videoWidth;
            NaturalVideoHeight = _videoHeight;
            Duration = sender.PlaybackSession.NaturalDuration;

            _videoFrameBuffer?.Dispose();
            _videoFrameBuffer = new CanvasRenderTarget(
                _canvasDevice!, _videoWidth, _videoHeight, 96f,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied);

            MediaOpened?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnPlayerMediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            IsPlaying = false;
            MediaEnded?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnPlayerMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            IsPlaying = false;
            MediaFailed?.Invoke(this, args.ErrorMessage ?? "Unknown error");
        });
    }

    #endregion

    #region Rendering

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        DispatcherQueue?.TryEnqueue(RenderFrame);
    }

    private void RenderFrame()
    {
        if (_drawingSurface == null || _shaderBytes == null ||
            _videoFrameBuffer == null || _player == null) return;

        try
        {
            _player.CopyFrameToVideoSurface(_videoFrameBuffer);

            using var effect = new PixelShaderEffect(_shaderBytes)
            {
                Source1 = _videoFrameBuffer,
                Source1BorderMode = EffectBorderMode.Hard
            };
            effect.Properties["panoParams"] = new Vector4(
                (float)RotationX, (float)RotationY, (float)Zoom, (float)Fov);
            effect.Properties["aspectRatio"] = GetAspectRatio();

            using var ds = CanvasComposition.CreateDrawingSession(_drawingSurface);
            ds.DrawImage(effect,
                new Windows.Foundation.Rect(0, 0, _drawingSurface.Size.Width, _drawingSurface.Size.Height),
                new Windows.Foundation.Rect(0, 0, _videoWidth, _videoHeight));
        }
        catch
        {
            // Ignore rendering errors during transitions
        }
    }

    private float GetAspectRatio()
    {
        float w = (float)ActualWidth;
        float h = (float)ActualHeight;
        return (w > 0 && h > 0) ? w / h : 1.778f;
    }

    #endregion

    #region Size Changed

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var height = e.NewSize.Height;
        if (width <= 0 || height <= 0) return;

        if (_spriteVisual != null)
            _spriteVisual.Size = new Vector2((float)width, (float)height);

        ResizeDrawingSurface(width, height);
        RenderFrame();
    }

    private void ResizeDrawingSurface(double width, double height)
    {
        if (_drawingSurface == null) return;
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        _drawingSurface.Resize(new Windows.Graphics.SizeInt32
        {
            Width = Math.Max(1, (int)(width * scale)),
            Height = Math.Max(1, (int)(height * scale))
        });
    }

    #endregion

    #region Pointer Interaction

    /// <summary>每 16ms 的速度保留比，值越接近 1 滑行越远。 0.95 ≈ 中速拖拽 2~3 秒停止。</summary>
    private const double InertiaFriction = 0.95;
    private const double InertiaStopThreshold = 0.0000005;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPointerPressed = true;
        _lastPointerPos = e.GetCurrentPoint(this).Position;
        _lastPointerTick = Environment.TickCount64;
        _velocityX = 0;
        _velocityY = 0;
        StopInertia();
        CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerPressed) return;

        var pos = e.GetCurrentPoint(this).Position;
        var now = Environment.TickCount64;
        var dt = Math.Max(1, now - _lastPointerTick);

        var dx = pos.X - _lastPointerPos.X;
        var dy = pos.Y - _lastPointerPos.Y;

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 用指数平滑稳定速度，避免单帧抖动导致初速不准
        var instantVx = dx / w * 0.5 / dt;
        var instantVy = -dy / h * 0.5 / dt;
        const double alpha = 0.4;
        _velocityX = _velocityX * (1 - alpha) + instantVx * alpha;
        _velocityY = _velocityY * (1 - alpha) + instantVy * alpha;

        var rx = (float)(RotationX + dx / w * 0.5);
        var ry = (float)(RotationY - dy / h * 0.5);

        if (rx > 1.0f) rx -= 1.0f;
        if (rx < 0.0f) rx += 1.0f;
        ry = Math.Clamp(ry, 0.01f, 0.99f);

        RotationX = rx;
        RotationY = ry;
        _lastPointerPos = pos;
        _lastPointerTick = now;

        if (!IsPlaying) RenderFrame();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPointerPressed = false;
        ReleasePointerCapture(e.Pointer);
        StartInertia();
    }

    private void StartInertia()
    {
        var speed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);
        if (speed < InertiaStopThreshold) return;

        _lastPointerTick = Environment.TickCount64;
        _isInertiaRunning = true;
        CompositionTarget.Rendering += OnInertiaRendering;
    }

    private void StopInertia()
    {
        if (_isInertiaRunning)
        {
            _isInertiaRunning = false;
            CompositionTarget.Rendering -= OnInertiaRendering;
        }
    }

    private void OnInertiaRendering(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = Math.Max(1, now - _lastPointerTick);
        _lastPointerTick = now;

        // 本帧位移 = 速度 × 时间
        var moveX = _velocityX * dt;
        var moveY = _velocityY * dt;

        // 指数衰减：快时减得多、慢时减得少，自然滑行感
        var decay = Math.Pow(InertiaFriction, dt / 16.0);
        _velocityX *= decay;
        _velocityY *= decay;

        if (Math.Abs(_velocityX) < InertiaStopThreshold &&
            Math.Abs(_velocityY) < InertiaStopThreshold)
        {
            StopInertia();
            return;
        }

        var rx = (float)(RotationX + moveX);
        var ry = (float)(RotationY + moveY);

        if (rx > 1.0f) rx -= 1.0f;
        if (rx < 0.0f) rx += 1.0f;
        ry = Math.Clamp(ry, 0.01f, 0.99f);

        RotationX = rx;
        RotationY = ry;

        if (!IsPlaying) RenderFrame();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        Zoom = Math.Clamp(Zoom + (delta > 0 ? 0.05 : -0.05), 0.1, 2.0);
        if (!IsPlaying) RenderFrame();
    }

    #endregion
}

/// <summary>
/// 简单的 ICommand 实现。
/// </summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
