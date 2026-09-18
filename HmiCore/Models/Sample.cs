namespace HmiCore.Models;

/// <summary>一次采集的结果：原始值 + 按 Scale/Offset 换算出的工程值</summary>
public sealed record Sample(PlcTag Tag, short RawValue, double EngineeringValue, DateTime Timestamp);
