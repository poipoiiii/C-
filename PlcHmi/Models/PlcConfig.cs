using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PlcHmi.Models;

/// <summary>落库结构：长表（一行一个点位）/ 宽表（一行一个时刻，点位成列）</summary>
public enum StorageMode
{
    Long,
    Wide
}

/// <summary>存储模式的下拉项</summary>
public sealed record StorageModeOption(StorageMode Mode, string Display);

/// <summary>
/// 存储组：一段连续的寄存器区段 → 一张表。
/// 区段是采集的读取单位（现场就是「从某地址开始读 99 或 20 个作为一组」），
/// 区段里的地址不一定都有点位（比如区段 50 个寄存器、实际只用了 40 个）。
/// 点位靠地址落在哪个区段来归组，不手工指定。
/// </summary>
public partial class StorageGroup : ObservableObject
{
    /// <summary>没配区段时的兜底组名</summary>
    public const string DefaultName = "默认组";

    [ObservableProperty] private string _name = DefaultName;

    [ObservableProperty] private string _tableName = "DataRecords";

    /// <summary>落库结构，默认宽表（一行一个时刻，点位成列，报表/BI 直接可用）</summary>
    [ObservableProperty] private StorageMode _mode = StorageMode.Wide;

    /// <summary>区段起始地址：0-based 协议地址</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndAddress))]
    [NotifyPropertyChangedFor(nameof(DisplayRange))]
    private ushort _startAddress;

    /// <summary>区段长度：从起始地址起连续多少个寄存器（含没点位的空地址）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndAddress))]
    [NotifyPropertyChangedFor(nameof(DisplayRange))]
    private ushort _count = 20;

    /// <summary>建表时要不要自增主键列 [Id]</summary>
    [ObservableProperty] private bool _useIdentityColumn = true;

    /// <summary>建表时要不要时间戳列 [Timestamp]</summary>
    [ObservableProperty] private bool _useTimestampColumn = true;

    /// <summary>这张表"非寄存器列"的取舍（Id / 时间戳由用户定，缺列时插入也不会写它们）</summary>
    [JsonIgnore]
    public TableShape Shape => new(UseIdentityColumn, UseTimestampColumn);

    /// <summary>区段结束地址（含）</summary>
    [JsonIgnore]
    public int EndAddress => StartAddress + Count - 1;

    /// <summary>人读区段，如「40001~40020（20 个）」</summary>
    [JsonIgnore]
    public string DisplayRange => Count == 0
        ? "（空区段）"
        : $"4{StartAddress + 1:D4}~4{EndAddress + 1:D4}（{Count} 个）";

    public StorageGroup()
    {
    }

    public StorageGroup(string name, string tableName, StorageMode mode, ushort startAddress, ushort count,
        bool useIdentityColumn = true, bool useTimestampColumn = true)
    {
        Name = name;
        TableName = tableName;
        Mode = mode;
        StartAddress = startAddress;
        Count = count;
        UseIdentityColumn = useIdentityColumn;
        UseTimestampColumn = useTimestampColumn;
    }

    /// <summary>这个地址是否落在本组区段内</summary>
    public bool Covers(ushort address) => Count > 0 && address >= StartAddress && address <= EndAddress;

    /// <summary>深拷贝：界面上的组表是草稿，提交时才覆盖生效配置</summary>
    public StorageGroup Clone() =>
        new(Name, TableName, Mode, StartAddress, Count, UseIdentityColumn, UseTimestampColumn);
}

/// <summary>
/// 一张表的形状：除了寄存器数据列之外，那两列"附加列"要不要。
/// 由用户按现场要求定 —— 有些表是别人建好的、或者只想要纯数据列。
/// </summary>
public sealed record TableShape(bool UseIdentityColumn, bool UseTimestampColumn)
{
    /// <summary>默认：Id 和时间戳都加</summary>
    public static TableShape Default { get; } = new(true, true);
}

/// <summary>
/// 机型配置：采集点位表 + 存储组 + 触发规则，存成 data/configs/&lt;机型&gt;.json。
/// 只放"这个型号怎么干"的工艺内容 —— 机台号、IP、从站号这些每台机器不一样的东西在
/// <see cref="MachineProfile"/>（data/machine.json）里，配置因此可以同型号整份复用。
/// </summary>
public sealed class PlcConfig
{
    /// <summary>配置格式版本：程序用它拦住「旧配置配新程序」</summary>
    public const int CurrentVersion = 1;

    /// <summary>配置格式版本，写盘时由 ConfigStore.Save 更新</summary>
    public int ConfigVersion { get; set; }

    /// <summary>机型代号：与 data/configs 下的文件名一致（AIXP.json → AIXP）</summary>
    public string MachineName { get; set; } = "";

    public ObservableCollection<PlcTag> Tags { get; set; } = [];

    /// <summary>存储组：一段寄存器区段 → 一张表</summary>
    public ObservableCollection<StorageGroup> Groups { get; set; } = [];

    /// <summary>触发记录规则：信号位 → 记录 → 应答，握手复位</summary>
    public ObservableCollection<TriggerRule> Triggers { get; set; } = [];

    /// <summary>
    /// 老配置把机台号存在机型配置里（那时"一台机器一份配置"）。
    /// 只用于读老文件：启动时搬进 data/machine.json，随后置空、不再写回
    /// （见 MainViewModel.MigrateMachineProfile）。
    /// </summary>
    [JsonPropertyName("MachineNo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyMachineNo { get; set; }

    /// <summary>老配置把连接参数也放在机型配置里；同样只用于读老文件，迁移后置空</summary>
    [JsonPropertyName("Connection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MachineProfile? LegacyConnection { get; set; }

    /// <summary>
    /// 旧版配置只有一个 Storage 段（单表 + 单模式），读进来后由 ConfigStore 迁移成 Groups，
    /// 迁移完置空，不再写回文件。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StorageConfig? Storage { get; set; }
}

/// <summary>旧版的单表映射，仅用于读取老配置文件（见 ConfigStore.Load 的迁移逻辑）</summary>
public sealed class StorageConfig
{
    public string TableName { get; set; } = "DataRecords";

    public StorageMode Mode { get; set; } = StorageMode.Long;
}
