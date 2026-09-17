# C# 上位机最小可行性方案（Modbus TCP → 数据库）

## 提示词（可直接复制给 AI）

```
你是一个 C# 上位机开发专家。请为我搭建一个最小可行性的工业数据采集程序。

【业务场景】
厂内设备通过 Modbus TCP 协议暴露寄存器数据。我需要读取指定寄存器的数值，
经过简单的缩放换算后，存入数据库。

【技术约束】
- 语言/框架：C# + .NET 8
- 通信协议：Modbus TCP
- 通信库：NModbus（如果后续要扩展多品牌 PLC，可换成 HslCommunication）
- 数据库：SQLite（最小可行，无需额外安装数据库服务；后续可换 SQL Server/MySQL）
- ORM：Entity Framework Core（Code First）
- UI：先做控制台程序（Console），跑通后再套 WPF + HandyControl

【具体需求】
1. 读取 Modbus TCP 保持寄存器，地址例如 40001，读取 1 个寄存器
2. 原始值为 short（16位有符号整数），按规则换算为工程值：
   工程值 = 原始值 × Scale + Offset
   例如 Scale=0.1, Offset=0，原始值 500 → 50.0
3. 每次采集记录：时间戳、寄存器地址、原始值、工程值
4. 存入 SQLite 数据库，表名 DataRecords
5. 采集周期 1 秒，循环运行，控制台打印每次采集结果
6. 代码结构清晰，方便后续替换为 WPF 界面和真实 PLC

【请输出】
1. 项目文件结构（目录树）
2. 每个文件的完整代码
3. NuGet 包安装命令
4. 运行说明
```

---

## 最小可行性方案（MVP）

> 说明：这是最早的设计稿与入门示例。当时那个独立的控制台工程 `PlcDataCollector` 已从仓库移除，采集与落库能力现由 `PlcHmi` 承担，下面的代码保留作学习参考。
>
> 另外：现在全项目统一走 ADO.NET 动态表（表名/列名要到运行时才知道），已经不再使用 EF Core，
> 下面基于 EF 的写法属于当时的方案；两种写法的取舍见 README 的「数据库连接与表结构」一节。

### 项目结构

```
PlcDataCollector/
├── PlcDataCollector.csproj
├── Program.cs                    # 入口，采集循环
├── Models/
│   └── DataRecord.cs             # 数据库实体
├── Data/
│   └── AppDbContext.cs           # EF Core 上下文
├── Services/
│   ├── ModbusService.cs          # Modbus 通信封装
│   └── DataCollectionService.cs  # 采集 + 换算 + 存储
└── appsettings.json              # 配置（IP、端口、地址、缩放系数）
```

### 核心代码

**1. 实体模型 `Models/DataRecord.cs`**

```csharp
public class DataRecord
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int RegisterAddress { get; set; }
    public short RawValue { get; set; }
    public double EngineeringValue { get; set; }
}
```

**2. 数据库上下文 `Data/AppDbContext.cs`**

```csharp
using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext
{
    public DbSet<DataRecord> DataRecords { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite("Data Source=plc_data.db");
    }
}
```

**3. Modbus 通信封装 `Services/ModbusService.cs`**

```csharp
using NModbus;
using System.Net.Sockets;

public class ModbusService : IDisposable
{
    private TcpClient _tcpClient;
    private IModbusMaster _master;

    public void Connect(string ip, int port, byte slaveId = 1)
    {
        _tcpClient = new TcpClient(ip, port);
        var factory = new ModbusFactory();
        _master = factory.CreateMaster(_tcpClient);
        _slaveId = slaveId;
    }

    private byte _slaveId;

    public short ReadRegister(ushort address)
    {
        // 读取 1 个保持寄存器
        ushort[] result = _master.ReadHoldingRegisters(_slaveId, address, 1);
        return (short)result[0];
    }

    public void Dispose()
    {
        _master?.Dispose();
        _tcpClient?.Close();
    }
}
```

> **地址说明**：Modbus 协议中“40001”是**人读地址**，实际协议地址是 `40001 - 40001 = 0`。如果设备文档写的是 40001，代码里传 `0`；如果写的是 0-based 地址，直接传原值。NModbus 的 `ReadHoldingRegisters` 参数是 **0-based 协议地址**。

**4. 采集服务 `Services/DataCollectionService.cs`**

