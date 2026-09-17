using System.Net.Sockets;
using NModbus;

namespace PlcDataCollector.Services;

/// <summary>
/// Modbus TCP 通信封装，后续可整体替换为 HslCommunication
/// </summary>
public class ModbusService : IDisposable
{
    private readonly byte _slaveId;
    private TcpClient? _tcpClient;
    private IModbusMaster? _master;

    public ModbusService(byte slaveId = 1)
    {
        _slaveId = slaveId;
    }

    public void Connect(string ip, int port)
    {
        _tcpClient = new TcpClient();
        _tcpClient.Connect(ip, port);

        var factory = new ModbusFactory();
        _master = factory.CreateMaster(_tcpClient);
    }

    /// <summary>
    /// 读取 1 个保持寄存器，地址为 0-based 协议地址
    /// </summary>
    public short ReadRegister(ushort address)
    {
        if (_master is null)
        {
            throw new InvalidOperationException("Modbus 尚未连接，请先调用 Connect。");
        }

        ushort[] result = _master.ReadHoldingRegisters(_slaveId, address, 1);
        return (short)result[0];
    }

    /// <summary>
    /// 批量读取连续保持寄存器，地址为 0-based 协议地址。
    /// 地址段非法时从站会回异常码（NModbus 抛 SlaveException），调用方自行捕获。
    /// </summary>
    public ushort[] ReadRegisters(ushort address, ushort count)
    {
        if (_master is null)
        {
            throw new InvalidOperationException("Modbus 尚未连接，请先调用 Connect。");
        }

        return _master.ReadHoldingRegisters(_slaveId, address, count);
    }

    public void Dispose()
    {
        _master?.Dispose();
        _tcpClient?.Dispose();
        _master = null;
        _tcpClient = null;
    }
}
