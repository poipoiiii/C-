using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HmiCore.Comm;

namespace PlcTests;

/// <summary>
/// 主连接的"没响应"行为：现场设备重启、网线松脱、空闲连接被网关悄悄清掉时，
/// 读操作必须**快速失败**，不能永久挂住 —— 挂住的那次读握着连接级互斥（<c>_gate</c>），
/// 界面线程上的断开 / 换从站 / 退出程序都会跟着卡死，也就是现场看到的"程序无响应"。
///
/// 复现办法：起一个只接受 TCP 连接、永不回数据的从站（等价于"线还在、对面不响了"），
/// 不需要真 PLC，也不依赖任何外部环境。
/// </summary>
public class ModbusServiceTests
{
    /// <summary>建一个静默从站（监听但不应答），返回端口</summary>
    private static int StartSilentSlave(out TcpListener listener)
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task 从站不响应时_读操作应在超时内失败而不是永久挂住()
    {
        int port = StartSilentSlave(out var listener);
        using var _ = listener;

        using var modbus = new ModbusService(1);
        modbus.Connect("127.0.0.1", port);

        var stopwatch = Stopwatch.StartNew();
        var reading = Task.Run(() => Record.Exception(() => modbus.ReadRegister(0)));

        // 超时设的是 1 秒；这里给到 6 秒，还没回来就说明超时根本没生效（修之前实测是永久阻塞）
        var finished = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(6)));
        stopwatch.Stop();

        Assert.True(finished == reading,
            $"读操作 {stopwatch.ElapsedMilliseconds} ms 仍未返回：连接超时没生效，会把界面一起拖死");

        var error = await reading;
        Assert.NotNull(error);
        Assert.Contains("通信中断", error!.Message);
    }

    [Fact]
    public async Task 断开连接_不应被卡住的读拖住界面线程()
    {
        int port = StartSilentSlave(out var listener);
        using var _ = listener;

        var modbus = new ModbusService(1);
        modbus.Connect("127.0.0.1", port);

        // 先制造一次"卡在网络上"的读：它会把连接级互斥一直占着
        var stuck = Task.Run(() => Record.Exception(() => modbus.ReadRegister(0)));
        await Task.Delay(100);   // 让它真的进到读写里

        var stopwatch = Stopwatch.StartNew();
        var disposing = Task.Run(modbus.Dispose);
        var finished = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(5)));
        stopwatch.Stop();

        Assert.True(finished == disposing,
            $"Dispose 用了 {stopwatch.ElapsedMilliseconds} ms 还没返回：界面线程会被拖死（断开 / 退出卡住）");

        // 连接关掉之后，那次卡住的读也该以异常收场，不能永远留在那里
        Assert.NotNull(await stuck);
    }
}
