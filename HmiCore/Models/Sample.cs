namespace HmiCore.Models;

/// <summary>
/// 一次采集的结果：原始值 + 按 Scale/Offset 换算出的工程值。
/// 原始值是 double：16 位量就是寄存器值本身，32/64 位量是几个寄存器按
/// <see cref="PlcTag.DataType"/> 与 <see cref="PlcTag.WordOrder"/> 拼出来的解码值。
/// </summary>
public sealed record Sample(PlcTag Tag, double RawValue, double EngineeringValue, DateTime Timestamp);
