# SkSL 着色器约定


## 项目配置

`.sksl` 添加为`AvaloniaResource`

---

## 标记注释

标记以 `// @` 开头，分两类：**文件级**标记描述整个效果，写在文件顶部、第一个 uniform 之前；
**uniform 级**标记描述单个 uniform，写在它声明的上一行。

### 文件级标记

| 标记 | 说明 |
|------|------|
| `// @effect: ClassName` | 生成强类型子类（partial class），支持属性绑定。类名建议以 `Effect` 结尾；省略名称时默认为 `<文件名>Effect` |
| `// @effect: default` | 不生成类，仅将着色器注册为工厂描述符，运行时按文件名创建 |
| `// @animate` | 标记该效果启动后自动播放动画（持续推送 `iTime`） |

> 一个文件必须带 `@effect`（两种形态任选一种），否则会被生成器**静默忽略** ——
> 既不生成类也不注册，且不报错。`Cosmos.sksl` 目前就处于这个状态。

### uniform 级标记

紧跟在被标注的 uniform 声明**上方一行**。

| 标记 | 作用对象 | 说明 |
|------|---------|------|
| `// @surface` | `uniform shader` | 该 slot 填目标控件自身的表面快照。一个文件最多一个，基类随之取 `ShaderEffect` |
| `// @texture [Name]` | `uniform shader` | 该 slot 由调用方喂图，生成 `Uri?` 属性。`Name` 可省略，默认从 uniform 名推导 |
| `// @property: default` | 非 shader uniform | 生成 Avalonia `DirectProperty` 并指定默认值 |

每个 `uniform shader` **必须**标 `@surface` 或 `@texture` 之一，否则报 `SKSL002` ——
渲染侧无从判断这个 slot 该填什么。

属性名推导规则：剥掉 `i` + 大写字母的前缀再首字母大写，`iImage → Image`、`iMask → Mask`；
`intensity` 这种 `i` 后接小写的不受影响。需要别的名字就显式写 `// @texture Background`。

> `@surface` 不生成属性，所以它后面写名字没有意义，会被忽略并报 `SKSL007`（警告）。

### 伴生尺寸 uniform

纹理的像素尺寸走**命名约定**，不是保留名：为纹理 `foo` 声明 `uniform float2 fooSize`，
渲染侧每帧自动填入该纹理的实际像素尺寸。

```glsl
// @texture
uniform shader iImage;
uniform float2 iImageSize;   // 自动填 iImage 的像素尺寸
```

只在需要时声明；不声明就不填。声明顺序不影响识别（`fooSize` 写在 `foo` 之前也认得）。
伴生名不能再标 `@property`，也不能是 `float2` 以外的类型，否则报 `SKSL008`。

对 `@surface` 而言表面尺寸恒等于视口尺寸，直接用 `iResolution` 即可，不必声明伴生名。

---

## 内置 Uniform（保留名称）

以下名称由框架自动管理，无需手动赋值：

| Uniform | 类型 | 说明 |
|---------|------|------|
| `iResolution` | `float2` | 视口设备像素尺寸 |
| `iTime` | `float` | 动画已播放秒数（需配合 `@animate`） |

只有这两个是保留名。`iImage` **不是**保留名 —— 它只是历史上惯用的纹理名，
和其他 `uniform shader` 一样必须自己标 `@surface` 或 `@texture`，改叫什么都行。

一个 `uniform shader` 都不声明的着色器就是纯生成式效果（如 `Clouds`、`Pyramid`）。

---

## 两种模式对比

### @effect: default（轻量注册）

适合**纯展示型**着色器，不需要从 C# 暴露属性绑定。框架在 `ModuleInitializer` 中自动将其注册到 `ShaderEffectConverter` 工厂。

```glsl
// @effect: default
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

### @effect: ClassName（强类型子类）

适合**需要属性绑定**的交互式效果。Generator 生成 partial class，你可以在手写的 `.cs` 文件中扩展行为逻辑。

```glsl
// @effect: PanoEffect
// @texture
uniform shader iImage;
uniform float2 iResolution;
uniform float2 iImageSize;
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
- 每个 `@texture` 对应的 `Uri?` 属性与 `CollectTextures` 覆写
- 构造函数中 uniform 初始值推送
- `[EffectName]` 特性
- `ShaderEffects` 静态描述符属性

