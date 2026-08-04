using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace MediaKit.Avalonia.Effects;

/// <summary>单个 uniform 变更。UI 线程 → 合成线程。</summary>
internal sealed record SetUniformMessage(string Name, object Value);

/// <summary>某个 <c>@texture</c> 槽的图片来源变更。UI 线程 → 合成线程。</summary>
internal sealed record SetTextureMessage(string Name, Uri? Source);

/// <summary>
/// 渲染器，运行在合成线程，独占着色器编译产物、uniform 值与全部 GPU 资源。
/// <para>
/// 与 <see cref="ShaderEffect"/> 之间没有共享可变状态：初始参数经构造入参一次性交接，
/// 后续变更经 <see cref="CompositionCustomVisual.SendHandlerMessage"/> 单向送入；
/// 回写 UI 一律经 <see cref="Dispatcher.UIThread"/> 投递，不等它执行。
/// </para>
/// <para>
/// 每个 <c>uniform shader</c> 是一个纹理槽，来源由 sksl 里的 <c>// @surface</c> /
/// <c>// @texture</c> 标记决定（见 Shaders/README.md）。表面槽每帧重拍快照，
/// 图片槽按版本号缓存。
/// </para>
/// </summary>
internal class ShaderRenderer : CompositionCustomVisualHandler
{
    public static readonly object StartMessage = new();
    public static readonly object InvalidateMessage = new();
    public static readonly object DisposeMessage = new();

    /// <summary>
    /// 一个对象收口 <see cref="SKRuntimeEffect"/>、uniform 与 children，三者必然同源。
    /// </summary>
    private readonly SKRuntimeShaderBuilder _builder;
    private readonly ShaderEffect _owner;

    /// <summary>按 sksl 声明顺序排列，数量是个位数，按名字线性查找即可。</summary>
    private readonly List<TextureSlot> _textures = new();

    private SKPaint? _cachedPaint;

    private bool _isAnimating;
    private readonly Stopwatch _clock = new();
    private bool _disposed;

#if DEBUG
    private int _frameCount;
    private long _lastFpsTicks;
    private double _fps;
#endif

    /// <summary>动画时长（秒），与 <c>iTime</c> 同源。</summary>
    protected double Elapsed => _clock.Elapsed.TotalSeconds;

    /// <remarks>
    /// 由 <see cref="ShaderEffect.CreateRenderer"/> 在 <b>UI 线程</b>调用，因此着色器编译失败与
    /// 标记误用的异常会直接冒泡到 attach 调用点，堆栈清晰且 DEBUG / Release 行为一致。
    /// </remarks>
    internal ShaderRenderer(ShaderEffect owner, string sksl, Dictionary<string, object> uniforms,
        Dictionary<string, Uri?> textures)
    {
        _owner = owner;

        var effect = SKRuntimeEffect.CreateShader(sksl, out var errors);
        if (effect == null)
            throw new InvalidOperationException($"Shader compile error in '{owner.Source}': {errors}");
        _builder = new SKRuntimeShaderBuilder(effect);

        BuildTextureSlots(sksl, textures);

        foreach (var pair in uniforms)
            WriteUniform(pair.Key, pair.Value);
    }

    #region Texture slots

    private sealed class TextureSlot
    {
        public required string Name { get; init; }

        /// <summary>取目标控件的表面快照，而不是图片。</summary>
        public required bool IsSurface { get; init; }

        /// <summary>伴生尺寸 uniform 名（<c>&lt;Name&gt;Size</c>），未声明时为 <c>null</c>。</summary>
        public string? SizeUniform { get; init; }

        /// <summary>图片槽的来源，表面槽恒为 <c>null</c>。</summary>
        public Uri? Source;

        public int Version;
        public int CachedVersion = -1;

        /// <summary>持有的 GPU 纹理或表面快照，与 <see cref="Shader"/> 同生共死。</summary>
        public SKImage? Image;
        public SKShader? Shader;

