using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.VisualTree;
using SkiaSharp;

namespace MediaKit.Avalonia.Effects;

/// <summary>
/// 基于 SkSL 的像素着色器效果基类。
/// SkSL 着色器约定以下名称（可不设置）：
///   uniform float2 iResolution;  // 视口设备像素尺寸
///   uniform float2 iSourceSize;  // 源图像原始像素尺寸
///   uniform shader iImage;       // 内容着色器
/// </summary>
[TypeConverter(typeof(ShaderEffectConverter))]
public class ShaderEffect : AvaloniaObject
{
    public static readonly AttachedProperty<ShaderEffect?> EffectProperty =
        AvaloniaProperty.RegisterAttached<ShaderEffect, Control, ShaderEffect?>("Effect");

    public static readonly DirectProperty<ShaderEffect, Uri?> InputProperty =
        AvaloniaProperty.RegisterDirect<ShaderEffect, Uri?>(
            nameof(Input), o => o.Input, (o, v) => o.Input = v);

    static ShaderEffect()
    {
        EffectProperty.Changed.AddClassHandler<Control>(OnEffectChanged);
    }

    private static void OnEffectChanged(Control target, AvaloniaPropertyChangedEventArgs e)
    {
        (e.OldValue as ShaderEffect)?.Detach();
        (e.NewValue as ShaderEffect)?.Attach(target);
    }

    public static ShaderEffect? GetEffect(Control c) => c.GetValue(EffectProperty);
    public static void SetEffect(Control c, ShaderEffect? v) => c.SetValue(EffectProperty, v);

    private SKRuntimeEffect? _skEffect;
    private readonly ConcurrentDictionary<string, object> _params = new();
    private Handler? _handler;
    private CompositionCustomVisual? _customVisual;
    private volatile bool _isAnimating;
    private readonly Stopwatch _stopwatch = new();
    private int _inputVersion;

    // Render cache (accessed from composition thread)
    private SKRectI _lastPixelBounds;
    private int _cachedInputVersion = -1;
    private SKImage? _gpuTextureImage;
    private SKShader? _cachedInputShader;
    private int _sourceWidth, _sourceHeight;
    private SKImage? _cachedSnapshotImage;
    private SKShader? _cachedSnapshotShader;
    private bool _snapshotDirty = true;
    private int _snapshotSkipFrames;
    private SKRuntimeEffectUniforms? _cachedUniforms;
    private SKRuntimeEffectChildren? _cachedChildren;
    private SKPaint? _cachedPaint;
#if DEBUG
    private int _frameCount;
    private double _lastFpsTime;
    private double _fps;
#endif

    public Control? Target { get; private set; }
    public Uri Source { get; }

    private Uri? _input;
    public Uri? Input
    {
        get => _input;
        set
        {
            if (SetAndRaise(InputProperty, ref _input, value))
            {
                _inputVersion++;
                _customVisual?.SendHandlerMessage(Handler.InvalidateMessage);
            }
        }
    }

    public double GetCurrentTime() => _stopwatch.Elapsed.TotalSeconds;

    public void StartAnimation()
    {
        _isAnimating = true;
        _stopwatch.Start();
        _customVisual?.SendHandlerMessage(Handler.StartMessage);
    }

    /// <summary>
    /// 由 Composition 线程调用，通知动画已自然结束。
    /// </summary>
    private void OnAnimationStopped()
    {
        _isAnimating = false;
        _stopwatch.Stop();
    }

    protected object this[string name]
    {
        get => _params.TryGetValue(name, out var v) ? v : 0f;
        set
        {
            ValidateUniformName(name);
            _params[name] = value;
            if (!_isAnimating)
                _customVisual?.SendHandlerMessage(Handler.InvalidateMessage);
        }
    }

    private void ValidateUniformName(string name)
    {
        if (_skEffect == null) return;
        var uniforms = _skEffect.Uniforms;
        for (int i = 0; i < uniforms.Count; i++)
            if (uniforms[i] == name) return;
        throw new ArgumentException($"Uniform '{name}' is not defined in shader '{Source}'.");
    }

    public ShaderEffect(Uri source)
    {
        Source = source;
        LoadShader(source);
    }

    public ShaderEffect(string skslCode)
    {
        Source = new Uri("memory:///inline.sksl");
        _skEffect = SKRuntimeEffect.CreateShader(skslCode, out var errors);
        if (_skEffect == null)
            throw new InvalidOperationException($"Shader compile error: {errors}");
    }

    private void LoadShader(Uri source)
    {
        string sksl = LoadSkslFromUri(source);
        _skEffect = SKRuntimeEffect.CreateShader(sksl, out var errors);
        if (_skEffect == null)
            throw new InvalidOperationException($"Shader compile error: {errors}");
    }

