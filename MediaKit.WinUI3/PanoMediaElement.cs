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
using Microsoft.UI.Composition.Interactions;
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
public sealed class PanoMediaElement : Panel, IInteractionTrackerOwner
{
    // Composition
    private Compositor? _compositor;
    private SpriteVisual? _spriteVisual;
    private CompositionDrawingSurface? _drawingSurface;
    private CompositionGraphicsDevice? _graphicsDevice;

    // Win2D
    private CanvasDevice? _canvasDevice;
    private CanvasRenderTarget? _videoFrameBuffer;
    private PixelShaderEffect? _panoEffect;

    // Media
    private MediaPlayer? _player;
    private int _videoWidth;
    private int _videoHeight;
    private bool _isInternalPositionUpdate;
    private DateTimeOffset _lastExternalSeekTime; // 防抖：最近一次外部 Seek 的时间戳

#if DEBUG
    // FPS
    private int _frameCount;
    private DateTimeOffset _lastFpsTick;
#endif

    // Interaction
    private const float TrackerScale = 5000f;
    private InteractionTracker? _tracker;
    private Vector3 _lastTrackerPosition;
    private bool _isPointerPressed;
    private Windows.Foundation.Point _lastPointerPos;
    private DateTimeOffset _lastTime;
    private double _velocityX;
    private double _velocityY;
    private bool _suppressRender;

    /// <summary>初始化 PanoMediaElement 实例。</summary>
    public PanoMediaElement()
    {
        PlayCommand = new RelayCommand(Play);
        PauseCommand = new RelayCommand(Pause);
        StopCommand = new RelayCommand(Stop);
        ResetCommand = new RelayCommand(Reset);

        Background = new SolidColorBrush(Colors.Transparent);
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
            new PropertyMetadata(0.5, OnViewParamChanged));

    /// <summary>垂直旋转 [0,1]，0.5 为水平视线。</summary>
    public static readonly DependencyProperty RotationYProperty =
        DependencyProperty.Register(nameof(RotationY), typeof(double), typeof(PanoMediaElement),
            new PropertyMetadata(0.5, OnViewParamChanged));