        /// <summary>喂给伴生尺寸 uniform 的值，无源时为 0。</summary>
        public float Width, Height;

        public void ReleaseGpuResources()
        {
            Shader?.Dispose();
            Image?.Dispose();
            Shader = null;
            Image = null;
        }
    }

    private static readonly Regex ShaderUniformRegex = new(
        @"^uniform\s+shader\s+(\w+)\s*;", RegexOptions.Compiled);

    /// <summary>
    /// 从 sksl 源码解析每个 <c>uniform shader</c> 的来源标记。
    /// <para>
    /// 生成器在编译期做同一件事（为了生成属性并给出诊断），这里仍要再做一遍：
    /// <c>@effect: default</c>、任意 URI、以及内联源码的手写子类这三条路径都没有生成类，
    /// 拿不到编译期结论，而「哪个 slot 取表面」是渲染必需的信息。
    /// </para>
    /// </summary>
    private void BuildTextureSlots(string sksl, Dictionary<string, Uri?> sources)
    {
        var lines = sksl.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // 三种来源标记互斥，用枚举就不可能表达「既 @surface 又 @texture」这种非法状态
        var pending = PendingSource.None;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.StartsWith("// @surface", StringComparison.Ordinal))
            {
                // 后面跟的名字被忽略：表面槽没有用户可设的值，也就没有属性名可言
                pending = PendingSource.Surface;
                continue;
            }
            if (line.StartsWith("// @texture", StringComparison.Ordinal))
            {
                // name 只影响生成的属性名，与渲染无关，这里不需要解析
                pending = PendingSource.Texture;
                continue;
            }
            if (!line.StartsWith("uniform ", StringComparison.Ordinal)) continue;

            var match = ShaderUniformRegex.Match(line);
            if (!match.Success)
            {
                // 数值 uniform 会打断上一个待配对的来源标记，与生成器一致
                pending = PendingSource.None;
                continue;
            }

            var name = match.Groups[1].Value;
            if (pending == PendingSource.None)
                throw new InvalidOperationException(
                    $"Shader '{_owner.Source}' declares 'uniform shader {name}' without a source marker. " +
                    $"Put '// @surface' above it to sample the target control's own surface, " +
                    $"or '// @texture' to supply an image.");

            var isSurface = pending == PendingSource.Surface;
            pending = PendingSource.None;

            if (isSurface && _textures.Any(t => t.IsSurface))
                throw new InvalidOperationException(
                    $"Shader '{_owner.Source}' has more than one '// @surface': " +
                    $"'{_textures.First(t => t.IsSurface).Name}' and '{name}'. " +
                    $"The target control's surface can only fill one slot.");

            // 伴生尺寸查的是编译产物而不是源码，所以 'maskSize' 写在 'mask' 之前也认得
            var sizeName = name + "Size";
            var slot = new TextureSlot
            {
                Name = name,
                IsSurface = isSurface,
                SizeUniform = _builder.Uniforms.Contains(sizeName) ? sizeName : null
            };

            if (!isSurface)
            {
                sources.TryGetValue(name, out var source);
                if (source == null)
                    throw new InvalidOperationException(
                        $"Shader '{_owner.Source}' declares '// @texture' on 'uniform shader {name}' " +
                        $"but no image was supplied. Assign the generated property before attaching, " +
                        $"or mark it '// @surface' to sample the target control's own surface.");
                slot.Source = source;
            }

