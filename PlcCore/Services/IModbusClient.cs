namespace PlcCore.Services;

/// <summary>
/// Modbus 客户端能力：采集循环、寄存器扫描、触发监听只依赖这个接口。
/// 抽出接口的实际用处是**可测**：单测塞一个假客户端就能驱动握手时序，
/// 不必真连一台 PLC；将来换 HslCommunication 也只改实现。
/// </summary>
public interface IModbusClient : IDisposable
{
    void Connect(string ip, int port);

    /// <summary>读 1 个保持寄存器（0-based 协议地址）</summary>
    short ReadRegister(ushort address);

    /// <summary>批量读连续保持寄存器；地址非法时实现应抛 <c>SlaveException</c></summary>
    ushort[] ReadRegisters(ushort address, ushort count);

    /// <summary>写 1 个保持寄存器（FC06）</summary>
    void WriteSingleRegister(ushort address, ushort value);

    /// <summary>写 1 个线圈（FC05）</summary>
    void WriteSingleCoil(ushort address, bool value);
}
