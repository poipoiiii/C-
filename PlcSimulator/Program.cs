using System.Net;
using System.Net.Sockets;
using NModbus;

namespace PlcSimulator;

/// <summary>
/// 本地仿真 PLC：用 NModbus 在同一个端口上挂多个从站，模拟“一个网关后面接多台 PLC”。
/// 每台设备开放各自的寄存器范围，越界地址返回异常码 02（非法数据地址），
/// 客户端用「IP + 端口 + 从站号」就能读到对应设备的全部数据。
/// </summary>
internal static class Program
{
    /// <summary>监听端口，客户端用 127.0.0.1:5020 连接（502 是标准端口，本机常被占用）</summary>
    private const int Port = 5020;

    /// <summary>每个从站开放的线圈数量</summary>
    private const int CoilCount = 20;

    private static async Task Main()
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();

        var profiles = DeviceProfile.CreateAll();
        var stores = new Dictionary<byte, LimitedSlaveDataStore>();

        var factory = new ModbusFactory();
        using var network = factory.CreateSlaveNetwork(listener);

        foreach (var profile in profiles)
        {
            var store = new LimitedSlaveDataStore(profile.RegisterCount, CoilCount);
            byte unitId = profile.UnitId;

            // 打印主站的每次读请求，带上从站号，便于确认采的是哪台设备
            store.HoldingRegisters.Read += (start, count) =>
                Console.WriteLine($"  <- 从站 {unitId}（{profile.Name}）读保持寄存器：起始={start} 数量={count}");

            stores[unitId] = store;
            network.AddSlave(factory.CreateSlave(unitId, store));
        }

        Console.WriteLine($"仿真 PLC 已启动：{IPAddress.Loopback}:{Port}，同一端口挂 {profiles.Count} 个从站");
        Console.WriteLine("客户端用「IP + 端口 + 从站号」定位设备，越界地址回异常码 02；单次读保持寄存器上限 125 个\n");
        foreach (var profile in profiles)
        {
            Console.WriteLine($"  从站 {profile.UnitId} | {profile.Name} | 开放地址 0~{profile.RegisterCount - 1}" +
                $"（40001~4{profile.RegisterCount:D4}）");
            Console.WriteLine($"           {profile.Map}");
        }

        Console.WriteLine("\n按 Ctrl+C 停止。\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // 拦下 Ctrl+C，走正常退出流程
            cts.Cancel();
        };

        var simTask = Task.Run(() => SimulateAsync(profiles, stores, cts.Token));

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
    /// 每秒刷新所有从站的寄存器。真实设备之间互不干扰，这里也各刷各的。
    /// </summary>
    private static async Task SimulateAsync(
        IReadOnlyList<DeviceProfile> profiles,
        IReadOnlyDictionary<byte, LimitedSlaveDataStore> stores,
        CancellationToken token)
    {
        var start = DateTime.Now;
        var buffers = profiles.ToDictionary(p => p.UnitId, p => new ushort[p.RegisterCount]);

        while (!token.IsCancellationRequested)
        {
            double t = (DateTime.Now - start).TotalSeconds;

            foreach (var profile in profiles)
            {
                var values = buffers[profile.UnitId];
                Array.Clear(values);
                profile.Fill(t, values);
                stores[profile.UnitId].HoldingRegisters.WritePoints(0, values);
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] " + string.Join("  |  ",
                profiles.Select(p => FormatLine(p, buffers[p.UnitId]))));

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

    private static string FormatLine(DeviceProfile profile, ushort[] values)
    {
        var text = profile.UnitId switch
        {
            1 => $"1#挤出机 温度={values[0]} 压力={values[1]} 产量={values[2]} 设定={values[3]} 状态={values[4]}",
            2 => $"2#注塑机 料筒={values[0]} 注射={values[1]} 锁模={values[2]} 良品={values[5]} 不良={values[6]}",
            _ => $"3#公用 水温={values[0]} 流量={values[1]} 电耗={values[2]} 报警={values[3]}"
        };

        return text;
    }
}
