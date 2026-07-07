using System;
using Microsoft.Graphics.Display;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinUIEx;

namespace MediaKit.WinUI3.Demo;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();
    }

    private async void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".avi");
        picker.FileTypeFilter.Add(".webm");

        var hwnd = this.GetWindowHandle();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            panoPlayer.Source = new Uri(file.Path);
        }
    }

    #region 伪全屏（规避 Independent Flip 卡顿）

    private bool _isFullScreen;
    private RectInt32 _restoreBounds;
    private WindowStyle _originalStyle;
    private ExtendedWindowStyle _originalExStyle;

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        //if (_isFullScreen)
        //    ExitPseudoFullScreen();
        //else
        //    EnterPseudoFullScreen();
        if (_isFullScreen) 
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            _isFullScreen = false;
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            _isFullScreen = true;
        }
    }

    private void EnterPseudoFullScreen()
    {
        var hwnd = this.GetWindowHandle();

        // 保存当前窗口位置和样式
        _restoreBounds = AppWindow.Position is var pos
            ? new RectInt32(pos.X, pos.Y, AppWindow.Size.Width, AppWindow.Size.Height)
            : default;
        _originalStyle = HwndExtensions.GetWindowStyle(hwnd);
        _originalExStyle = HwndExtensions.GetExtendedWindowStyle(hwnd);

        // 设置 WS_POPUP 样式（无边框）
        HwndExtensions.SetWindowStyle(hwnd, WindowStyle.Popup | WindowStyle.Visible);
        HwndExtensions.SetExtendedWindowStyle(hwnd, 0);

        // 置顶（盖住任务栏）
        this.SetIsAlwaysOnTop(true);

        // 定位到屏幕大小 - 2px（规避 Independent Flip）
        var bounds = DisplayArea.Primary.OuterBounds;
        AppWindow.MoveAndResize(new RectInt32(
            bounds.X, bounds.Y, bounds.Width, bounds.Height - 2));

        _isFullScreen = true;
    }

    private void ExitPseudoFullScreen()
    {
        var hwnd = this.GetWindowHandle();

        // 恢复样式
        HwndExtensions.SetWindowStyle(hwnd, _originalStyle);
        HwndExtensions.SetExtendedWindowStyle(hwnd, _originalExStyle);

        // 取消置顶
        this.SetIsAlwaysOnTop(false);

        // 恢复位置大小
        AppWindow.MoveAndResize(_restoreBounds);

        _isFullScreen = false;
    }

    #endregion
}
