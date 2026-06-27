# MediaKit.WinUI3

基于 Win2D `PixelShaderEffect` + Composition API 的 360 全景视频播放控件，为 WinUI 3 应用提供开箱即用的球面投影渲染。

## 支持平台

- .NET 8.0
- Windows App SDK 1.8+
- 最低 Windows 版本：10.0.19041.0
- 目标 Windows 版本：10.0.22621.0

## 安装

```bash
dotnet add package MediaKit.WinUI3
```

或在 NuGet 包管理器中搜索 `MediaKit.WinUI3`。

## 命名空间

```csharp
using MediaKit.WinUI3;
```

XAML 中引用：

```xml
xmlns:mk="using:MediaKit.WinUI3"
```

## 功能

### PanoMediaElement — 360 全景视频播放器

一个自包含的 WinUI 3 控件，集成了：
- 等距柱状投影 → 球面透视投影（GPU 着色器实时渲染）
- 鼠标拖拽旋转 + 惯性滑动
- 滚轮缩放
- 完整的播放控制（播放/暂停/停止/跳转/循环/倍速）
- 视角重置（Reset）

## 基本用法

### XAML

```xml
<Page xmlns:mk="using:MediaKit.WinUI3">
    <mk:PanoMediaElement x:Name="Player"
                         Source="{Binding VideoUri}"
                         AutoPlay="True"
                         Fov="90"
                         Zoom="0.5"
                         IsLooping="True" />
</Page>
```

### Code-Behind

```csharp
using MediaKit.WinUI3;

// 设置视频源
Player.Source = new Uri("ms-appx:///Assets/360video.mp4");

// 播放控制
Player.Play();
Player.Pause();
Player.Stop();
Player.Seek(TimeSpan.FromSeconds(30));

// 调整视角
Player.Fov = 100;
Player.Zoom = 0.8;

// 重置视角
Player.Reset();
```

## 属性说明

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Source` | Uri | null | 视频源 URI |
| `RotationX` | double | 0.5 | 水平旋转 [0, 1]，0.5 为正前方 |
| `RotationY` | double | 0.5 | 垂直旋转 [0, 1]，0.5 为水平视线 |
| `Zoom` | double | 0.5 | 缩放级别 [0.1, 2.0] |
| `Fov` | double | 90.0 | 视场角（度） |
| `Position` | TimeSpan | 0 | 当前播放位置，外部设置时自动 Seek（内置防抖） |
| `Duration` | TimeSpan | 0 | 媒体总时长（只读） |
| `Progress` | double | 0.0 | 播放进度 [0, 100]，双向绑定，设置时自动 Seek（内置防抖） |
| `IsPlaying` | bool | false | 是否正在播放 |
| `Volume` | double | 1.0 | 音量 [0.0, 1.0] |
| `IsMuted` | bool | false | 是否静音 |
| `PlaybackRate` | double | 1.0 | 播放速率 |
| `AutoPlay` | bool | true | 设置 Source 后是否自动播放 |
| `IsLooping` | bool | false | 是否循环播放 |
| `NaturalVideoWidth` | int | 0 | 视频原始宽度（只读） |
| `NaturalVideoHeight` | int | 0 | 视频原始高度（只读） |

## 命令（ICommand）

支持 MVVM 命令绑定，无需 Code-Behind：

| 命令 | 说明 |
|------|------|
| `PlayCommand` | 开始播放 |
| `PauseCommand` | 暂停播放 |
| `StopCommand` | 停止并重置到起始位置 |
| `ResetCommand` | 重置视角参数（Fov/Zoom/RotationX/RotationY） |

```xml
<Button Command="{x:Bind Player.PlayCommand}" Content="▶" />
<Button Command="{x:Bind Player.PauseCommand}" Content="⏸" />
<Button Command="{x:Bind Player.StopCommand}" Content="⏹" />
<Button Command="{x:Bind Player.ResetCommand}" Content="Reset" />
```

## 事件

| 事件 | 参数 | 说明 |
|------|------|------|
| `MediaOpened` | EventArgs | 媒体打开完成 |
| `MediaEnded` | EventArgs | 播放结束 |
| `MediaFailed` | string | 加载或播放失败，参数为错误信息 |
| `PositionChanged` | TimeSpan | 播放位置变化 |

## 与 Slider 绑定进度

直接双向绑定 `Progress`，拖拽时内置防抖防止画面跳跃：

```xml
<Slider Minimum="0" Maximum="100"
        Value="{x:Bind Player.Progress, Mode=TwoWay}" />
```

## 依赖项

- Microsoft.Graphics.Win2D 1.4.0
- Microsoft.WindowsAppSDK 1.8+

## 许可证

[MIT](../LICENSE)