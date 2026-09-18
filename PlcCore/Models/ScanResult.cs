using CommunityToolkit.Mvvm.ComponentModel;

namespace PlcCore.Models;

/// <summary>
/// 扫描结果的一行：地址、读到的原始值、状态。
/// 只区分「可读」和「读异常」，不做“恒 0 就是空点位”之类的主观判断，由用户自己勾选。
/// </summary>
public partial class ScanResult : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

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
