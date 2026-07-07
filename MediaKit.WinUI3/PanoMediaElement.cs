using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Interactions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using System.Reflection;
using System.Windows.Input;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace MediaKit.WinUI3;

/// <summary>
/// 360 全景视频播放控件。
/// 基于 CanvasAnimatedControl + Win2D PixelShaderEffect 实现球面投影渲染。
/// 内置鼠标拖拽旋转和滚轮缩放交互。
/// </summary>
public sealed class PanoMediaElement : Panel
{
    private CanvasAnimatedControl? _canvas;
    private PixelShaderEffect? _panoEffect;
    private CanvasRenderTarget? _frameBuffer;
    private volatile bool _frameAvailable;   // 有新视频帧待拉取（媒体线程置位，渲染线程消费）
    private volatile bool _needRebuild;      // 视频尺寸/设备变化，需在渲染线程重建帧缓冲

    // 视角参数镜像，供渲染线程读取（避免非 UI 线程访问依赖属性）
    private volatile float _viewRotationX = 0.5f;
    private volatile float _viewRotationY = 0.5f;
    private volatile float _viewZoom = 0.5f;
    private volatile float _viewFov = 90f;
    private volatile bool _isParamsUpdatting = false;

    private MediaPlayer? _player;
    private int _videoWidth;
    private int _videoHeight;
    private bool _isInternalPositionUpdate;
    private DateTimeOffset _lastExternalSeekTime; // 最近一次外部 Seek 时间戳（回写防抖）

#if DEBUG
    private readonly System.Diagnostics.Stopwatch _fpsStopwatch = System.Diagnostics.Stopwatch.StartNew();
    private int _frameCount;
#endif

    // 拖拽 + 帧循环惯性
    private bool _isPointerPressed;
    private Point _lastPointerPos;
    private DateTimeOffset _lastTime;
    private double _velocityX; // 旋转量/毫秒
    private double _velocityY; // 旋转量/毫秒
    private volatile bool _inertiaRunning;
    private DateTimeOffset _lastInertiaTime;

    /// <summary>初始化 PanoMediaElement 实例。</summary>
    public PanoMediaElement()
    {
        PlayCommand = new RelayCommand(Play);
        PauseCommand = new RelayCommand(Pause);
        StopCommand = new RelayCommand(Stop);
        ResetCommand = new RelayCommand(Reset);

        SyncViewParams();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
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
            new PropertyMetadata(false, OnIsPlayingChanged));

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
        StopInertia();
        _velocityX = 0;
        _velocityY = 0;
        _isParamsUpdatting = true;
        Fov = 90.0;
        Zoom = 0.5;
        RotationX = 0.5;
        RotationY = 0.5;
        _isParamsUpdatting = false;
        SyncViewParams();
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