            _textures.Add(slot);
        }
    }

    private enum PendingSource
    {
        None,
        Surface,
        Texture
    }

    #endregion

    /// <summary>
    /// 写入单个 uniform。着色器未声明的名字被静默丢弃——UI 侧 Release 下不做校验，
    /// 这里是唯一的兜底，且不必关心 SkiaSharp 对未知名字的行为。
    /// </summary>
    private void WriteUniform(string name, object value)
    {
        if (!_builder.Uniforms.Contains(name)) return;

        switch (value)
        {
            case float f: _builder.Uniforms[name] = f; break;
            case float[] fa: _builder.Uniforms[name] = fa; break;
            case int i: _builder.Uniforms[name] = i; break;
            case int[] ia: _builder.Uniforms[name] = ia; break;
        }
    }

    /// <summary>子类推送自己维护的 uniform（如惯性推进后的旋转量、波纹槽位）。</summary>
    protected void SetUniform(string name, object value) => WriteUniform(name, value);

    protected bool HasUniform(string name) => _builder.Uniforms.Contains(name);

#if DEBUG
    /// <summary>交给 UI 侧做即时错名校验的名字快照（拷贝，不共享）。</summary>
    internal string[] GetUniformNamesSnapshot() => _builder.Uniforms.Names.ToArray();

    /// <summary>同上，但只含可写入的 <c>@texture</c> 槽：表面槽不接受 UI 侧喂图。</summary>
    internal string[] GetTextureNamesSnapshot()
        => _textures.Where(t => !t.IsSurface).Select(t => t.Name).ToArray();
#endif

    #region Frame driving

    public override void OnMessage(object message)
    {
        if (_disposed) return;

        switch (message)
        {
            case SetUniformMessage u:
                WriteUniform(u.Name, u.Value);
                // 动画中每帧都会重绘，无需额外置脏
                if (!_isAnimating) Invalidate();
                return;

            case SetTextureMessage t:
                var slot = _textures.FirstOrDefault(s => s.Name == t.Name);
                if (slot != null && !slot.IsSurface)
                {
                    slot.Source = t.Source;
                    slot.Version++;
                    if (!_isAnimating) Invalidate();
                }
                return;
        }

        if (ReferenceEquals(message, StartMessage))
        {
            _isAnimating = true;
            _clock.Start();
            RegisterForNextAnimationFrameUpdate();
        }
        else if (ReferenceEquals(message, InvalidateMessage))
        {
            Invalidate();
        }
        else if (ReferenceEquals(message, DisposeMessage))
        {
            DisposeResources();
        }
    }

    public override void OnAnimationFrameUpdate()
    {
        // Detach 后可能仍有一次延迟回调抵达
        if (_disposed || !_isAnimating) return;

        var elapsed = _clock.Elapsed.TotalSeconds;
        if (_builder.Uniforms.Contains("iTime"))
            _builder.Uniforms["iTime"] = (float)elapsed;

        var continueAnimation = OnFrameUpdate(elapsed);
        Invalidate();

        if (continueAnimation)
            RegisterForNextAnimationFrameUpdate();
        else
            StopAnimation();
    }

    /// <summary>
    /// 每帧推进子类自己的动画状态。返回 <c>false</c> 结束动画。
    /// 运行在合成线程，时间源与 <c>iTime</c> 同源。
    /// </summary>
    protected virtual bool OnFrameUpdate(double elapsed) => _builder.Uniforms.Contains("iTime");

    private void StopAnimation()
    {
        _isAnimating = false;
        _clock.Stop();
        Dispatcher.UIThread.Post(_owner.NotifyAnimationStopped);
    }

    #endregion

    #region Rendering

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_disposed) return;

        // 动画模式：确保帧回调持续注册（防止 SendHandlerMessage 在合成器提交前丢失）
        if (_isAnimating)
            RegisterForNextAnimationFrameUpdate();

        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature == null) return;

        using var lease = leaseFeature.Lease();
        var surface = lease.SkSurface;
        if (surface == null) return;

        Render(surface, lease.GrContext, GetRenderBounds());
    }

    private void Render(SKSurface surface, GRContext? grContext, Rect renderBounds)
    {
        var canvas = surface.Canvas;
        var scale = canvas.TotalMatrix.ScaleX;
        var deviceW = (float)(renderBounds.Width * scale);
        var deviceH = (float)(renderBounds.Height * scale);
        if (deviceW <= 0 || deviceH <= 0) return;

        foreach (var slot in _textures)
        {
            var shader = ResolveSlotShader(slot, surface, grContext, deviceW, deviceH);
            // 一个槽没源就整帧跳过：半喂的画面比黑屏更难排查
            if (shader == null) return;

            _builder.Children[slot.Name] = shader;
            if (slot.SizeUniform != null)
                _builder.Uniforms[slot.SizeUniform] = new[] { slot.Width, slot.Height };
        }

        if (_builder.Uniforms.Contains("iResolution"))
            _builder.Uniforms["iResolution"] = new[] { deviceW, deviceH };

        using var built = _builder.Build();
        if (built == null) return;

        _cachedPaint ??= new SKPaint();
        _cachedPaint.Shader = built;
        canvas.Clear();
        canvas.Save();
        canvas.Scale(1f / scale, 1f / scale);
        canvas.DrawRect(SKRect.Create(deviceW, deviceH), _cachedPaint);
#if DEBUG
        DrawDebugOverlay(canvas);
#endif
        canvas.Restore();
    }

    private static SKShader? ResolveSlotShader(TextureSlot slot, SKSurface surface, GRContext? grContext,
        float deviceW, float deviceH)
    {
        if (slot.IsSurface)
        {
            // 表面内容每帧都可能变，快照没有可缓存的余地
            slot.ReleaseGpuResources();

            slot.Image = surface.Snapshot();
            if (slot.Image == null) return null;
            //using var data = slot.Image.Encode(SKEncodedImageFormat.Png, 100);
            //using var file = File.Create("ttt.png");
            //data.SaveTo(file);

            slot.Shader = slot.Image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            slot.Width = deviceW;
            slot.Height = deviceH;
            return slot.Shader;
        }

        if (slot.Source == null) return null;
        if (slot.CachedVersion == slot.Version) return slot.Shader;

        slot.ReleaseGpuResources();
        slot.Width = 0;
        slot.Height = 0;

        using var cpuImage = LoadImageFromUri(slot.Source);
        if (cpuImage != null)
        {
            slot.Width = cpuImage.Width;
            slot.Height = cpuImage.Height;

            var texImage = grContext != null ? cpuImage.ToTextureImage(grContext) : null;
            if (texImage != null)
            {
                slot.Image = texImage;
                slot.Shader = texImage.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Clamp);
            }
            else
            {
                slot.Shader = cpuImage.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Clamp);
            }
        }
        slot.CachedVersion = slot.Version;
        return slot.Shader;
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

