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

    [ObservableProperty] private StorageMode _mode = StorageMode.Long;

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

    public StorageGroup(string name, string tableName, StorageMode mode, ushort startAddress, ushort count)
    {
        Name = name;
        TableName = tableName;
        Mode = mode;
        StartAddress = startAddress;
        Count = count;
    }

    /// <summary>这个地址是否落在本组区段内</summary>
    public bool Covers(ushort address) => Count > 0 && address >= StartAddress && address <= EndAddress;

    /// <summary>深拷贝：界面上的组表是草稿，提交时才覆盖生效配置</summary>
    public StorageGroup Clone() => new(Name, TableName, Mode, StartAddress, Count);
}

/// <summary>界面配置：采集点位表 + 存储组，存成 plc-config.json</summary>
public sealed class PlcConfig
{
    public ObservableCollection<PlcTag> Tags { get; set; } = [];

    /// <summary>存储组：一段寄存器区段 → 一张表</summary>
    public ObservableCollection<StorageGroup> Groups { get; set; } = [];

    /// <summary>触发记录规则：信号位 → 记录 → 应答，握手复位</summary>
    public ObservableCollection<TriggerRule> Triggers { get; set; } = [];

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