    /// <summary>视角参数变更时同步镜像字段。</summary>
    private static void OnViewParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PanoMediaElement c)
            c.SyncViewParams();
    }

    /// <summary>视角依赖属性同步到镜像字段，并补画一帧。</summary>
    private void SyncViewParams()
    {
        if (_isParamsUpdatting)
            return;
        _viewRotationX = (float)RotationX;
        _viewRotationY = (float)RotationY;
        _viewZoom = (float)Zoom;
        _viewFov = (float)Fov;
        _canvas?.Invalidate();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PanoMediaElement c)
        {
            c._canvas!.Paused = !(bool)e.NewValue && !c._inertiaRunning;
        }
    }

    #endregion

    #region Lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeRendering();
        if (Source != null)
            ApplySource(Source);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CleanUp();
    }

    /// <summary>
    /// 测量：Panel 基类不实现布局（默认返回 0），这里手动重写，
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double childW = 0, childH = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            childW = Math.Max(childW, child.DesiredSize.Width);
            childH = Math.Max(childH, child.DesiredSize.Height);
        }
        // 可用尺寸为无穷时（如置于 ScrollViewer/StackPanel），退回子元素期望尺寸，避免返回无穷引发异常
        return new Size(
            double.IsInfinity(availableSize.Width) ? childW : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? childH : availableSize.Height);
    }

    /// <summary>
    /// 排列：把唯一的子元素放置到填满整个可用区域。
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(0, 0, finalSize.Width, finalSize.Height);
        foreach (var child in Children)
        {
            child.Arrange(rect);
        }
        return finalSize;
    }

    private void InitializeRendering()
    {
        if (_canvas != null) return;

        _canvas = new CanvasAnimatedControl
        {
            ClearColor = Colors.Black,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsFixedTimeStep = false,
            Paused = true
        };
        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnDraw;
        Children.Add(_canvas);

        _player = new MediaPlayer
        {
            IsVideoFrameServerEnabled = true,
            Volume = Volume,
            IsMuted = IsMuted,
            IsLoopingEnabled = IsLooping
        };
        _player.PlaybackSession.PlaybackRate = PlaybackRate;
        _player.VideoFrameAvailable += OnVideoFrameAvailable;
        _player.MediaOpened += OnPlayerMediaOpened;
        _player.MediaEnded += OnPlayerMediaEnded;
        _player.MediaFailed += OnPlayerMediaFailed;
        _player.PlaybackSession.PositionChanged += OnPlaybackPositionChanged;
    }

    private void CleanUp()
    {
        StopInertia();

        // 先移除画布停止渲染循环，再释放播放器与帧缓冲，避免渲染线程仍在拉帧/绘制时资源被销毁
        if (_canvas != null)
        {
            _canvas.Paused = true;
            _canvas.CreateResources -= OnCreateResources;
            _canvas.Draw -= OnDraw;
            _canvas.RemoveFromVisualTree();
            _canvas = null;
        }

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
        _frameBuffer?.Dispose();
        _frameBuffer = null;
    }

    /// <summary>
    /// 创建/重建设备资源（首次加载与设备丢失后由 CanvasAnimatedControl 触发）。
    /// 着色器为设备无关，仅建一次；帧缓冲为设备绑定纹理，每次都重建。
    /// </summary>
    private void OnCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
        if (_panoEffect == null)
        {
            using var stream = typeof(PanoMediaElement).Assembly.GetManifestResourceStream("Pano.cso");
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

        _needRebuild = false;
        RebuildBuffer(sender.Device);
    }

    /// <summary>
    /// 重建帧缓冲。
    /// </summary>
    private void RebuildBuffer(CanvasDevice device)
    {
        if (_videoWidth == 0 || _videoHeight == 0)
            return;
        if (_frameBuffer != null)
        {
            // 设备与尺寸都未变则复用
            if (_frameBuffer.Device == device
                && (int)_frameBuffer.SizeInPixels.Width == _videoWidth
                && (int)_frameBuffer.SizeInPixels.Height == _videoHeight)
                return;
            _frameBuffer.Dispose();
            _frameBuffer = null;
        }
        _frameBuffer = new CanvasRenderTarget(
            device, _videoWidth, _videoHeight, 96f,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied);
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

            _needRebuild = true;
            _canvas?.Invalidate();

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

    /// <summary>视频新帧到达（媒体线程）：仅置标记并请求重绘，真正的 CopyFrameToVideoSurface
    /// 延后到渲染线程 OnDraw 执行，确保拉帧与绘制都在渲染线程单线程访问 GPU，避免跨线程并发导致访问违例。</summary>
    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        _frameAvailable = true;
        _canvas?.Invalidate();
    }

    /// <summary>渲染线程回调：帧缓冲经全景着色器绘制到当前帧。</summary>
    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        if (_needRebuild)
        {
            _needRebuild = false;
            RebuildBuffer(sender.Device);
        }

        if (_frameBuffer == null)
            return;

        var size = sender.Size;
        if (size.Width <= 0 || size.Height <= 0 || _videoWidth == 0 || _videoHeight == 0) return;

        OnInertia();

        var player = _player;
        if (_frameAvailable && player != null)
        {
            _frameAvailable = false;
            player.CopyFrameToVideoSurface(_frameBuffer);
        }

        var effect = _panoEffect;
        if (effect == null) return;

        effect.Source1 = _frameBuffer;

        #region 预计算仅依赖视角/FOV 的量（每帧一次），避免在 shader 中逐像素重复计算超越函数
        float scaleX = (float)(size.Width / _videoWidth);
        float scaleY = (float)(size.Height / _videoHeight);
        float aspect = (float)(size.Width / size.Height);
        float hfovRad = _viewFov * (float)(Math.PI / 180.0);
        float tanHalfH = MathF.Tan(hfovRad * 0.5f);
        float vfovRad = 2f * MathF.Atan(tanHalfH / aspect);
        float tanHalfV = MathF.Tan(vfovRad * 0.5f);
        float pitch = (_viewRotationY - 0.5f) * MathF.PI;
        float yaw = (_viewRotationX - 0.5f) * 2f * MathF.PI;
        #endregion

        effect.Properties["viewParams"] = new Vector4(scaleX, scaleY, _viewZoom, 0f);
        effect.Properties["fovTan"] = new Vector4(tanHalfH, tanHalfV, 0f, 0f);
        effect.Properties["rotSinCos"] = new Vector4(
            MathF.Sin(pitch), MathF.Cos(pitch), MathF.Sin(yaw), MathF.Cos(yaw));

        args.DrawingSession.DrawImage(effect);