```csharp
public class DataCollectionService
{
    private readonly ModbusService _modbus;
    private readonly AppDbContext _db;
    private readonly ushort _address;
    private readonly double _scale;
    private readonly double _offset;

    public DataCollectionService(ModbusService modbus, AppDbContext db,
        ushort address, double scale, double offset)
    {
        _modbus = modbus;
        _db = db;
        _address = address;
        _scale = scale;
        _offset = offset;
    }

    public async Task CollectOnceAsync()
    {
        short raw = _modbus.ReadRegister(_address);
        double eng = raw * _scale + _offset;

        var record = new DataRecord
        {
            Timestamp = DateTime.Now,
            RegisterAddress = _address,
            RawValue = raw,
            EngineeringValue = eng
        };

        _db.DataRecords.Add(record);
        await _db.SaveChangesAsync();

        Console.WriteLine($"[{record.Timestamp:HH:mm:ss}] " +
            $"地址={_address} 原始={raw} 工程值={eng:F2}");
    }
}
```

**5. 入口 `Program.cs`**

```csharp
using Microsoft.EntityFrameworkCore;

// 初始化数据库（首次运行自动建表）
using (var db = new AppDbContext())
{
    db.Database.EnsureCreated();
}

using var modbus = new ModbusService();
modbus.Connect("192.168.1.10", 502);   // 改成你的 PLC IP

using var db2 = new AppDbContext();
var collector = new DataCollectionService(
    modbus, db2,
    address: 0,        // 40001 对应协议地址 0
    scale: 0.1,
    offset: 0
);

while (true)
{
    try
    {
        await collector.CollectOnceAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"采集失败：{ex.Message}");
    }
    await Task.Delay(1000);
}
```

**6. 安装命令**

```bash
dotnet add package NModbus
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
```

**7. 运行**

```bash
dotnet run
```

程序会在当前目录生成 `plc_data.db`，每秒写入一条记录。

---

## 从 0 开始的整体框架演进路径

跑通 MVP 后，按以下顺序逐步升级，每一步都保持可运行：

### 阶段一：控制台 MVP（当前）
**目标**：验证通信通、数据能存。
**产出**：Console 程序 + SQLite。

### 阶段二：配置外置
把 IP、端口、地址、缩放系数移到 `appsettings.json`，用 `Microsoft.Extensions.Configuration` 读取。避免改代码重新编译。

```json
{
  "Plc": {
    "Ip": "192.168.1.10",
    "Port": 502,
    "Registers": [
      { "Address": 0, "Name": "温度", "Scale": 0.1, "Offset": 0 },
      { "Address": 1, "Name": "压力", "Scale": 0.01, "Offset": 0 }
    ]
  }
}
```

### 阶段三：支持多寄存器 + 变量映射表
把“地址 → 变量名 → 缩放规则 → 数据库字段”做成一张配置表，采集服务循环遍历。这是从“玩具”走向“可用”的关键一步。

### 阶段四：WPF + HandyControl 界面
- 用 Prism 搭 MVVM 骨架
- 主界面显示实时数据表格 + 实时曲线（LiveCharts）
- 后台采集用 `Task.Run` 或独立线程，通过 EventAggregator 通知 UI 更新

### 阶段五：工程化加固
- **通信健壮性**：自动重连、超时、断线告警（HslCommunication 自带，NModbus 需自己包一层）
- **数据库选型**：SQLite → SQL Server / MySQL（数据量大时）
- **批量写入**：攒一批再 `SaveChanges`，避免每秒一次 I/O
- **日志**：Serilog 或 NLog
- **异常隔离**：采集线程崩溃不能拖垮整个程序

### 阶段六：功能扩展
- 历史数据查询界面
- 报警规则引擎（超阈值触发）
- 对接 MES / ERP（HTTP API 或数据库直连）
- 多设备并行采集（每台设备一个采集任务）

---

## 关键决策点提醒

| 决策 | MVP 选择 | 后续升级 |
|---|---|---|
| 通信库 | NModbus（够用） | HslCommunication（多品牌） |
| 数据库 | SQLite（零配置） | SQL Server / MySQL |
| UI | Console（先跑通） | WPF + HandyControl |
| 架构 | 简单分层 | Prism MVVM |
| 写入策略 | 每条 `SaveChanges` | 批量攒批写入 |

**建议**：先把阶段一和阶段二跑通，确认能稳定读到 PLC 数据并存入数据库。这两步走稳了，后面套 UI 只是换一层皮，核心逻辑不用动。