    private static string LoadSkslFromUri(Uri source)
    {
        if (source.Scheme == "avares")
        {
            using var stream = AssetLoader.Open(source);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        string path;
        if (source.Scheme == "file")
            path = source.LocalPath;
        else if (Path.IsPathRooted(source.OriginalString))
            path = source.OriginalString;
        else
            throw new InvalidOperationException(
                $"Unsupported URI scheme '{source.Scheme}'. Use avares:// or a local file path.");

        if (!File.Exists(path))
            throw new FileNotFoundException($"Shader file not found: {path}");

        return File.ReadAllText(path);
    }

    #region Lifecycle

    private void Attach(Control target)
    {
        Target = target;

        if (TopLevel.GetTopLevel(target) != null)
            AttachCompositor();
        else
            target.AttachedToVisualTree += OnAttachedToVisualTree;

        target.DetachedFromVisualTree += OnDetachedFromVisualTree;
        OnAttached(target);
    }

    private void Detach()
    {
        if (Target != null)
        {
            OnDetaching();
            Target.AttachedToVisualTree -= OnAttachedToVisualTree;
            Target.DetachedFromVisualTree -= OnDetachedFromVisualTree;
            DetachCompositor();
        }
        // _skEffect / _cachedUniforms / _cachedChildren / _cachedPaint 属于效果固有状态，
        // 跨 re-attach 复用，不在此处 Dispose（销毁 _skEffect 会导致 re-attach 后渲染失效）；
        // 效果对象被彻底丢弃时，由 SkiaSharp 各自的 finalizer 回收。
        // 仅重置版本号，确保 re-attach 后强制重建 GPU 纹理。
        _cachedInputVersion = -1;
        Target = null;
    }

    internal bool _pendingAnimate;

    private void AttachCompositor()
    {
        if (Target == null) return;
        var targetVisual = ElementComposition.GetElementVisual(Target);
        if (targetVisual == null) return;

        // 在创建 Visual 前激活动画状态，确保首次 OnRender 中自愈机制生效
        if (_pendingAnimate)
        {
            _pendingAnimate = false;
            _isAnimating = true;
            _stopwatch.Start();
        }

        _handler = new Handler(this);
        _customVisual = targetVisual.Compositor.CreateCustomVisual(_handler);
        UpdateVisualSize();
        ElementComposition.SetElementChildVisual(Target, _customVisual);

        if (_isAnimating)
            _customVisual.SendHandlerMessage(Handler.StartMessage);

        Target.SizeChanged += OnTargetSizeChanged;
        _snapshotSkipFrames = 1;
    }

    private void DetachCompositor()
    {
        if (Target == null) return;

        // 停止动画，确保合成器帧回调不再续注册
        _isAnimating = false;
        _stopwatch.Stop();

        Target.SizeChanged -= OnTargetSizeChanged;

        if (_customVisual != null)
            ElementComposition.SetElementChildVisual(Target, null);
        _customVisual = null;
        _handler = null;
        _cachedSnapshotShader?.Dispose();
        _cachedSnapshotImage?.Dispose();
        _cachedSnapshotShader = null;
        _cachedSnapshotImage = null;
        _snapshotDirty = true;
        _snapshotSkipFrames = 0;
    }

    protected virtual void OnAttached(Control target) { }
    protected virtual void OnDetaching() { }

    private void OnAttachedToVisualTree(object? s, VisualTreeAttachmentEventArgs e)
    {
        AttachCompositor();
    }

    private void OnDetachedFromVisualTree(object? s, VisualTreeAttachmentEventArgs e)
    {
        DetachCompositor();
    }

    #endregion

    private void OnTargetSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateVisualSize();

    private void UpdateVisualSize()
    {
        if (_customVisual == null || Target == null) return;
        _customVisual.Size = new Vector(Target.Bounds.Width, Target.Bounds.Height);
    }

    #region Public API

    public void Invalidate()
    {
        _customVisual?.SendHandlerMessage(Handler.InvalidateMessage);
    }

    protected virtual bool OnFrameUpdate()
    {
        return HasUniform("iTime");
    }

    #endregion

    #region Render Pipeline

    /// <summary>
    /// 解析内容着色器：从 Input 图片加载或从目标控件快照获取。
    /// </summary>
    private SKShader? ResolveContentShader(SKSurface surface, GRContext? grContext, float deviceW, float deviceH, SKRectI pixelBounds)
    {
        if (!HasChildShader("iImage")) return null;

        if (_input != null)
        {
            if (_cachedInputVersion != _inputVersion)
            {
                _cachedInputShader?.Dispose();
                _gpuTextureImage?.Dispose();
                _cachedInputShader = null;
                _gpuTextureImage = null;

                using var cpuImage = LoadImageFromUri(_input);
                if (cpuImage != null)
                {
                    _sourceWidth = cpuImage.Width;
                    _sourceHeight = cpuImage.Height;

                    var texImage = grContext != null ? cpuImage.ToTextureImage(grContext) : null;
                    if (texImage != null)
                    {
                        _gpuTextureImage = texImage;
                        _cachedInputShader = texImage.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Clamp);
                    }
                    else
                    {
                        _cachedInputShader = cpuImage.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Clamp);
                    }
                }
                _cachedInputVersion = _inputVersion;
            }
            return _cachedInputShader;
        }

