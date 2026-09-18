using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HmiCore.Models;

/// <summary>
/// 采集点位：协议地址 + 数据类型 + 变量名 + 换算规则 + 落库列名（宽表模式下用列名建表）。
/// 会被序列化到 data/configs/&lt;机型&gt;.json，所以必须是可读写的普通属性。
///
/// 注意「一个点位占几个寄存器」由 <see cref="DataType"/> 决定：16 位量占 1 个，
/// 32 位占 2 个，64 位占 4 个（见 <see cref="PlcValueCodec"/>）。现场多数量都是 32 位浮点占两个寄存器。
/// </summary>
public partial class PlcTag : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayAddress))]
    [NotifyPropertyChangedFor(nameof(EndAddress))]
    private ushort _address;

    /// <summary>数据类型：老配置没有这个字段，缺省 16 位有符号，与老版本的行为一致</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayAddress))]
    [NotifyPropertyChangedFor(nameof(EndAddress))]
    [NotifyPropertyChangedFor(nameof(RegisterCount))]
    [NotifyPropertyChangedFor(nameof(IsMultiRegister))]
    private PlcDataType _dataType = PlcDataType.Int16;

    /// <summary>多寄存器数值的字序（只有占多个寄存器的类型用得上）</summary>
    [ObservableProperty] private WordOrder _wordOrder = WordOrder.HighWordFirst;

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
    // 只是给人看的展示值，写进配置文件只会让同名机的文件出现无谓差异（区段才是权威）
    [JsonIgnore]
    public string GroupText => string.IsNullOrEmpty(Group) ? "未归组" : Group;

    /// <summary>
    /// 运行期提示：与别的点位压住了同一个寄存器时说明跟谁重叠（空 = 正常）。
    /// 把某个点位改宽（比如 16 位改成 32 位）很容易盖住后面的点位，这里当场标出来 ——
    /// 建表前的校验也会拦，但等到那时才说，人只会觉得"怎么建不了表"。
    /// 同样是展示值，不写进配置：重叠由地址与类型推出来，存一份就会不一致。
    /// </summary>
    [JsonIgnore]
    public string OverlapText
    {
        get => _overlapText;
        set
        {
            if (SetProperty(ref _overlapText, value))
            {
                OnPropertyChanged(nameof(HasOverlap));
            }
        }
    }

    private string _overlapText = string.Empty;

    /// <summary>寄存器被别的点位占着，或者自己占了别人的</summary>
    [JsonIgnore]
    public bool HasOverlap => OverlapText.Length > 0;

    /// <summary>
    /// 类型与字序能不能改：**被压住**的那个锁住（再改只会把冲突改大），
    /// 压住别人的那个不锁 —— 总得留一条"改窄改回来"的路。
    /// </summary>
    [JsonIgnore]
    public bool CanEditType
    {
        get => _canEditType;
        set => SetProperty(ref _canEditType, value);
    }

    private bool _canEditType = true;

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

    public PlcTag(ushort address, string name, string unit, double scale, double offset, string? columnName = null,
        PlcDataType dataType = PlcDataType.Int16, WordOrder wordOrder = WordOrder.HighWordFirst)
    {
        Address = address;
        Name = name;
        Unit = unit;
        Scale = scale;
        Offset = offset;
        ColumnName = columnName ?? name;
        DataType = dataType;
        WordOrder = wordOrder;
    }

    /// <summary>这个点位占几个寄存器（16 位 1 个 / 32 位 2 个 / 64 位 4 个）</summary>
    [JsonIgnore]
    public int RegisterCount => PlcValueCodec.RegisterCount(DataType);

    /// <summary>占多个寄存器（32/64 位）：只有这种点位才谈得上「字序」</summary>
    [JsonIgnore]
    public bool IsMultiRegister => RegisterCount > 1;

    /// <summary>点位占用的最后一个寄存器地址（协议地址，含）</summary>
    [JsonIgnore]
    public int EndAddress => Address + RegisterCount - 1;

    /// <summary>人读地址：协议地址 0 对应 40001；占多个寄存器时显示成一段</summary>
    [JsonIgnore]
    public string DisplayAddress => RegisterCount == 1
        ? $"4{Address + 1:D4}"
        : $"4{Address + 1:D4}~4{EndAddress + 1:D4}";

    /// <summary>默认点位表，与 PlcSimulator 的寄存器映射一致</summary>
    public static IReadOnlyList<PlcTag> Default { get; } =
    [
        new PlcTag(0, "温度", "℃", 0.1, 0),
        new PlcTag(1, "压力", "kPa", 0.01, 0),
        new PlcTag(2, "产量", "件", 1, 0)
    ];
}
