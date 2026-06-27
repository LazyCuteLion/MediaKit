using System;

namespace MediaKit.WinUI3.Demo;

/// <summary>
/// 将 TimeSpan 格式化为 "mm:ss" 或 "h:mm:ss" 字符串。
/// </summary>
public static class TimeSpanToStringConverter
{
    public static string Format(TimeSpan ts)
    {
        return ts.Hours > 0
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"mm\:ss");
    }
}
