# MediaKit

一套用于 .NET 桌面应用的视频特效与媒体播放控件库。

## 项目组成

| 包名 | 平台 | 说明 |
|------|------|------|
| [MediaKit.WPF](MediaKit.WPF/) | WPF (.NET 8 / .NET Framework 4.6.2) | 基于 WPF ShaderEffect 的视频像素着色器效果库，提供 Alpha 通道分离、360 全景投影、视频平铺等效果 |
| [MediaKit.WinUI3](MediaKit.WinUI3/) | WinUI 3 (.NET 8) | 基于 Win2D PixelShaderEffect + Composition API 的 360 全景视频播放控件 |

## 快速开始

### 安装

```bash
# WPF 项目
dotnet add package MediaKit.WPF

# WinUI 3 项目
dotnet add package MediaKit.WinUI3
```

### MediaKit.WPF 使用示例

```xml
<Window xmlns:mk="https://github.com/LazyCuteLion/MediaKit">
    <!-- Alpha 通道分离 -->
    <MediaElement mk:AlphaEffect.IsEnabled="True" mk:AlphaEffect.Position="Right" />

    <!-- 360 全景视频 -->
    <MediaElement mk:PanoEffect.IsEnabled="True" mk:PanoEffect.Fov="90" />

    <!-- 视频平铺 -->
    <MediaElement mk:TileEffect.IsEnabled="True" mk:TileEffect.Rows="2" mk:TileEffect.Columns="2" />
</Window>
```

### MediaKit.WinUI3 使用示例

```xml
<Page xmlns:mk="using:MediaKit.WinUI3">
    <mk:PanoMediaElement Source="{Binding VideoUri}" AutoPlay="True" Fov="90" />
</Page>
```

## 构建要求

- Visual Studio 2022 17.8+
- .NET 8 SDK
- Windows SDK 10.0.26100.0（用于 HLSL 着色器编译 fxc.exe）

## 许可证

[MIT](LICENSE)
