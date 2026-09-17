using System.Collections.ObjectModel;

namespace PlcHmi.Models;

/// <summary>落库结构：长表（一行一个点位）/ 宽表（一行一个时刻，点位成列）</summary>
public enum StorageMode
{
    Long,
    Wide
}

/// <summary>存储模式的下拉项</summary>
public sealed record StorageModeOption(StorageMode Mode, string Display);

/// <summary>目标表配置</summary>
public sealed class StorageConfig
{
    /// <summary>目标表名，默认沿用采集项目已有的 DataRecords</summary>
    public string TableName { get; set; } = "DataRecords";

    public StorageMode Mode { get; set; } = StorageMode.Long;
}

/// <summary>界面配置：采集点位表 + 存储映射，存成 plc-config.json</summary>
public sealed class PlcConfig
{
    public ObservableCollection<PlcTag> Tags { get; set; } = [];

    public StorageConfig Storage { get; set; } = new();
}
