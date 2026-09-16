using System.Net;
using System.Net.Sockets;
using NModbus;
using NModbus.Data;

namespace PlcSimulator;

/// <summary>
/// 本地仿真 PLC：用 NModbus 起一个 Modbus TCP 从站，周期性刷新保持寄存器，
/// 让 PlcDataCollector 在没有真实 PLC 的情况下也能跑通「通信 → 换算 → 落库」全链路。
/// </summary>
internal static class Program
{
    /// <summary>从站地址，必须与采集端 ModbusService 的 slaveId 一致</summary>
    private const byte SlaveId = 1;

    /// <summary>监听端口，采集端用 127.0.0.1:5020 连接（502 是标准端口，本机常被占用）</summary>
    private const int Port = 5020;

    private static async Task Main()
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();

        var factory = new ModbusFactory();
        var dataStore = new SlaveDataStore();

        // 打印主站的每次读请求，便于确认采集端确实连上来了
        dataStore.HoldingRegisters.BeforeRead += (_, e) =>
            Console.WriteLine($"  <- 主站读保持寄存器：起始={e.StartAddress} 数量={e.NumberOfPoints}");

        using var network = factory.CreateSlaveNetwork(listener);
        network.AddSlave(factory.CreateSlave(SlaveId, dataStore));

        Console.WriteLine($"仿真 PLC 已启动：{IPAddress.Loopback}:{Port}，从站地址={SlaveId}");
        Console.WriteLine("寄存器映射：0=温度(x0.1)  1=压力(x0.01)  2=产量计数");
        Console.WriteLine("按 Ctrl+C 停止。\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // 拦下 Ctrl+C，走正常退出流程
            cts.Cancel();
        };

        var simTask = Task.Run(() => SimulateAsync(dataStore, cts.Token));

        try
        {
            await network.ListenAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C 退出
        }

        await simTask;
        Console.WriteLine("仿真 PLC 已停止。");
    }

    /// <summary>
    /// 每秒刷新一次寄存器，模拟现场变化的数据：
    /// 温度走正弦波，压力走另一条正弦波，产量单调递增。
    /// </summary>
    private static async Task SimulateAsync(SlaveDataStore store, CancellationToken token)
    {
        var start = DateTime.Now;

        while (!token.IsCancellationRequested)
        {
            double t = (DateTime.Now - start).TotalSeconds;

            // 原始值 100~900，配 Scale=0.1 即 10.0~90.0 摄氏度
            ushort temperature = (ushort)(500 + 400 * Math.Sin(t / 10));
            ushort pressure = (ushort)(300 + 200 * Math.Sin(t / 5));
            ushort output = (ushort)((long)t % 10000);

            store.HoldingRegisters.WritePoints(0, new[] { temperature, pressure, output });

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 寄存器0(温度)={temperature} " +
                $"寄存器1(压力)={pressure} 寄存器2(产量)={output}");

            try
            {
                await Task.Delay(1000, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}
