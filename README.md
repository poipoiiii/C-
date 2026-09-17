# PLC 采集监控上位机（C# 新手导读）

一套用 C# / .NET 8 写的工业上位机示例：**Modbus TCP 读 PLC → 换算工程值 → 写数据库 → 界面实时展示**。
本文面向 C# 新手：先讲清「这项目在干什么」，再逐个文件讲「代码为什么这么写」。

- 想直接跑起来：看 [start.md](file:///d:/taoxi_download_for_use/no_use_do/c_Test/start.md)
- 想看设计思路与演进路线：看 [C_study.md](file:///d:/taoxi_download_for_use/no_use_do/c_Test/C_study.md)

---

## 一、业务背景（不懂 PLC 也能读）

| 名词 | 一句话解释 |
|---|---|
| PLC | 工厂里的控制器，内部存着一堆数字（温度、压力、产量…） |
| 寄存器 | PLC 存数字的格子，按地址编号，比如 0 号格子放温度 |
| Modbus TCP | 上位机与 PLC 之间最常见的通信协议，走 TCP，标准端口 502 |
| 从站号 (UnitId/SlaveId) | 一个网关（同一 IP:端口）后面可以挂多台 PLC，靠从站号区分问的是哪台，常见 1~32 |
| 保持寄存器 | 最常用的一种寄存器，16 位，对应 C# 的 `ushort` / `short` |
| 40001 地址 | 人读地址，从 1 开始数；**协议地址从 0 开始**，所以 40001 = 协议地址 0 |
| 原始值 / 工程值 | 寄存器里是 500 这样的整数，实际意思是 50.0 ℃：`工程值 = 原始值 × Scale + Offset` |
| 异常码 02 | 从站回「这个地址我没有」，扫描功能就是靠它区分地址是否存在 |

**换算规则是这个项目唯一的业务逻辑**，其余都是工程化的脚手架。

---

## 二、数据是怎么流动的

```
┌───────────────────────────┐   读保持寄存器（Modbus TCP）   ┌──────────────────────────┐
│ PlcSimulator（仿真 PLC）   │  ◄───────────────────────     │ PlcHmi（WPF 上位机）       │
│ 127.0.0.1:5020            │                                │ 采集循环 / 看板 / 点位配置  │
│ 同一端口挂多个从站：        │  ────────────────────────►     │ 曲线 / 卡片 / 动态表        │
│  1#挤出机 2#注塑机 3#公用   │   返回 ushort[] 或异常码 02     └─────────────┬────────────┘
└───────────────────────────┘                                              │ ADO.NET 动态 SQL
┌───────────────────────────┐   控制台采集（可选，最小示例）                 ▼
│ PlcDataCollector           │  ─────────────────────────►   ┌──────────────────────────┐
│ 无界面的采集程序             │                                │ SQL Server                │
└───────────────────────────┘                                │ DataRecords / 自定义表      │
                                                             └──────────────────────────┘
```

三个可独立运行的项目：

| 项目 | 类型 | 作用 |
|---|---|---|
| [PlcSimulator](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/Program.cs) | 控制台 | 假 PLC：一个端口挂 3 个从站，没真设备也能跑通全链路，还能验证扫描功能 |
| [PlcDataCollector](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Program.cs) | 控制台 | 最小采集程序：读一个寄存器 → 换算 → 存库 → 打印 |
| [PlcHmi](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/MainWindow.xaml) | WPF 桌面 | 完整上位机：连接 / 扫描点位 / 实时曲线 / 动态建表落库 |

`PlcHmi` 通过 `ProjectReference` 复用 `PlcDataCollector` 里的通信与数据库代码（见 [PlcHmi.csproj](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/PlcHmi.csproj#L21-L24)），不重复实现。

配置文件两份，分工明确：

| 文件 | 内容 | 是否入库 |
|---|---|---|
| `.env`（模板 [.env.example](file:///d:/taoxi_download_for_use/no_use_do/c_Test/.env.example)） | 数据库地址、账号、密码 | ❌ 已在 [.gitignore](file:///d:/taoxi_download_for_use/no_use_do/c_Test/.gitignore) 里排除 |
| [plc-config.json](file:///d:/taoxi_download_for_use/no_use_do/c_Test/plc-config.json) | 采集点位表 + 目标表名 + 长表/宽表模式 | ✅ 跟着代码一起版本管理 |

---

## 三、目录结构

```
c_Test/
├── PlcSimulator/                     # 仿真 PLC（从站）
│   ├── Program.cs                    # 起监听、挂多个从站、每秒刷新数据
│   ├── DeviceProfile.cs              # 设备档案：从站号/名称/开放多少寄存器/刷新规则
│   └── LimitedSlaveDataStore.cs      # 只开放指定地址，越界回异常码 02
├── PlcDataCollector/                 # 采集核心（被 PlcHmi 复用）
│   ├── Program.cs                    # 入口：连接 + 每秒采集一次
│   ├── Models/DataRecord.cs          # 数据库实体
│   ├── Data/AppDbContext.cs          # EF Core 上下文（连接串来自 .env）
│   ├── Data/AppDbContextFactory.cs   # 给 dotnet ef 命令用的设计时工厂
│   ├── Services/ModbusService.cs     # Modbus TCP 通信封装（单点读 + 批量读）
│   └── Services/DataCollectionService.cs  # 采集 + 换算 + 落库
├── PlcHmi/                           # WPF 上位机（MVVM）
│   ├── App.xaml(.cs)                 # 启动 + 加载 .env
│   ├── MainWindow.xaml(.cs)          # 三分页界面 / 卡片自适应列数
│   ├── Models/
│   │   ├── PlcTag.cs                 # 点位：地址+名称+单位+Scale+Offset+宽表列名（可编辑）
│   │   ├── PlcConfig.cs              # 配置对象：点位表 + 存储映射（落 plc-config.json）
│   │   ├── Sample.cs / AcquisitionBatch.cs  # 一轮采集结果（成功样本 + 失败点位）
│   │   └── ScanResult.cs             # 扫描结果一行
│   ├── Services/
│   │   ├── PlcAcquisitionService.cs  # 后台采集循环，异常分层
│   │   ├── ModbusScanService.cs      # 寄存器扫描（分段批读 + 失败段逐点定位）
│   │   ├── SlaveProbeService.cs      # 从站号探测（短超时 + 分批并行）
│   │   ├── DynamicTableStore.cs      # 动态建表/写入/查询（表名列名白名单防注入）
│   │   └── ConfigStore.cs            # plc-config.json 读写
│   └── ViewModels/
│       ├── MainViewModel.cs          # 主视图模型（连接/扫描/采集/存储/查询）
│       └── TagViewModel.cs           # 单张点位卡片 + 滚动曲线
├── plc-config.json                   # 界面配置（点位表 + 目标表）
└── start.md                          # 两条运行命令
```

---

## 四、跑起来（两个终端）

先把 `.env.example` 复制成 `.env` 并填好数据库连接，再按 [start.md](file:///d:/taoxi_download_for_use/no_use_do/c_Test/start.md) 分别启动：

```powershell
# 终端 1：仿真 PLC（127.0.0.1:5020，从站 1/2/3）
dotnet run --project PlcSimulator\PlcSimulator.csproj

# 终端 2：上位机界面（默认已填 127.0.0.1:5020，点【连接】即开始采集）
dotnet run --project PlcHmi\PlcHmi.csproj
```

只想跑命令行采集（最小示例）：

```powershell
dotnet run --project PlcDataCollector\PlcDataCollector.csproj -- 127.0.0.1 5020
```

`127.0.0.1 5020` 是命令行参数，对应 [Program.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Program.cs#L8-L10) 的 `args[0]`/`args[1]`，不传则默认 `192.168.1.10:502`。

**界面推荐操作顺序**：连接 → 「采集点位」页扫描 40001~40040 → 勾选可读地址加入点位表、改名称与 Scale → 「存储映射」页选目标表与长/宽表 → 点【建表 / 校验结构】→ 回看板看曲线。

---

## 五、核心代码逐个讲

### 1. 通信层：ModbusService

[ModbusService.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Services/ModbusService.cs)

| 知识点 | 说明 |
|---|---|
| `IDisposable` + `using var` | `TcpClient` 是网络资源，用完必须释放。调用方写 `using var modbus = new ModbusService(1)`，离开作用域自动关连接 |
| `?` 可空引用 | 项目开了 `<Nullable>enable</Nullable>`，`IModbusMaster? _master` 表示「可能还没连」。读之前先判断，把空引用异常换成能看懂的提示（[L34-L37](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Services/ModbusService.cs#L34-L37)） |
| 类型转换 | 寄存器读回来是 `ushort[]`，业务要按有符号看，所以 `(short)result[0]` |
| 两个读方法 | [ReadRegister](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Services/ModbusService.cs#L32-L41) 读单点（采集用）；[ReadRegisters](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Services/ModbusService.cs#L47-L55) 批量读（扫描用，一次最多 125 个） |
| 从站号 | 构造函数固定 `_slaveId`，所以**切换从站要重连**（见下文 MainViewModel） |

单独包一层的好处：以后换 HslCommunication 或别的库，只改这一个文件。

### 2. 数据模型与数据库（EF Core 部分）

- 实体 [DataRecord.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Models/DataRecord.cs)：`Id` 主键 + 时间戳 + 寄存器地址 + 原始值 + 工程值。
- 上下文 [AppDbContext.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Data/AppDbContext.cs)：`DbSet<DataRecord>` 对应 `DataRecords` 表；连接串不写死，用局部函数 `Get` 从环境变量拼出来（[L14-L22](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Data/AppDbContext.cs#L14-L22)），缺配置时给一句清晰报错。
- `.env` 由 `DotNetEnv` 加载，[Env.TraversePath().Load()](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Program.cs#L6) 会从当前目录向上找；WPF 端同理，见 [App.xaml.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/App.xaml.cs#L10-L21)（没有 `.env` 也允许启动，只在读写库时报错）。
- 建表两种方式：`db.Database.EnsureCreated()` 最省事；`Migrations/InitialCreate` 是 EF 迁移脚本，规范做法是 `dotnet ef database update`。**迁移命令要在 `PlcDataCollector` 目录下执行**，否则找不到 `.env`（原因写在 [AppDbContextFactory.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Data/AppDbContextFactory.cs#L7-L11)）。

> 注意：本项目里 EF Core 只负责「控制台采集 + 固定表 `DataRecords`」。界面上的动态表（表名/列名可变）走的是 ADO.NET，见第 6 节 —— 因为 EF 的实体是编译期固定的。

### 3. 最小采集服务：读 → 换算 → 存

[DataCollectionService.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Services/DataCollectionService.cs#L27-L45)，核心就三行：

```csharp
short raw = _modbus.ReadRegister(_address);   // 1. 读原始值
double eng = raw * _scale + _offset;          // 2. 换算工程值
_db.DataRecords.Add(record);
await _db.SaveChangesAsync();                 // 3. 异步写库
```

新手要点：**构造函数注入**（依赖从外面给，不在类里 `new`，字段用 `readonly`）、**`async/await`**（等数据库 IO 时不占线程）、**字符串插值**（`$"...{eng:F2}"` 保留两位小数）。
入口 [Program.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Program.cs#L29-L41) 是 `while (true)` + `Task.Delay(1000)`，每轮都包 `try/catch` —— **一次失败不能让程序退出**，这是工业软件和练习代码最大的区别。

### 4. 仿真 PLC：怎么模拟「一个网关挂多台设备」

- [DeviceProfile.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/DeviceProfile.cs)：一台设备的档案。`required` 属性表示「创建时必须赋值」；[Fill](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/DeviceProfile.cs#L20-L21) 是一个 `Action<double, ushort[]>` 委托，把「按秒数刷新寄存器」这条规则当成数据传给框架 —— 这是**把函数当参数**的典型用法。
- [Program.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/Program.cs)：同一端口上 `AddSlave` 多个从站，各自数据互不相干；每秒刷新并打印。
- [LimitedSlaveDataStore.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/LimitedSlaveDataStore.cs#L40-L46)：越界读写抛 `InvalidModbusRequestException(IllegalDataAddress)`，即回异常码 02。**这样仿真器和真实 PLC 行为一致**——不是所有地址都存在，扫描功能才有得验证。
- [Ctrl+C 处理](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/Program.cs#L55-L60)：不直接杀进程，而是 `cts.Cancel()` 让循环正常收尾，这是 .NET 推荐的任务取消方式。

### 5. 界面结构：三个页签

[MainWindow.xaml](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/MainWindow.xaml) 顶部是共用工具条（连接/从站/采集控制），下面是 `TabControl`：

| 页签 | 干什么 |
|---|---|
| 实时看板 | 每点位一张卡片（大数字 + 原始值 + 60 点滚动曲线），下方是数据库最近 50 条记录 |
| 采集点位 | 扫描寄存器 → 勾选加入点位表 → 编辑名称/单位/Scale/Offset/宽表列名 |
| 存储映射 | 选目标表与长表/宽表 → 预览建表语句 → 建表/校验结构 |

几个 WPF 技巧：

- **卡片列数自适应**：[MainWindow.xaml.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/MainWindow.xaml.cs#L14-L40) 定义了一个依赖属性 `CardColumns`，卡片区尺寸变化时按宽度重算一行放几张，绑给 `UniformGrid.Columns`。用 `UniformGrid` 而不是 `WrapPanel`，是因为前者会均分宽度、把整行排满。
- **状态灯三态**：连上且无错→绿、连上有错→红、未连接→灰，靠 `MultiDataTrigger` 实现（[L126-L137](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/MainWindow.xaml#L126-L137)）。
- **最近记录表列不固定**：长表/宽表列不同，所以 `AutoGenerateColumns="True"` 直接绑一个 `DataView`（[L276-L278](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/MainWindow.xaml#L276-L278)）。
- **从站号框回车即生效**：绑定是 `LostFocus` 提交，[PreviewKeyDown](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/MainWindow.xaml.cs#L42-L53) 里手动 `UpdateSource()`，免得「敲了字没生效」的误会。

### 6. 动态表：DynamicTableStore（本项目最值得看的一段）

[DynamicTableStore.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/DynamicTableStore.cs)

需求是「表名、列名由用户在界面上填」，EF 实体做不到，只能用 ADO.NET 拼 SQL。**拼 SQL 就有注入风险**，所以这里的处理方式是：

1. 表名列名**不能参数化**，一律先过白名单正则 [`^[\u4e00-\u9fa5A-Za-z_][\u4e00-\u9fa5A-Za-z0-9_]{0,63}$`](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/DynamicTableStore.cs#L18-L32)（中英文/数字/下划线，不能数字开头）；
2. 再用 `[ ]` 包裹并转义 `]`（[Quote](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/DynamicTableStore.cs#L296)）；
3. **数据值一律走参数**（时间、原始值、工程值…都是 `@参数`）。

两种存储模式：

| 模式 | 结构 | 特点 |
|---|---|---|
| 长表 Long | 一行一个点位：时间/地址/原始值/工程值 | 点位增减不用改表结构，最通用（`DataRecords` 就是长表） |
| 宽表 Wide | 一行一个时刻，每个点位一列 | 查起来直观、适合做报表；点位增减要同步列 |

宽表有个容易踩的坑，代码专门处理了（[EnsureWideColumnsAsync](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/DynamicTableStore.cs#L136-L196)）：

- 缺的点位列 → `ALTER TABLE ... ADD` 补上；
- 点位列是 `NOT NULL` 的 → 放宽成 `NULL`（某个点位本次读失败时该列写 NULL，不该让整行插入失败）；
- 表里存在**既非空、又不属于当前点位表**的列（比如长表改建出来的 `RawValue`）→ 提前点名为「⚠ 阻断列」，因为插入语句不会写它，SQL Server 会直接报错（[L184-L193](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/DynamicTableStore.cs#L184-L193)）。

`await using var db = new AppDbContext()` + 用完即释放：**DbContext 不是线程安全的，一次操作一个上下文**最稳妥。

### 7. 点位扫描与从站探测

[ModbusScanService.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/ModbusScanService.cs)：Modbus 没有「列出所有点位」的指令，只能试读。策略是**分段批量读**，整段成功就整段标记可读；某段失败再**逐点重读**，定位到底是哪几个地址不可读（[L37-L68](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/ModbusScanService.cs#L37-L68)）。每段/每点之间 Sleep 一下，避免把真实 PLC 打满。取消靠 `CancellationToken`，`token.ThrowIfCancellationRequested()` 在每个循环开头检查。

[SlaveProbeService.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/SlaveProbeService.cs)：一个网关后面可能挂多台 PLC。做法是**短超时 + 分批并行**（每批 8 个 [L19](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/SlaveProbeService.cs#L19)）：

- 读到数据、或回了 Modbus 异常码 → 判定「从站存在」（回了异常码说明它活着，只是地址没定义）；
- 连不上/超时 → 判定不存在；
- `Task.WhenAll` 等一批全部结束，`Retries = 0` 免得多等几个超时。

> 局限也写在注释里：个别网关对任何从站号都响应，探测结果只能作参考，最终以现场点表为准。

### 8. 采集循环：异常分层是重点

[PlcAcquisitionService.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/PlcAcquisitionService.cs)

| 设计 | 为什么 |
|---|---|
| [Start / StopAsync](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/PlcAcquisitionService.cs#L49-L82) | `Task.Run` 把循环扔后台；停止时 `cts.Cancel()` 再 `await` 等它真结束，不乱杀线程 |
| [ReadAll](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/PlcAcquisitionService.cs#L129-L148) 逐点读、**只捕获 `SlaveException`** | 地址类错误（异常码 02）只记为该点位失败，继续读下一个；点位表与现场不匹配时，坏点位标灰自己，不拖垮整轮 |
| 传输层异常交给外层 | 断网/连接断开这种重试也没意义的，整轮作废并上报 |
| [PersistAsync](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/PlcAcquisitionService.cs#L32-L36) 由外部注入 | 采集服务不认识数据库，只拿到读成功的样本；写哪张表、长表还是宽表由界面决定 |
| 先刷界面、后落库 | 数据库出问题也不能影响实时监控（[L84-L113](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/PlcAcquisitionService.cs#L84-L113)） |
| 事件 `BatchCompleted / Failed / PersistFailed` | 发布-订阅解耦：服务只管发，界面只管收 |

### 9. MainViewModel：状态、命令与线程

[MainViewModel.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/MainViewModel.cs) 是全项目最大的文件，但套路固定：

- **`[ObservableProperty]`**：写 `private string _statusText = "未连接";`，编译时自动生成 `StatusText` 属性，值一变界面自动刷新（`CommunityToolkit.Mvvm` 源生成器，省掉手写 `INotifyPropertyChanged`）。
- **`[RelayCommand]`**：`[RelayCommand(CanExecute = nameof(CanConnect))]` 自动生成 `ConnectCommand`，XAML 里 `Command="{Binding ConnectCommand}"`；`CanExecute` 控制按钮能否点，所以 `IsConnected`/`IsAcquiring`/`IsScanning` 一变，按钮可用状态跟着联动（[L67-L76](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/MainViewModel.cs#L67-L76)）。
- **后台线程不能碰界面**：采集回调统一用 [`_dispatcher.BeginInvoke`](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/MainViewModel.cs#L371-L395) 切回 UI 线程。忘记这一步，程序会随机崩溃。
- **`partial void OnXxxChanged`**：源生成器提供的钩子。改采集周期立即作用到运行中的循环（[L424-L431](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/MainViewModel.cs#L424-L431)）；改从站号则**重连**再采集，因为从站号是 `ModbusService` 构造时固定的（[L300-L309](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/MainViewModel.cs#L300-L309)）。
- **草稿值 vs 生效值**：界面上的表名/模式只是草稿，点【建表 / 校验结构】或【保存配置】才提交成生效值并落盘（[PersistBatchAsync](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/MainViewModel.cs#L441-L456) 用的是 `_config.Storage`）。免得边打字边把数据写到还不存在的表上。
- **改点位就重载**：[ApplyTagChanges](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/MainViewModel.cs#L581-L591) 重建卡片 → 存盘 → 采集中则重启采集让新点位生效。
- 两个小细节值得学：地址输入支持 `40001` 这种人读地址自动转协议地址（[TryParseAddress](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/MainViewModel.cs#L659-L680)）；从站号输入框绑的是**字符串**而不是 `byte`，否则打字中间态会弹红色校验错误（[L59-L63](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/MainViewModel.cs#L59-L63)）。

### 10. 配置与图形

- [PlcTag.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Models/PlcTag.cs) 是普通 `class`（不再是 `record`），因为它在表格里可编辑、要序列化到 json。`OnNameChanged` 里做了「改名称时列名跟着变，但列名被单独改过就不动」的联动（[L31-L37](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Models/PlcTag.cs#L31-L37)）。
- [ConfigStore.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/ConfigStore.cs)：json 读写 + `UnsafeRelaxedJsonEscaping`（中文直接写出来，配置文件要给人看）+ 配置损坏时退回默认点位保证界面能起；[ResolveRootDirectory](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/ConfigStore.cs#L53-L69) 从运行目录向上找 `.env`/`.git` 来定位仓库根。
- [TagViewModel.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/TagViewModel.cs)：滑动窗口曲线（[MaxPoints = 60](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/TagViewModel.cs#L16-L19)），每次 `Apply` 加点、删最旧；`MarkFailed` 只标灰自己并显示失败原因。

---

## 六、新手知识点速查

| 语法 / 概念 | 出现在哪 | 一句话记住 |
|---|---|---|
| `namespace` 文件作用域 | 所有 .cs 顶部 | 一个文件一个命名空间，少一层缩进 |
| `?` 可空引用 + 判空 | ModbusService | 编译期提醒你别忘判空 |
| `IDisposable` / `using var` / `await using` | ModbusService、AppDbContext | 网络、数据库资源必须释放 |
| `async` / `await` / `Task` | 采集与落库 | 等 IO 时不阻塞线程 |
| `CancellationToken` | 采集循环、扫描、仿真器 | .NET 标准的「停止」信号 |
| `event Action<T>` | PlcAcquisitionService | 发布-订阅，让服务不认识界面 |
| `Func<T, Task>` 委托 / `Action<T>` | PersistAsync、DeviceProfile.Fill | 把「动作」当参数传 |
| `required` + `init` | DeviceProfile | 创建时必须赋值、赋值后不可改 |
| `record` | Sample、AcquisitionBatch、TagFailure、SlaveProbeResult | 写不可变数据类最省事 |
| `[ObservableProperty]` / `[RelayCommand]` | ViewModels | 源生成器自动生成属性通知与命令 |
| `partial void OnXxxChanged` | MainViewModel、PlcTag | 属性变化钩子，做联动 |
| 依赖属性 `DependencyProperty` | MainWindow.CardColumns | WPF 里能参与绑定的自定义属性 |
| 数据绑定 `{Binding X}` / `MultiDataTrigger` | MainWindow.xaml | 界面跟着数据走；多条件决定样式 |
| `Dispatcher.BeginInvoke` | MainViewModel | 后台线程更新界面必须切回 UI 线程 |
| LINQ `Where / Select / Take / OrderByDescending` | 各处 | 对集合做筛选、投影、排序、取前 N |
| `$"..."` + `:F2` / `:D4` / `:HH:mm:ss` | 各处 | 字符串插值与格式化 |
| 构造函数注入 + `readonly` | 所有 Service | 依赖从外面给，不在里面 `new` |
| 参数化 SQL + 标识符白名单 | DynamicTableStore | 拼 SQL 时：值用参数，名字过白名单 |
| 正则 `Regex` | DynamicTableStore | 校验标识符合法性 |
| EF Core Code First | Data/、Migrations/ | 先写实体，再生成表（固定表用它，动态表用 ADO.NET） |

---

## 七、常见问题排查

| 现象 | 原因与处理 |
|---|---|
| 启动报「缺少环境变量 DB_SERVER」 | 根目录没有 `.env`，从 `.env.example` 复制并填值 |
| 「连接失败」 | 仿真 PLC 没启动，或 IP/端口填错（仿真用 `127.0.0.1:5020`） |
| 端口 502 监听失败 | 502 是常用端口，本机常被占用，仿真器已改用 5020 |
| 探测不到从站 | 仿真器要从站 1/2/3；真实网关可能不响应短超时，可把 `ProbeTimeoutMs` 调大 |
| 扫描结果大片「读异常：Modbus 异常码 2」 | 这些地址从站没开放（正是扫描要发现的事）；仿真器 1# 只开 0~19 |
| 卡片数字是 `--`、圆点灰 | 该点位读失败或未连接，卡片右下角会显示失败原因 |
| 落库失败「不能将值 NULL 插入列」 | 目标表里有非空列没被写入，按【建表/校验结构】的 ⚠ 提示换表名或把列改可空 |
| 表名/列名报「不合法」 | 只能中英文/数字/下划线，且不能数字开头 |
| `dotnet ef` 报找不到连接串 | 没在 `PlcDataCollector` 目录下执行，见 [AppDbContextFactory](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcDataCollector/Data/AppDbContextFactory.cs) |
| 界面改了列名但预览没变 | 正常情况下会实时联动；若没变，确认改的是点位表那一行（双击编辑） |

---

## 八、下一步可以练什么

按难度递增，每步都保持「可运行」：

1. 在「采集点位」页把某点位 Scale 从 1 改成 0.1，观察看板数值与落库值的变化；
2. 用【建表 / 校验结构】建一张宽表，对比它与 `DataRecords` 长表在查询上的差异；
3. 给采集加**自动重连**（当前传输层故障只上报，不重连）；
4. 落库改**攒批写入**（现在是每个周期一次插入）；
5. 加历史数据查询页与报警阈值（超限变红 / 触发记录）；
6. 引入日志库（Serilog / NLog）替代 `Console.WriteLine` 与状态栏提示；
7. 多设备并行采集：每台 PLC 一个采集任务，按「设备 + 从站号」分别落表。
