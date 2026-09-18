using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HmiCore.Models;

/// <summary>
/// 触发记录规则：靠握手模拟上升沿，保证每次触发都被记到、不重复。
/// 时序（现场约定）：
///   1) PLC 把信号寄存器写 1；
///   2) 上位机轮询到信号=1 → 记一条记录 + 把应答寄存器写 1；
///   3) PLC 看到应答=1 → 把信号寄存器写 0；
///   4) 上位机轮询到信号=0 → 把应答寄存器写 0，回到待触发，开始下一次监听。
/// 因为"记录"一定发生在"应答清零"之前，所以 PLC 只要不在应答为 1 期间重新置位信号，
/// 就不会出现漏记。
/// </summary>
public partial class TriggerRule : ObservableObject
{
    [ObservableProperty] private string _name = "触发";

    /// <summary>信号寄存器：PLC 写 1 表示"来了一次"（0-based 协议地址）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySignal))]
    private ushort _signalAddress;

    /// <summary>应答寄存器：上位机写 1 应答，PLC 看到后负责把信号清 0（0-based 协议地址）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayAck))]
    private ushort _ackAddress;

    /// <summary>是否参与监听</summary>
    [ObservableProperty] private bool _enabled = true;

    /// <summary>写应答之后等 PLC 清信号的超时（毫秒）：超时只报警、保持等待，不强行复位</summary>
    [ObservableProperty] private int _timeoutMs = 5000;

    [JsonIgnore] public string DisplaySignal => $"4{SignalAddress + 1:D4}";

    [JsonIgnore] public string DisplayAck => $"4{AckAddress + 1:D4}";

    /// <summary>运行期状态（等待信号 / 等 PLC 清信号 / 超时），仅界面展示，不写进配置</summary>
    [JsonIgnore]
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private string _statusText = "未启动";

    public TriggerRule()
    {
    }

    public TriggerRule(string name, ushort signalAddress, ushort ackAddress)
    {
        Name = name;
        SignalAddress = signalAddress;
        AckAddress = ackAddress;
    }
}