    /// <summary>缩放级别，默认 0.5。</summary>
    public static readonly DependencyProperty ZoomProperty =
        DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(PanoMediaElement),
            new PropertyMetadata(0.5, OnViewParamChanged));

    /// <summary>视场角（度），默认 90。</summary>
    public static readonly DependencyProperty FovProperty =
        DependencyProperty.Register(nameof(Fov), typeof(double), typeof(PanoMediaElement),
            new PropertyMetadata(90.0, OnViewParamChanged));

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
        _suppressRender = true;
        Fov = 90.0;
        Zoom = 0.5;
        RotationX = 0.5;
        RotationY = 0.5;
        _suppressRender = false;
        if (_tracker != null)
        {
            _lastTrackerPosition = _tracker.Position;
            _tracker.TryUpdatePosition(_tracker.Position);
        }
        RenderFrame();
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

            c._lastExternalSeekTime = DateTimeOffset.Now;

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

            c._lastExternalSeekTime = DateTimeOffset.Now;
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

    private static void OnViewParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PanoMediaElement c && !c._suppressRender)
            c.RenderFrame();
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
        // 从嵌入资源加载预编译着色器，创建 PixelShaderEffect（仅一次）
        using (var stream = typeof(PanoMediaElement).Assembly
            .GetManifestResourceStream("Pano.cso"))
        {
            if (stream != null)
            {
                var shaderBytes = new byte[stream.Length];
                stream.ReadExactly(shaderBytes);
                _panoEffect = new PixelShaderEffect(shaderBytes)
                {
                    Source1BorderMode = EffectBorderMode.Hard
                };
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

        // InteractionTracker：系统级惯性动画
        _tracker = InteractionTracker.CreateWithOwner(_compositor, this);
        _tracker.MinPosition = new Vector3(float.MinValue);
        _tracker.MaxPosition = new Vector3(float.MaxValue);
        _tracker.PositionInertiaDecayRate = new Vector3(0.8f, 0.8f, 0f);

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
        _panoEffect?.Dispose();
        _panoEffect = null;
        _videoFrameBuffer?.Dispose();
        _videoFrameBuffer = null;
        _tracker = null;
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
        if ((DateTimeOffset.Now - _lastExternalSeekTime).TotalMilliseconds < 300) return;

        DispatcherQueue?.TryEnqueue(() =>
        {
            if ((DateTimeOffset.Now - _lastExternalSeekTime).TotalMilliseconds < 300) return;
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
        DispatcherQueue?.TryEnqueue(() =>
        {
            _player?.CopyFrameToVideoSurface(_videoFrameBuffer);
            RenderFrame();
        });
    }

    private void RenderFrame()
    {
        var buffer = _videoFrameBuffer;
        if (_drawingSurface == null || _panoEffect == null ||
            buffer == null || _player == null) return;

        try
        {
            _panoEffect.Source1 = buffer;
            _panoEffect.Properties["panoParams"] = new Vector4(
                (float)RotationX, (float)RotationY, (float)Zoom, (float)Fov);
            _panoEffect.Properties["view"] = new Vector3(
                (float)(this.ActualWidth / _videoWidth),
                (float)(this.ActualHeight / _videoHeight),
                (float)(this.ActualWidth / this.ActualHeight));

            using var ds = CanvasComposition.CreateDrawingSession(_drawingSurface);
            ds.DrawImage(_panoEffect);

#if DEBUG
            _frameCount++;
            var now = DateTimeOffset.Now;
            if ((now - _lastFpsTick).TotalMilliseconds >= 1000)
            {
                System.Diagnostics.Debug.WriteLine($"FPS: {_frameCount * 1000.0 / (now - _lastFpsTick).TotalMilliseconds:f1}");
                _frameCount = 0;
                _lastFpsTick = now;
            }
#endif
        }
        catch
        {
            // Ignore rendering errors during transitions
        }
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

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPointerPressed = true;
        _lastPointerPos = e.GetCurrentPoint(this).Position;
        _lastTime = DateTimeOffset.Now;
        _velocityX = 0;
        _velocityY = 0;
        // 停止正在进行的惯性
        if (_tracker != null)
        {
            _lastTrackerPosition = _tracker.Position;
            _tracker.TryUpdatePosition(_tracker.Position);
        }
        CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerPressed) return;

        var pos = e.GetCurrentPoint(this).Position;
        var now = DateTimeOffset.Now;
        var dt = Math.Max(1, (now - _lastTime).TotalMilliseconds);

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

        _suppressRender = true;
        RotationX = rx;
        RotationY = ry;
        _suppressRender = false;
        _lastPointerPos = pos;
        _lastTime = now;

        RenderFrame();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPointerPressed = false;
        ReleasePointerCapture(e.Pointer);

        // 注入速度到 InteractionTracker，由系统处理惯性
        var speed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);
        if (speed > 0.0001 && _tracker != null)
        {
            _lastTrackerPosition = _tracker.Position;
            _tracker.TryUpdatePositionWithAdditionalVelocity(
                new Vector3((float)(_velocityX * 1000 * TrackerScale),
                            (float)(_velocityY * 1000 * TrackerScale), 0));
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        Zoom = Math.Clamp(Zoom + (delta > 0 ? 0.05 : -0.05), 0.1, 2.0);
    }

    #endregion

    #region IInteractionTrackerOwner

    void IInteractionTrackerOwner.ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
    {
        var delta = args.Position - _lastTrackerPosition;
        _lastTrackerPosition = args.Position;

        if (Math.Abs(delta.X) < 0.0001 && Math.Abs(delta.Y) < 0.0001) return;

        DispatcherQueue?.TryEnqueue(() =>
        {
            var rx = RotationX + delta.X / TrackerScale;
            var ry = RotationY + delta.Y / TrackerScale;

            if (rx > 1.0) rx -= 1.0;
            if (rx < 0.0) rx += 1.0;
            ry = Math.Clamp(ry, 0.01, 0.99);

            _suppressRender = true;
            RotationX = rx;
            RotationY = ry;
            _suppressRender = false;

            RenderFrame();
        });
    }

    void IInteractionTrackerOwner.CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args) { }
    void IInteractionTrackerOwner.IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args) { }
    void IInteractionTrackerOwner.InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args) { }
    void IInteractionTrackerOwner.InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args) { }
    void IInteractionTrackerOwner.RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args) { }

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