使用：
```csharp
var pano = ShaderEffects.Pano.Create();
pano.Image = new Uri(path);      // 属性名由 iImage 推导
ShaderEffect.SetEffect(panel, pano);
```

> `@effect: default` 不生成类，自然也没有属性可以喂图，所以它**不能**和 `@texture` 同时用（报 `SKSL009`）。
> 只用 `@surface` 或不用纹理时才能用 `default` 形态。

---

## 两种基类：ShaderPainter 与 ShaderEffect

文件里有没有 `@surface` 决定生成的类继承谁。

| | `ShaderPainter`（默认） | `ShaderEffect`（带 `@surface`） |
|---|---|---|
| 定位 | 自己画像素 | 拿别人画好的像素再加工 |
| 纹理来源 | 全部由 `@texture` 属性喂图；不声明则纯生成式 | 其中一个 slot 取目标控件的表面快照 |
| 对目标控件的副作用 | 无 | attach 时开启 `BitmapCache` |
| 典型用途 | Clouds、Pyramid、Pano | Ripple、Heartfelt |

两者共用同一个 `ShaderRenderer`；哪个 slot 取表面、哪个取图片，是渲染时读 sksl 标记得出的，
不靠类型区分。`@surface` 和 `@texture` 也可以同时存在（拿控件快照叠一张贴图），
不过仓库里目前没有这样的着色器，这条组合路径尚未实跑验证过。

### 为什么要分开

`ShaderEffect` 这个名字在两个框架里都严格是「后置加工」语义：Avalonia 的 `Avalonia.Media.Effects.Effect`
（`BlurEffect` / `DropShadowEffect`）、WPF 的 `System.Windows.Media.Effects.ShaderEffect`，都只用于控件表面后处理。
自生成着色器套用这个名字会和框架惯例相悖，所以自绘的那一半另起 `ShaderPainter`，
`ShaderEffect` 只留给真正读取目标像素的场景。

### 为什么 ShaderEffect 要占用目标的 CacheMode

因为要拿到目标子树**在父级合成之前**的像素。开启 `BitmapCache` 后，lease 出的 `SKSurface`
变成目标子树的隔离离屏 layer，快照取到的是 alpha 正确的干净像素；不开的话，半透明目标
会把底下父级的颜色一起串进快照，加工结果就错了。

侵入是有边界的：

- attach 时若 `target.CacheMode != null`，**不抢**——尊重调用方已有的缓存策略；
- detach 时只还原自己设的那一份（内部 `_ownsCacheMode` 标记），不会把别人的 `CacheMode` 清掉。

> 实现上有个坑：`CacheModeProperty` 不在 `AffectsRender` 列表里，赋值后必须主动
> `InvalidateVisual()`，才能把变更同步到合成树。

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
// @surface
uniform shader iImage;
uniform float2 iResolution;
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
    return iImage.eval(uv * iResolution);
}
```

---

## 诊断编号

| 编号 | 级别 | 含义 |
|------|------|------|
| `SKSL001` | Error | 效果名称在多个 `.sksl` 文件中重复 |
| `SKSL002` | Error | `uniform shader` 缺 `@surface` / `@texture` 来源标记 |
| `SKSL003` | Error | 来源标记位置错误（标在非 shader 上，或同一个 uniform 标了两种） |
| `SKSL004` | Error | 多个 `@surface` —— 表面只能填一个 slot |
| `SKSL005` | Error | `@property` 标在了 `shader` 类型上 |
| `SKSL006` | Error | `@texture` 推导或指定的属性名不可用（非法标识符、与其他属性冲突） |
| `SKSL007` | Warning | `@surface` 后面的名字被忽略 |
| `SKSL008` | Error | 伴生尺寸 uniform 用法错误（类型不是 `float2`，或多标了 `@property`） |
| `SKSL009` | Error | `@effect: default` 下用了 `@texture`，没有属性可以喂图 |