#if DEBUG
        _frameCount++;
        if (_fpsStopwatch.Elapsed.TotalMilliseconds >= 1000)
        {
            System.Diagnostics.Debug.WriteLine(
                $"真实帧率: {_frameCount * 1000.0 / _fpsStopwatch.Elapsed.TotalMilliseconds:f1} fps");
            _frameCount = 0;
            _fpsStopwatch.Restart();
        }
#endif
    }

    #endregion

    #region Pointer Interaction

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _canvas!.Paused = false;
        _isPointerPressed = true;
        _lastPointerPos = e.GetCurrentPoint(this).Position;
        _lastTime = DateTimeOffset.Now;
        _velocityX = 0;
        _velocityY = 0;
        StopInertia();
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

        _isParamsUpdatting = true;
        RotationX = rx;
        RotationY = ry;
        _isParamsUpdatting = false;
        this.SyncViewParams();

        _lastPointerPos = pos;
        _lastTime = now;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPointerPressed = false;
        ReleasePointerCapture(e.Pointer);

        var speed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);
        if (speed > 0.0001)
            StartInertia();
        else if (!IsPlaying)
            _canvas!.Paused = true;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        Zoom = Math.Clamp(Zoom + (delta > 0 ? 0.05 : -0.05), 0.1, 2.0);
    }

    #endregion

    #region Inertia

    /// <summary>启动帧循环惯性</summary>
    private void StartInertia()
    {
        if (_inertiaRunning) return;
        _inertiaRunning = true;
        _lastInertiaTime = DateTimeOffset.Now;
        _canvas!.Paused = false;
    }

    private void OnInertia()
    {
        if (!_inertiaRunning) return;

        var now = DateTimeOffset.Now;
        var dt = (now - _lastInertiaTime).TotalMilliseconds;
        _lastInertiaTime = now;
        if (dt <= 0) return;

        var decay = Math.Pow(0.95, dt / 16.0);
        _velocityX *= decay;
        _velocityY *= decay;

        var rx = _viewRotationX + _velocityX * dt;
        var ry = _viewRotationY + _velocityY * dt;

        if (rx > 1.0) rx -= 1.0;
        if (rx < 0.0) rx += 1.0;
        ry = Math.Clamp(ry, 0.01, 0.99);

        _viewRotationX = (float)rx;
        _viewRotationY = (float)ry;
        DispatcherQueue?.TryEnqueue(() =>
        {
            _isParamsUpdatting = true;
            RotationX = rx;
            RotationY = ry;
            _isParamsUpdatting = false;
        });

        if (Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY) < 0.0000005)
        {
            StopInertia();
        }
    }

    /// <summary>停止帧循环惯性。回到 UI 线程按需关闭渲染循环。</summary>
    private void StopInertia()
    {
        _inertiaRunning = false;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!IsPlaying)
                _canvas!.Paused = true;
        });
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
