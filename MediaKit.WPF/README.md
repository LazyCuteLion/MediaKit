# MediaKit.WPF

基于 WPF `ShaderEffect` 的视频像素着色器效果库，通过附加属性零侵入地为任意 `UIElement`（特别是 `MediaElement`）添加实时渲染效果。

## 支持平台

- .NET 8.0 (Windows)
- .NET Framework 4.6.2

## 安装

```bash
dotnet add package MediaKit.WPF
```

或在 NuGet 包管理器中搜索 `MediaKit.WPF`。

## 命名空间

```csharp
using MediaKit.WPF;
```

XAML 中引用：

```xml
xmlns:mk="https://github.com/LazyCuteLion/MediaKit"
```

## 功能

### 1. PanoEffect — 360 全景视频投影

将等距柱状投影（Equirectangular）全景视频转换为球面透视投影，支持鼠标拖拽旋转 + 惯性滑动 + 滚轮缩放。

```xml
<MediaElement Source="360_video.mp4"
              mk:PanoEffect.IsEnabled="True"
              mk:PanoEffect.Fov="90"
              mk:PanoEffect.Zoom="0.5" />
```

**属性说明：**

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `IsEnabled` | bool | false | 是否启用全景效果 |
| `Fov` | double | 90.0 | 视场角（度） |
| `Zoom` | double | 0.5 | 缩放级别 [0.1, 2.0] |
| `RotationX` | double | 0.5 | 水平旋转 [0, 1] |
| `RotationY` | double | 0.5 | 垂直旋转 [0, 1] |

**重置参数：**

```csharp
// 通过静态方法
PanoEffect.Reset(mediaElement);

// 或通过实例方法
(mediaElement.Effect as PanoEffect)?.Reset();
```

### 2. AlphaEffect — Alpha 通道合成

将视频中水平/上下排列的 Alpha 通道信息进行合成，实现透明视频播放。

```xml
<MediaElement Source="alpha_video.mp4"
              mk:AlphaEffect.IsEnabled="True"
              mk:AlphaEffect.Position="Right" />
```

**属性说明：**

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `IsEnabled` | bool | false | 是否启用 Alpha 合成效果 |
| `Position` | Dock | Right | Alpha 通道位置（Left/Top/Right/Bottom） |

### 3. TileEffect — 视频平铺

将视频画面以网格方式平铺显示，支持自定义行列数和间距。

```xml
<MediaElement Source="video.mp4"
              mk:TileEffect.IsEnabled="True"
              mk:TileEffect.Rows="2"
              mk:TileEffect.Columns="3"
              mk:TileEffect.Spacing="4"
              mk:TileEffect.SpacingColor="Black" />
```

**属性说明：**

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `IsEnabled` | bool | false | 是否启用平铺效果 |
| `Rows` | double | 1.0 | 行数 |
| `Columns` | double | 1.0 | 列数 |
| `Spacing` | double | 0.0 | 间距（像素） |
| `SpacingColor` | Color | Transparent | 间距填充颜色 |

### 4. MediaBehavior — 媒体进度绑定

为 `MediaElement` 提供可绑定的播放进度、位置和时长附加属性，支持 Slider 双向绑定与拖拽 Seek（内置节流防抖）。

```xml
<MediaElement x:Name="media"
              Source="video.mp4"
              LoadedBehavior="Manual"
              mk:MediaBehavior.Interval="100" />

<Slider Minimum="0" Maximum="100"
        Value="{Binding (mk:MediaBehavior.Progress), ElementName=media}" />

<TextBlock Text="{Binding (mk:MediaBehavior.Position), ElementName=media, StringFormat=mm\\:ss}" />
<TextBlock Text="{Binding (mk:MediaBehavior.Duration), ElementName=media, StringFormat=mm\\:ss}" />
```

**属性说明：**

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Interval` | int | 0 | 轮询间隔（ms），>0 启用，≤0 禁用 |
| `Progress` | double | 0.0 | 播放进度 0~100，双向绑定，设置时自动 Seek |
| `Position` | TimeSpan | 00:00:00 | 当前播放位置（只读） |
| `Duration` | TimeSpan | 00:00:00 | 媒体总时长（只读） |

## 许可证

[MIT](../LICENSE)