        // 快照模式
        if (_snapshotDirty || _cachedSnapshotShader == null)
        {
            if (_snapshotSkipFrames > 0)
            {
                _snapshotSkipFrames--;
                return null;
            }

            if (pixelBounds.Width <= 0 || pixelBounds.Height <= 0) return null;

            _cachedSnapshotShader?.Dispose();
            _cachedSnapshotImage?.Dispose();
            _cachedSnapshotShader = null;
            _cachedSnapshotImage = null;

            _cachedSnapshotImage = surface.Snapshot(pixelBounds);
            if (_cachedSnapshotImage == null) return null;

            _cachedSnapshotShader = _cachedSnapshotImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            _sourceWidth = (int)deviceW;
            _sourceHeight = (int)deviceH;
            _snapshotDirty = false;
        }
        return _cachedSnapshotShader;
    }

    /// <summary>
    /// 将内置 uniform（iResolution、iSourceSize）及所有用户参数写入 _cachedUniforms。
    /// </summary>
    private void ApplyUniforms(float deviceW, float deviceH)
    {
        _cachedUniforms ??= new SKRuntimeEffectUniforms(_skEffect);

        if (HasUniform("iResolution"))
            _cachedUniforms["iResolution"] = new[] { deviceW, deviceH };
        if (HasUniform("iSourceSize"))
            _cachedUniforms["iSourceSize"] = new[] { (float)_sourceWidth, (float)_sourceHeight };

        foreach (var p in _params)
        {
            switch (p.Value)
            {
                case float f: _cachedUniforms[p.Key] = f; break;
                case float[] fa: _cachedUniforms[p.Key] = fa; break;
                case int i: _cachedUniforms[p.Key] = i; break;
            }
        }
    }

    private bool HasUniform(string name)
    {
        if (_skEffect == null) return false;
        var uniforms = _skEffect.Uniforms;
        for (int i = 0; i < uniforms.Count; i++)
            if (uniforms[i] == name) return true;
        return false;
    }

    private bool HasChildShader(string name)
    {
        if (_skEffect == null) return false;
        var children = _skEffect.Children;
        for (int i = 0; i < children.Count; i++)
            if (children[i] == name) return true;
        return false;
    }


    private void ApplyChildren(SKShader? contentShader)
    {
        _cachedChildren ??= new SKRuntimeEffectChildren(_skEffect);

        if (contentShader != null && HasChildShader("iImage"))
            _cachedChildren["iImage"] = contentShader;
    }

    internal virtual void Render(SKSurface surface, GRContext? grContext, Rect renderBounds)
    {
        var canvas = surface.Canvas;
        var ctm = canvas.TotalMatrix;
        var scale = ctm.ScaleX;
        var deviceW = (float)(renderBounds.Width * scale);
        var deviceH = (float)(renderBounds.Height * scale);
        if (deviceW <= 0 || deviceH <= 0) return;

        // 从 canvas CTM 推算 surface 绝对像素坐标（用于 snapshot）
        var pixelBounds = new SKRectI(
            (int)ctm.TransX,
            (int)ctm.TransY,
            (int)(ctm.TransX + deviceW),
            (int)(ctm.TransY + deviceH));

        if (pixelBounds != _lastPixelBounds)
        {
            _lastPixelBounds = pixelBounds;
            _snapshotDirty = true;
        }

        var contentShader = ResolveContentShader(surface, grContext, deviceW, deviceH, pixelBounds);
        if (contentShader == null && HasChildShader("iImage")) return;

        ApplyUniforms(deviceW, deviceH);
        ApplyChildren(contentShader);

        using var shader = _skEffect!.ToShader(_cachedUniforms, _cachedChildren);
        if (shader == null) return;

        _cachedPaint ??= new SKPaint();
        _cachedPaint.Shader = shader;
        canvas.Save();
        canvas.Clear();
        canvas.Scale(1f / scale, 1f / scale);
        canvas.DrawRect(SKRect.Create(deviceW, deviceH), _cachedPaint);
#if DEBUG
        _frameCount++;
        var now = _stopwatch.Elapsed.TotalSeconds;
        if (now - _lastFpsTime >= 1.0)
        {
            _fps = _frameCount / (now - _lastFpsTime);
            _frameCount = 0;
            _lastFpsTime = now;
        }
        using var debugPaint = new SKPaint
        {
            Color = new SKColor((byte)Random.Shared.Next(255), (byte)Random.Shared.Next(255), (byte)Random.Shared.Next(255), (byte)125),
        };
        canvas.DrawRect(50, 50, 50, 50, debugPaint);
        Debug.WriteLine("{0:HH:mm:ss.fff} FPS={1:F1}", DateTimeOffset.Now, _fps);
#endif
        canvas.Restore();
    }

    private static SKImage? LoadImageFromUri(Uri uri)
    {
        if (uri.Scheme == "avares")
        {
            using var stream = AssetLoader.Open(uri);
            return SKImage.FromEncodedData(stream);
        }

        var path = uri.Scheme == "file" ? uri.LocalPath : uri.OriginalString;
        if (!File.Exists(path)) return null;
        return SKImage.FromEncodedData(path);
    }

    #endregion

    #region Handler

    private sealed class Handler : CompositionCustomVisualHandler
    {
        public static readonly object StartMessage = new();
        public static readonly object InvalidateMessage = new();

        private readonly ShaderEffect _effect;

        public Handler(ShaderEffect effect) => _effect = effect;

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (_effect._skEffect == null) return;

            // 动画模式：确保帧回调持续注册（防止 SendHandlerMessage 在合成器提交前丢失）
            if (_effect._isAnimating)
                RegisterForNextAnimationFrameUpdate();

            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();
            var surface = lease.SkSurface;
            if (surface == null) return;

            var renderBounds = GetRenderBounds();
            _effect.Render(surface, lease.GrContext, renderBounds);
        }

        public override void OnAnimationFrameUpdate()
        {
            // 守卫：Detach 后可能仍有延迟回调触发
            if (!_effect._isAnimating) return;

            var elapsed = _effect._stopwatch.Elapsed.TotalSeconds;

            if (_effect.HasUniform("iTime"))
                _effect._params["iTime"] = (float)elapsed;

            bool continueAnimation = _effect.OnFrameUpdate();
            Invalidate();

            if (continueAnimation)
                RegisterForNextAnimationFrameUpdate();
            else
                _effect.OnAnimationStopped();
        }

        public override void OnMessage(object message)
        {
            if (message == StartMessage)
            {
                RegisterForNextAnimationFrameUpdate();
            }
            else if (message == InvalidateMessage)
            {
                Invalidate();
            }
        }
    }

    #endregion
}

