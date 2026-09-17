namespace PlcSimulator;

/// <summary>
/// 一台仿真设备的档案：从站号、名称、开放多少个保持寄存器、点位说明、每秒刷新规则。
/// 真实产线上一个网关（同一个 IP:端口）后面可以挂多台 PLC，靠从站号区分，
/// 这里就按这个模型模拟：同一个端口上挂多个从站，各自数据互不相干。
/// </summary>
internal sealed class DeviceProfile
{
    public required byte UnitId { get; init; }

    public required string Name { get; init; }

    /// <summary>开放的保持寄存器数量：协议地址 0 ~ RegisterCount-1</summary>
    public required int RegisterCount { get; init; }

    /// <summary>点位说明，启动时打印出来，方便照着扫描</summary>
    public required string Map { get; init; }

    /// <summary>按运行秒数刷新寄存器数组（数组长度 = RegisterCount）</summary>
    public required Action<double, ushort[]> Fill { get; init; }

    public static IReadOnlyList<DeviceProfile> CreateAll() =>
    [
        new DeviceProfile
        {
            UnitId = 1,
            Name = "1# 挤出机",
            RegisterCount = 20,
            Map = "0=温度(x0.1) 1=压力(x0.01) 2=产量 3=设定温度(x0.1,恒定) 4=运行状态(0/1) 5~19=未使用",
            Fill = (t, v) =>
            {
                v[0] = (ushort)(500 + 400 * Math.Sin(t / 10));    // 100~900 → 10.0~90.0 ℃
                v[1] = (ushort)(300 + 200 * Math.Sin(t / 5));     // 压力
                v[2] = (ushort)((long)t % 10000);                 // 产量计数
                v[3] = 500;                                       // 设定温度，恒定
                v[4] = (ushort)((long)t % 2);                     // 运行状态，0/1 交替
            }
        },
        new DeviceProfile
        {
            UnitId = 2,
            Name = "2# 注塑机",
            RegisterCount = 50,
            Map = "0=料筒温度(x0.1) 1=注射压力 2=锁模力 3=周期时间(x0.01,恒定) 4=合模位置 5=良品数 6=不良品数 7~49=未使用",
            Fill = (t, v) =>
            {
                v[0] = (ushort)(2500 + 500 * Math.Sin(t / 8));    // 200.0~300.0 ℃
                v[1] = (ushort)(800 + 600 * Math.Sin(t / 4));     // 注射压力
                v[2] = (ushort)(1000 + 400 * Math.Sin(t / 12));   // 锁模力
                v[3] = 3500;                                      // 周期时间，恒定 → 35.00 s
                v[4] = (ushort)((long)t * 120 % 10000);           // 合模位置，来回走
                v[5] = (ushort)((long)t * 3);                     // 良品数
                v[6] = (ushort)((long)t / 20);                    // 不良品数，涨得慢
            }
        },
        new DeviceProfile
        {
            UnitId = 3,
            Name = "3# 公用工程",
            RegisterCount = 10,
            Map = "0=冷却水温(x0.1) 1=流量(x0.01) 2=总电耗 3=报警字(0/1) 4~9=未使用",
            Fill = (t, v) =>
            {
                v[0] = (ushort)(220 + 60 * Math.Sin(t / 15));     // 22.0~28.0 ℃
                v[1] = (ushort)(1500 + 300 * Math.Sin(t / 7));    // 流量
                v[2] = (ushort)((long)t * 2);                     // 累计电耗
                v[3] = (ushort)((long)t % 30 == 0 ? 1 : 0);       // 报警字，偶尔置 1
            }
        }
    ];
}
