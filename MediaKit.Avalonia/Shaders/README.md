# SkSL 着色器约定


## 项目配置

`.sksl` 添加为`AvaloniaResource`

---

## 标记注释

标记必须位于文件顶部、uniform 声明之前，以 `// @` 开头。

| 标记 | 说明 |
|------|------|
| `// @register` | 将着色器注册为工厂效果，运行时通过名称创建。默认名称为文件名，也可指定：`// @register: MyName` |
| `// @effect: ClassName` | 生成强类型 `ShaderEffect` 子类（partial class），支持属性绑定。类名建议以 `Effect` 结尾 |
| `// @animate` | 标记该效果启动后自动播放动画（持续推送 `iTime`） |
| `// @property: default` | 紧跟在 uniform 上方，为该 uniform 生成 Avalonia `DirectProperty`，并指定默认值 |

> `@register` 与 `@effect` 互斥 —— 一个文件只能选择其中一种模式。

---

## 内置 Uniform（保留名称）

以下名称由框架自动管理，无需手动赋值：

| Uniform | 类型 | 说明 |
|---------|------|------|
| `iResolution` | `float2` | 视口设备像素尺寸 |
| `iSourceSize` | `float2` | 源图像原始像素尺寸（仅 Input 模式有效） |
| `iImage` | `shader` | 内容着色器（图片或控件快照） |
| `iTime` | `float` | 动画已播放秒数（需配合 `@animate`） |

声明了 `iImage` 的着色器需要图像输入（通过 `Input` 属性或控件快照提供）；未声明则为纯生成式效果。

---

## 两种模式对比

### @register（轻量注册）

适合**纯展示型**着色器，不需要从 C# 暴露属性绑定。框架在 `ModuleInitializer` 中自动将其注册到 `ShaderEffectConverter` 工厂。

```glsl
// @register
// @animate
uniform float2 iResolution;
uniform float iTime;

half4 main(float2 fragCoord) {
    // ...
}
```

生成代码等效于：
```csharp
// ShaderEffects.g.cs
public static class ShaderEffects
{
    public static EffectDescriptor Clouds { get; } = new("Clouds", ..., animate: true);
}
```

使用：
```csharp
ShaderEffect.SetEffect(panel, ShaderEffects.Clouds.Create());
```

### @effect（强类型子类）

适合**需要属性绑定**的交互式效果。Generator 生成 partial class，你可以在手写的 `.cs` 文件中扩展行为逻辑。

```glsl
// @effect: PanoEffect
uniform shader iImage;
uniform float2 iResolution;
uniform float2 iSourceSize;
// @property: 0.5
uniform float rotationX;
// @property: 90.0
uniform float fov;

half4 main(float2 fragCoord) {
    // ...
}
```

生成代码包含：
- `DirectProperty` 声明（支持数据绑定）
- 构造函数中 uniform 初始值推送
- `[EffectName]` 特性
- `ShaderEffects` 静态描述符属性

使用：
```csharp
var pano = ShaderEffects.Pano.Create();
pano.Input = new Uri(path);
ShaderEffect.SetEffect(panel, pano);
```

---

## @property 默认值格式

| SkSL 类型 | 默认值示例 | C# 类型 |
|-----------|-----------|---------|
| `float` | `0.5` | `double` |
| `int` | `3` | `int` |
| `float2` | `0.5, 0.5` | `float[]` |
| `float3` | `0.0, 0.0, -10.0` | `float[]` |
| `float4` | `1.0, 1.0, 1.0, 1.0` | `float[]` |

---

## 入口函数

着色器入口为：

```glsl
half4 main(float2 fragCoord) {
    // fragCoord: 当前片元在设备像素坐标系中的位置
    // 返回: RGBA 颜色
}
```

---

## 完整示例

```glsl
// @effect: RippleEffect
uniform shader iImage;
uniform float2 iResolution;
uniform float2 iSourceSize;
uniform float iTime;
// @property: 0.1
uniform float amplitude;
// @property: 50.0
uniform float frequency;
// @property: 10.0
uniform float speed;
// @property: 3.0
uniform float duration;

half4 main(float2 fragCoord) {
    float2 uv = fragCoord / iResolution;
    // ... 波纹计算
    return iImage.eval(uv * iSourceSize);
}
```
