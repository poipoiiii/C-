using NModbus;

namespace PlcSimulator;

/// <summary>
/// 只开放指定数量点位的数据区：越界读写按 Modbus 规范回异常码 02（非法数据地址）。
/// NModbus 约定用 <see cref="InvalidModbusRequestException"/> 把异常码带回主站，
/// 这样仿真从站的行为就和“不是所有地址都存在的真实 PLC”一致，扫描功能才能验证。
/// </summary>
internal sealed class LimitedPointSource<T> : IPointSource<T>
{
    private readonly T[] _points;

    public LimitedPointSource(int count)
    {
        _points = new T[count];
    }

    public int Count => _points.Length;

    /// <summary>被主站读取时触发，参数为（起始地址, 数量）</summary>
    public event Action<ushort, ushort>? Read;

    public T[] ReadPoints(ushort startAddress, ushort numberOfPoints)
    {
        ValidateRange(startAddress, numberOfPoints);
        Read?.Invoke(startAddress, numberOfPoints);

        var result = new T[numberOfPoints];
        Array.Copy(_points, startAddress, result, 0, numberOfPoints);
        return result;
    }

    public void WritePoints(ushort startAddress, T[] points)
    {
        ValidateRange(startAddress, (ushort)points.Length);
        Array.Copy(points, 0, _points, startAddress, points.Length);
    }

    private void ValidateRange(ushort startAddress, ushort numberOfPoints)
    {
        if (startAddress + numberOfPoints > _points.Length)
        {
            throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalDataAddress);
        }
    }
}

/// <summary>由 <see cref="LimitedPointSource{T}"/> 组成的从站数据区</summary>
internal sealed class LimitedSlaveDataStore : ISlaveDataStore
{
    public LimitedSlaveDataStore(int registerCount, int coilCount)
    {
        HoldingRegisters = new LimitedPointSource<ushort>(registerCount);
        InputRegisters = new LimitedPointSource<ushort>(registerCount);
        CoilDiscretes = new LimitedPointSource<bool>(coilCount);
        CoilInputs = new LimitedPointSource<bool>(coilCount);
    }

    public LimitedPointSource<ushort> HoldingRegisters { get; }

    public LimitedPointSource<ushort> InputRegisters { get; }

    public LimitedPointSource<bool> CoilDiscretes { get; }

    public LimitedPointSource<bool> CoilInputs { get; }

    IPointSource<ushort> ISlaveDataStore.HoldingRegisters => HoldingRegisters;

    IPointSource<ushort> ISlaveDataStore.InputRegisters => InputRegisters;

    IPointSource<bool> ISlaveDataStore.CoilDiscretes => CoilDiscretes;

    IPointSource<bool> ISlaveDataStore.CoilInputs => CoilInputs;
}
