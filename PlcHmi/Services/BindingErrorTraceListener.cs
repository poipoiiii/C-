using System.Diagnostics;
using Serilog;

namespace PlcHmi.Services;

/// <summary>
/// 把 WPF 的绑定错误接进日志。
/// 绑错属性名时界面不会报错、也不会崩，只是那一格"没有数据" —— 靠肉眼很难发现，
/// 而这类错误在重构 XAML / DataContext 之后最容易出现，所以让它落到日志里。
/// </summary>
public sealed class BindingErrorTraceListener : TraceListener
{
    public override void Write(string? message)
    {
        // WPF 是按行输出的，写内容的都在 WriteLine，这里不用管
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Log.Warning("WPF 绑定：{Message}", message);
        }
    }
}
