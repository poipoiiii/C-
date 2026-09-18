using CommunityToolkit.Mvvm.ComponentModel;

namespace HmiCore.Models;

/// <summary>
/// 扫描结果的一行：地址、读到的原始值、状态。
/// 只区分「可读」和「读异常」，不做“恒 0 就是空点位”之类的主观判断，由用户自己勾选。
/// </summary>
public partial class ScanResult : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// 浮点预览：本行与紧跟它的下一个寄存器拼成一个 32 位浮点显示（字序由界面上的下拉决定）。
    /// 现场 PLC 常把浮点写成两个连续寄存器，这一列帮着认出哪两个是一个浮点、该用哪种字序；
    /// 只是给人看的提示，不参与采集 —— 采集用点位表里那个点位的类型与字序。
    /// 地址不连续、或有一边读不到时为 "--"。
    /// </summary>
    [ObservableProperty] private string _floatPreview = "--";

    public ScanResult(ushort address, short? rawValue, string status)
    {
        Address = address;
        RawValue = rawValue;
        Status = status;
    }

    public ushort Address { get; }

    public short? RawValue { get; }

    public string Status { get; }

    public bool IsReadable => RawValue.HasValue;

    /// <summary>人读地址：协议地址 0 对应 40001</summary>
    public string DisplayAddress => $"4{Address + 1:D4}";

    public string RawText => RawValue?.ToString() ?? "--";
}