/// <summary>
/// 效果描述符，持有工厂方法，通过 Create() 生成实例。
/// </summary>
public sealed class EffectDescriptor
{
    public string Name { get; }
    internal Func<ShaderEffect> Factory { get; }
    internal bool Animate { get; }

    public EffectDescriptor(string name, Func<ShaderEffect> factory, bool animate = false)
    {
        Name = name;
        Factory = factory;
        Animate = animate;
    }

    public ShaderEffect Create()
    {
        var effect = Factory();
        if (Animate) effect._pendingAnimate = true;
        return effect;
    }

    public override string ToString() => Name;
}

/// <summary>
/// 标记 ShaderEffect 子类在 XAML 中的可用名称。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EffectNameAttribute : Attribute
{
    public string Name { get; }
    public EffectNameAttribute(string name) => Name = name;
}

public class ShaderEffectConverter : TypeConverter
{
    private static readonly Dictionary<string, EffectDescriptor> _registry = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(EffectDescriptor descriptor)
    {
        _registry[descriptor.Name] = descriptor;
    }

    public static IReadOnlyList<string> Names => _registry.Keys.ToList();

    public static ShaderEffect? Create(string name)
    {
        if (_registry.TryGetValue(name, out var descriptor))
            return descriptor.Create();
        return null;
    }

    public override bool CanConvertFrom(ITypeDescriptorContext? ctx, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(ctx, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? ctx, CultureInfo? culture, object value)
    {
        if (value is string s)
        {
            s = s.Trim();

            if (_registry.TryGetValue(s, out var descriptor))
                return descriptor.Create();

            if (Uri.TryCreate(s, UriKind.Absolute, out var uri))
                return new ShaderEffect(uri);
            if (Path.IsPathRooted(s) && File.Exists(s))
                return new ShaderEffect(new Uri(s));
        }
        return base.ConvertFrom(ctx, culture, value);
    }

    public override bool GetStandardValuesSupported(ITypeDescriptorContext? ctx) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? ctx) => false;

    public override StandardValuesCollection? GetStandardValues(ITypeDescriptorContext? ctx)
    {
        return new(_registry.Keys.ToArray());
    }
}
