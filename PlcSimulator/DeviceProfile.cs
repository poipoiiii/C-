namespace PlcSimulator;

/// <summary>一张表的一列：列名 + 该列原始值的量程（闭区间，含端点）</summary>
internal sealed record MachineColumn(string Name, ushort Min, ushort Max);

/// <summary>
/// 机型档案：一种机台的模拟数据长什么样。
/// 上位机的机台号 = 机型代号（4 字母）+ 台号（3 位数字），这里只按机型代号模拟 ——
/// 从站号 1~5 依次对应五种机型，一个从站只放一种机型的数据（台号不区分）。
///
/// 数据布局：40001~40100 共 100 个保持寄存器，每 5 个一个区段，
/// 一个区段就是一张表，段里的 5 个寄存器是那张表的 5 列；20 个区段各自独立随机。
/// </summary>
internal sealed record MachineSpec(string Code, string Name, IReadOnlyList<MachineColumn> Columns)
{
    /// <summary>一个从站的区段数（= 表数）</summary>
    public const int SegmentCount = 20;

    /// <summary>一个区段的寄存器数（= 一张表的列数）</summary>
    public const int RegistersPerSegment = 5;

    /// <summary>一个从站开放的寄存器总数：20 × 5 = 100（40001~40100）</summary>
    public const int RegisterCount = SegmentCount * RegistersPerSegment;

    /// <summary>第 index 个区段（0 开始）对应的表名，如 AIXP01</summary>
    public string SegmentName(int index) => $"{Code}{index + 1:D2}";

    /// <summary>第 index 个区段的地址范围（人读地址），如 40001~40005</summary>
    public string SegmentRange(int index)
    {
        int first = index * RegistersPerSegment + 1;
        return $"4{first:D4}~4{first + RegistersPerSegment - 1:D4}";
    }

    /// <summary>
    /// 五种机型：顺序即从站号（从站 1 = AIXP … 从站 5 = AIPC）。
    /// 列的量程只是仿真用的占位值，现场点表确定后按真实量程改这几行即可，其余逻辑不用动。
    /// </summary>
    public static IReadOnlyList<MachineSpec> All { get; } =
    [
        new("AIXP", "分离封装机",
        [
            new("温度", 200, 900),
            new("压力", 300, 3000),
            new("速度", 0, 3000),
            new("位置", 0, 10000),
            new("计数", 0, 60000)
        ]),
        new("AITN", "辊压机",
        [
            new("辊温", 200, 1500),
            new("辊压", 500, 8000),
            new("线速", 100, 2500),
            new("张力", 50, 900),
            new("计数", 0, 60000)
        ]),
        new("AIBM", "成型机",
        [
            new("模温", 300, 1200),
            new("合模力", 1000, 5000),
            new("节拍", 100, 900),
            new("良品数", 0, 60000),
            new("不良数", 0, 500)
        ]),
        new("AIIR", "提纯机",
        [
            new("炉温", 500, 2000),
            new("真空度", 0, 1000),
            new("功率", 200, 3000),
            new("流量", 100, 2000),
            new("批次", 0, 60000)
        ]),
        new("AIPC", "大片机",
        [
            new("水温", 150, 600),
            new("切割速度", 500, 4000),
            new("张力", 100, 1500),
            new("厚度", 50, 800),
            new("计数", 0, 60000)
        ])
    ];
}

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

    /// <summary>
    /// 会被每秒刷新的数据区寄存器数量；握手区排在数据区之后，由 <see cref="HandshakeDevice"/> 单独维护，
    /// 不会被每秒刷新冲掉。不填就等于整个 RegisterCount 都是数据区。
    /// </summary>
    public int? DataRegisterCount { get; init; }

    /// <summary>握手测试设备的信号/应答约定；不填表示这台设备不参与握手</summary>
    public HandshakeSpec? Handshake { get; init; }

    /// <summary>每秒刷新的数据区大小</summary>
    public int DataCount => DataRegisterCount ?? RegisterCount;

    /// <summary>点位说明，启动时打印出来，方便照着扫描；多行用 \n 分隔</summary>
    public required string Map { get; init; }

    /// <summary>按运行秒数刷新寄存器数组（数组长度 = RegisterCount）</summary>
    public required Action<double, ushort[]> Fill { get; init; }

    /// <summary>从站 1~5 = 五种机型（各 100 个寄存器 = 20 张表），最后一个从站 = 握手测试设备</summary>
    public static IReadOnlyList<DeviceProfile> CreateAll()
    {
        var profiles = new List<DeviceProfile>();

        for (int i = 0; i < MachineSpec.All.Count; i++)
        {
            var spec = MachineSpec.All[i];
            byte unitId = (byte)(i + 1);

            profiles.Add(new DeviceProfile
            {
                UnitId = unitId,
                Name = $"{unitId}# {spec.Code} · {spec.Name}",
                RegisterCount = MachineSpec.RegisterCount,
                Map = DescribeMachine(spec),
                Fill = (_, values) => FillMachine(spec, values)
            });
        }

        // 握手测试设备：模拟「热封/冲压来了一个信号 → 上位机记一条 → 握手复位」的现场时序。
        // 数据区 0~19 每秒刷新；20/21 是握手区，由 HandshakeDevice 自己维护，不会被刷新冲掉。
        profiles.Add(new DeviceProfile
        {
            UnitId = (byte)(MachineSpec.All.Count + 1),
            Name = $"{MachineSpec.All.Count + 1}# 握手测试",
            RegisterCount = 24,
            DataRegisterCount = 20,
            Map = "0~19=数据区(每秒随机刷新) 20=信号(本设备置1) 21=应答(上位机置1) 22~23=未使用",
            Fill = (_, values) =>
            {
                values[0] = (ushort)Random.Shared.Next(0, 1000);
                values[1] = (ushort)Random.Shared.Next(0, 1000);
            },
            Handshake = new HandshakeSpec(SignalAddress: 20, AckAddress: 21, IntervalMs: 500)
        });

        return profiles;
    }

    /// <summary>按区段刷随机值：区段内第 j 个寄存器取该机型第 j 列的量程，20 个区段各自独立</summary>
    private static void FillMachine(MachineSpec spec, ushort[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            var column = spec.Columns[i % MachineSpec.RegistersPerSegment];
            values[i] = (ushort)Random.Shared.Next(column.Min, column.Max + 1);
        }
    }

    private static string DescribeMachine(MachineSpec spec)
    {
        int last = MachineSpec.SegmentCount - 1;

        string tables = $"{MachineSpec.SegmentCount} 张表 × {MachineSpec.RegistersPerSegment} 个寄存器：" +
            $"{spec.SegmentName(0)}({spec.SegmentRange(0)}) ~ {spec.SegmentName(last)}({spec.SegmentRange(last)})";

        string columns = string.Join(" | ", spec.Columns.Select(c => $"{c.Name} {c.Min}~{c.Max}"));

        return $"{tables}\n每个区段的 5 列（顺序固定）：{columns}";
    }
}
