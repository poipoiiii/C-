using System.Net;
using System.Net.Sockets;
using NModbus;

namespace PlcSimulator;

/// <summary>
/// 本地仿真 PLC：用 NModbus 在同一个端口上挂多个从站，模拟“一个网关后面接多台 PLC”。
/// 每台设备开放各自的寄存器范围，越界地址返回异常码 02（非法数据地址），
/// 客户端用「IP + 端口 + 从站号」就能读到对应设备的全部数据。
///
/// 从站 1~5 = 五种机台（AIXP / AITN / AIBM / AIIR / AIPC），一个从站只放一种机型的数据，
/// 各开放 40001~40100：每 5 个寄存器一个区段（= 一张表），共 20 张表，每秒各自随机刷新。
/// 最后一个从站是握手测试设备（信号位 → 应答位），用来验证触发记录，不参与机台数据。
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
        var handshakes = new List<HandshakeDevice>();

        var factory = new ModbusFactory();
        using var network = factory.CreateSlaveNetwork(listener);

        foreach (var profile in profiles)
        {
            var store = new LimitedSlaveDataStore(profile.RegisterCount, CoilCount);
            byte unitId = profile.UnitId;

            // 打印主站的每次读请求，带上从站号，便于确认采的是哪台设备。
            // 握手设备不打印：上位机以 100ms 的节奏轮询信号位，打出来会把控制台刷满，
            // 它的时序改由 HandshakeDevice 自己记录。
            if (profile.Handshake is null)
            {
                store.HoldingRegisters.Read += (start, count) =>
                    Console.WriteLine($"  <- 从站 {unitId}（{profile.Name}）读保持寄存器：起始={start} 数量={count}");
            }

            stores[unitId] = store;
            network.AddSlave(factory.CreateSlave(unitId, store));

            if (profile.Handshake is not null)
            {
                handshakes.Add(new HandshakeDevice(unitId, profile.Name, profile.Handshake, store));
            }
        }

        Console.WriteLine($"仿真 PLC 已启动：{IPAddress.Loopback}:{Port}，同一端口挂 {profiles.Count} 个从站");
        Console.WriteLine("客户端用「IP + 端口 + 从站号」定位设备，越界地址回异常码 02；单次读保持寄存器上限 125 个\n");
        foreach (var profile in profiles)
        {
            Console.WriteLine($"  从站 {profile.UnitId} | {profile.Name} | 开放地址 0~{profile.RegisterCount - 1}" +
                $"（40001~4{profile.RegisterCount:D4}）");

            // Map 允许写成多行（机台的点位说明会比较长），逐行缩进打印
            foreach (var line in profile.Map.Split('\n'))
            {
                Console.WriteLine($"           {line}");
            }
        }

        foreach (var handshake in handshakes)
        {
            Console.WriteLine($"\n  ▶ {handshake.Describe()}");
            Console.WriteLine("     上位机侧把它配成一条触发规则（信号 20 / 应答 21），连上就能看到握手与触发记录。");
        }

        Console.WriteLine("\n按 Ctrl+C 停止。\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // 拦下 Ctrl+C，走正常退出流程
            cts.Cancel();
        };

        var simTask = Task.Run(() => SimulateAsync(profiles, stores, handshakes, cts.Token));

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
    /// 仿真主循环。分两个节奏：数据每秒刷新一次（真实设备的采样节奏），
    /// 握手状态机每 100ms 推一次 —— 握手一轮要走四步、还要应对每秒 1~2 次的触发，
    /// 用 1 秒的节奏推会直接成为瓶颈、把上位机的握手拖挂。
    /// </summary>
    private static async Task SimulateAsync(
        IReadOnlyList<DeviceProfile> profiles,
        IReadOnlyDictionary<byte, LimitedSlaveDataStore> stores,
        IReadOnlyList<HandshakeDevice> handshakes,
        CancellationToken token)
    {
        const int TickMs = 100;
        const int RefreshMs = 1000;

        var start = DateTime.Now;
        var buffers = profiles.ToDictionary(p => p.UnitId, p => new ushort[p.RegisterCount]);
        var nextRefreshAt = DateTime.Now;

        while (!token.IsCancellationRequested)
        {
            var now = DateTime.Now;

            if (now >= nextRefreshAt)
            {
                double t = (now - start).TotalSeconds;
                foreach (var profile in profiles)
                {
                    var values = buffers[profile.UnitId];
                    Array.Clear(values);
                    profile.Fill(t, values);

                    // 只写数据区：握手区由 HandshakeDevice 自己维护，写全量会把信号/应答冲掉
                    stores[profile.UnitId].HoldingRegisters.WritePoints(0, values[..profile.DataCount]);
                }

                Console.WriteLine($"[{now:HH:mm:ss}] " + string.Join("  |  ",
                    profiles.Select(p => FormatLine(p, buffers[p.UnitId]))));

                nextRefreshAt = now.AddMilliseconds(RefreshMs);
            }

            foreach (var handshake in handshakes)
            {
                handshake.Step(now);
            }

            try
            {
                await Task.Delay(TickMs, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 每秒一行摘要：机台从站打印第一个和最后一个区段（各 5 个值），
    /// 100 个寄存器全打会把控制台刷满，取首尾两段就够看出「每个区段各自在随机」。
    /// </summary>
    private static string FormatLine(DeviceProfile profile, ushort[] values)
    {
        if (profile.Handshake is not null)
        {
            return $"{profile.Name} 数据0={values[0]} 数据1={values[1]}（信号/应答见上面的握手日志）";
        }

        int segmentCount = values.Length / MachineSpec.RegistersPerSegment;
        int lastStart = values.Length - MachineSpec.RegistersPerSegment;

        return $"{profile.Name} 区段01=[{JoinSegment(values, 0)}] … " +
            $"区段{segmentCount:D2}=[{JoinSegment(values, lastStart)}]";
    }

    private static string JoinSegment(ushort[] values, int start) =>
        string.Join(" ", Enumerable.Range(start, MachineSpec.RegistersPerSegment).Select(i => values[i]));
}
