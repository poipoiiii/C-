namespace PlcCore.Models;

/// <summary>
/// 一次采集的记录：时间戳 + 寄存器地址 + 原始值 + 工程值
/// </summary>
public class DataRecord
{
    public int Id { get; set; }

    public DateTime Timestamp { get; set; }

    public int RegisterAddress { get; set; }

    public short RawValue { get; set; }

    public double EngineeringValue { get; set; }
}