#if DEBUG
    private void DrawDebugOverlay(SKCanvas canvas)
    {
        _frameCount++;
        var nowTicks = Stopwatch.GetTimestamp();
        if (_lastFpsTicks == 0) _lastFpsTicks = nowTicks;

        var seconds = (nowTicks - _lastFpsTicks) / (double)Stopwatch.Frequency;
        if (seconds >= 1.0)
        {
            _fps = _frameCount / seconds;
            _frameCount = 0;
            _lastFpsTicks = nowTicks;
            Debug.WriteLine("{0:HH:mm:ss.fff} {1} FPS={2:F1}", DateTimeOffset.Now, GetType().Name, _fps);
        }
    }
#endif

    #endregion

    /// <summary>
    /// 在合成线程释放全部 GPU 资源。由 <see cref="DisposeMessage"/> 触发。
    /// </summary>
    protected virtual void DisposeResources()
    {
        _disposed = true;

        foreach (var slot in _textures)
            slot.ReleaseGpuResources();

        _cachedPaint?.Dispose();
        _cachedPaint = null;

        // 连带释放内部的 SKRuntimeEffect。仅因为 Renderer 独占该编译产物才成立：
        // 若日后为「同一 sksl 的多个实例复用同一份 SKRuntimeEffect」引入编译缓存，
        // 这里必须改为只释放 Uniforms 与 Children，或让缓存持引用计数。
        _builder.Dispose();
    }
}
