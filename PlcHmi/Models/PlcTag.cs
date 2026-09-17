using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PlcHmi.Models;

/// <summary>
/// 采集点位：协议地址 + 变量名 + 换算规则 + 落库列名（宽表模式下用列名建表）。
/// 会被序列化到 plc-config.json，所以必须是可读写的普通属性。
/// </summary>
public partial class PlcTag : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayAddress))]
    private ushort _address;

    [ObservableProperty] private string _name = "点位";
    [ObservableProperty] private string _unit = string.Empty;
    [ObservableProperty] private double _scale = 1;
    [ObservableProperty] private double _offset;

    /// <summary>
    /// 宽表模式下的列名，默认跟变量名一致。
    /// 通常不用单独改：改名时会自动跟着走，见下面的 OnNameChanged。
    /// </summary>
    [ObservableProperty] private string _columnName = "点位";

    /// <summary>
    /// 归到哪个存储组：由点位地址落在哪个区段决定（见 MainViewModel.RefreshTagGroups）。
    /// 只是运行期的展示值，不写进配置文件 —— 区段才是唯一权威。
    /// 空字符串表示不在任何区段内：这种点位既不会被采集，也不会落库。
    /// </summary>
    [JsonIgnore]
    public string Group
    {
        get => _group;
        set
        {
            if (SetProperty(ref _group, value))
            {
                OnPropertyChanged(nameof(GroupText));
            }
        }
    }

    private string _group = string.Empty;

    /// <summary>点位表里的归组展示：没落到任何区段时明说，免得看起来像已归组</summary>
    public string GroupText => string.IsNullOrEmpty(Group) ? "未归组" : Group;

    /// <summary>
    /// 改「名称」时同步「宽表列名」——只有列名没被单独改过（仍等于旧名称）才跟着变。
    /// 常规用法下改一次名称就够了；单独调过列名的场景不会被覆盖。
    /// </summary>
    partial void OnNameChanged(string? oldValue, string newValue)
    {
        if (oldValue is not null && ColumnName == oldValue)
        {
            ColumnName = newValue;
        }
    }

    public PlcTag()
    {
    }

    public PlcTag(ushort address, string name, string unit, double scale, double offset, string? columnName = null)
    {
        Address = address;
        Name = name;
        Unit = unit;
        Scale = scale;
        Offset = offset;
        ColumnName = columnName ?? name;
    }

    /// <summary>人读地址：协议地址 0 对应 40001</summary>
    [JsonIgnore]
    public string DisplayAddress => $"4{Address + 1:D4}";

    /// <summary>默认点位表，与 PlcSimulator 的寄存器映射一致</summary>
    public static IReadOnlyList<PlcTag> Default { get; } =
    [
        new PlcTag(0, "温度", "℃", 0.1, 0),
        new PlcTag(1, "压力", "kPa", 0.01, 0),
        new PlcTag(2, "产量", "件", 1, 0)
    ];
}
