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
                                                                           ▼
                                                             ┌──────────────────────────┐
                                                             │ SQL Server               │
                                                             │ DataRecords / 自定义表   │
                                                             └──────────────────────────┘
```

一个类库装地基，两个可独立运行的程序，外加一个测试工程：

| 项目 | 类型 | 作用 |
|---|---|---|
| [HmiCore](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/HmiCore.csproj) | 类库 | 上位机地基：通信 / 采集 / 存储 / 配置四组服务 + 领域模型（[IModbusClient](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Comm/IModbusClient.cs)、[DynamicTableStore](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Storage/DynamicTableStore.cs)），不含任何 WPF 类型 |
| [PlcSimulator](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/Program.cs) | 控制台 | 假 PLC：一个端口挂 3 个从站，没真设备也能跑通全链路，还能验证扫描功能 |
| [PlcHmi](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Views/MainWindow.xaml) | WPF 桌面 | 完整上位机：连接 / 扫描点位 / 实时曲线 / 动态建表落库 |
| [PlcTests](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcTests/PlcTests.csproj) | 测试 | xunit：动态表拼 SQL 的白名单校验、机型名校验、点位模型联动、换算公式 |

`PlcHmi` 通过 `ProjectReference` 引用 `HmiCore`，通信与数据库代码只有一份（见 [PlcHmi.csproj](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/PlcHmi.csproj#L21-L24)）。根目录的 [c_Test.sln](file:///d:/taoxi_download_for_use/no_use_do/c_Test/c_Test.sln) 把四个工程串在一起，`dotnet build` 一次编译全部。

配置与运行产物的位置是一套固定约定，按用途分目录（见 [AppPaths.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Config/AppPaths.cs)）：

| 路径 | 内容 | 是否入库 |
|---|---|---|
| `.env`（模板 [.env.example](file:///d:/taoxi_download_for_use/no_use_do/c_Test/.env.example)） | 数据库地址、账号、密码 | ❌ 已在 [.gitignore](file:///d:/taoxi_download_for_use/no_use_do/c_Test/.gitignore) 里排除 |
| `data/` | **唯一的运行根目录**：配置 / 日志 / 导出 / 更新包全在它里面，按用途分子目录 | 全部忽略（各机器自己生成） |
| `data/configs/`\[机型\].json | **现场配置副本**：点位表 + 存储组 + 触发规则（工艺内容），首次运行从随包的机型模板复制 | ❌ |
| `data/machine.json` | **本机档案**：机台号 + 连接参数（IP/端口/从站号/采集周期）+ 当前机型 | ❌ |
| `PlcHmi/Templates/`\[机型\].json | **机型模板**：一个机型一份，随程序发布，是"这个型号该怎么采"的标准答案 | ✅ |
| `data/logs/` | 运行日志：按天滚动，只留 30 天 | ❌ |
| `data/exports/` | 历史数据导出 | ❌ |
| `data/updates/` | 远程更新包：应用成功后只留 2 份以便回滚 | ❌ |

根目录怎么定：向上找 `.env` / `.git` 的那一层（开发时就是仓库根），找不到就用 exe 目录（现场部署）；目录不可写时（例如装在 Program Files）整体回退到 `%ProgramData%\PlcHmi`。

**机型由机台号决定**：机台号是「4 个字母 + 3 位数字」（如 `AIXP999`），前 4 位就是机型代号 —— `AIXP` 分离封装机、`AITN` 辊压机、`AIBM` 成型机、`AIIR` 提纯机、`AIPC` 大片机（表在 [MachineModel.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Models/MachineModel.cs) 里，是唯一判定依据）。机台号以**左上角大字**展示，下面一行小字写出它对应的机型；小写会自动补成大写（`aixp001` → `AIXP001`）。

**机型不给手工选**：它只能由机台号推出来，避免出现"机台号写着 AIXP、实际却按 AIIR 采"这种事。所以**改机台号要输密码**（[MachineNoWindow](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Views/Dialogs/MachineNoWindow.xaml.cs)，防误改的软门槛，不是安全机制）。格式不对或代号不在表里时**不切机型**，保留当前机型继续跑，并在机台号下面标黄提示 —— 不会因为一个笔误就去动配置。

**三份文件各管一段**，这样同型号的配置才能完全一致、可以整份复制给另一台机器：
- `PlcHmi/Templates/<代号>.json`：机型的**标准工艺**，随代码走、随包发布（`dotnet publish` 会带上），现场只读。
- `data/configs/<代号>.json`：**现场副本**，首次运行从模板复制，之后现场调的就是这一份（同型号之间可以直接对拷）。
- `data/machine.json`：**这台设备**的机台号与连接参数 —— 每台都不一样，所以不放在机型配置里。

启动时选机型的顺序（见 [ProfileResolver.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Config/ProfileResolver.cs)）：命令行 `--config <代号>` > 命令行 `--machine <机台号>` > 本机档案里记的已知机型 > 机台号推出来的机型 > 已有的自定义档案 > 已知机型第一个。例如 `dotnet run --project PlcHmi\PlcHmi.csproj -- --machine AIXP999`。

老配置（机台号与连接参数还写在机型配置里的那份）启动时会自动迁移：机台号与连接参数搬到 `data/machine.json`，若机台号能认出机型且该机型的档案还不存在，就把老档案整份复制成 `<代号>.json` 当初始内容（老档案本身保留，下拉里仍可选回去）。

---

## 三、目录结构

```
c_Test/
├── data/                             # 唯一的运行根目录：只放行 configs，其余整个目录不入库
│   ├── configs/                      # 机型配置：一台机器一份（README 见「二、数据是怎么流动的」）
│   │   └── 默认.json                 # 连接参数 + 点位表 + 存储组 + 触发规则
│   ├── logs/                         # 运行日志（保留 30 天）
│   ├── exports/                      # 历史数据导出
│   ├── updates/                      # 远程更新包（保留 2 份）
│   └── last-profile.txt              # 上次用的机型
├── HmiCore/                          # 上位机地基类库（被 PlcHmi 引用，里面不允许出现 WPF 类型）
│   ├── AppServices.cs                # 组合根：需要 new 具体实现的地方只有这里
│   ├── Comm/                         # Modbus 通信与设备发现
│   │   ├── IModbusClient.cs          # Modbus 客户端能力（抽取出来为了可测 + 可换库）
│   │   ├── ModbusService.cs          # Modbus TCP 通信封装（单点读 + 批量读）
│   │   ├── ModbusScanService.cs      # 寄存器扫描（分段批读 + 失败段逐点定位）
│   │   ├── SlaveProbeService.cs      # 从站号探测（短超时 + 分批并行）
│   │   └── ConnectionController.cs   # 链路生命周期：连接 / 断开 / 换从站重连
│   ├── Acquisition/                  # 采集与触发的运行循环
│   │   ├── PlcAcquisitionService.cs  # 后台采集循环，异常分层
│   │   ├── AcquisitionController.cs  # 采集循环的生命周期、运行期调参与落库
│   │   ├── TriggerWatchService.cs    # 触发监听（高频轮询信号位 + 握手应答）
│   │   └── ShellStatusController.cs  # 会话状态与"哪些开关能点"的唯一来源
│   ├── Storage/                      # 数据库与动态表
│   │   ├── SqlConnectionFactory.cs   # 数据库连接工厂（连接串来自 .env）
│   │   ├── DynamicTableStore.cs      # 动态建表/写入/查询（表名列名白名单防注入）
│   │   ├── TriggerLogStore.cs        # 触发记录表读写
│   │   └── StorageGroupValidator.cs  # 存储组校验（组名/表名/区段/列数上限）
│   ├── Config/                       # 配置与运行目录
│   │   ├── AppPaths.cs               # 目录约定：data/ 下的 configs|logs|exports|updates + 机型模板目录
│   │   ├── ConfigStore.cs            # 机型配置读写（一个实例 = 一个机型；缺就抄模板）
│   │   ├── MachineStore.cs           # 本机档案读写（data/machine.json）
│   │   ├── ProfileResolver.cs        # 启动时用哪个机型：命令行 > 档案 > 机台号 > 兜底
│   │   └── MachineProfileController.cs # 机型装载/切换 + 本机档案读写
│   └── Models/                       # 领域模型
│       ├── PlcTag.cs                 # 点位：地址+名称+单位+Scale+Offset+宽表列名（可编辑）
│       ├── PlcConfig.cs              # 机型配置对象：点位表 + 存储组 + 触发规则（工艺内容）
│       ├── MachineModel.cs           # 机台号 → 机型：4 字母代号 + 中文型号名
│       ├── MachineProfile.cs         # 本机档案：机台号 + 连接参数 + 当前机型
│       ├── Sample.cs / AcquisitionBatch.cs  # 一轮采集结果（成功样本 + 失败点位）
│       ├── TriggerRule.cs            # 触发规则：信号/应答地址 + 超时
│       └── ScanResult.cs             # 扫描结果一行
├── PlcSimulator/                     # 仿真 PLC（从站）
│   ├── Program.cs                    # 起监听、挂多个从站、每秒刷新数据
│   ├── DeviceProfile.cs              # 设备档案：从站号/名称/开放多少寄存器/刷新规则
│   ├── HandshakeDevice.cs            # 模拟现场握手设备（信号位 → 应答位时序）
│   └── LimitedSlaveDataStore.cs      # 只开放指定地址，越界回异常码 02
├── PlcHmi/                           # WPF 上位机（MVVM）：一层外壳 + 按功能分模块
│   ├── App.xaml(.cs)                 # 启动 + 加载 .env + 全应用共用样式
│   ├── Views/                        # 外壳窗口与弹窗
│   │   ├── MainWindow.xaml(.cs)      # 顶栏（机台号 / 连接 / 采集 / 状态栏）+ 页签容器
│   │   └── Dialogs/                  # 弹窗：机台号 / 密码 / PLC 配置 / 表记录历史
│   ├── ViewModels/
│   │   └── MainViewModel.cs          # 外壳：机型 / 连接 / 采集生命周期 / 状态栏
│   ├── Features/                     # 一个功能一个目录，自成一块
│   │   ├── Plc/                      # PLC 采集这一块（四个页签）
│   │   │   ├── Views/                # 页签内容各是一个 UserControl
│   │   │   │   ├── DashboardView.xaml(.cs)  # 页签①：实时卡片与曲线，按存储组分段（含自适应列数）
│   │   │   │   ├── ScanView.xaml(.cs)       # 页签②：扫描寄存器、点位表编辑
│   │   │   │   ├── StorageView.xaml(.cs)    # 页签③：存储组草稿、建表与校验
│   │   │   │   └── TriggerView.xaml(.cs)    # 页签④：触发规则、报警、触发记录
│   │   │   └── ViewModels/
│   │   │       ├── DashboardViewModel.cs      # 页签①：点位卡片与曲线、最近记录
│   │   │       ├── DashboardGroupViewModel.cs # 页签①：一个存储组的一段卡片（可折叠）
│   │   │       ├── ScanViewModel.cs           # 页签②：寄存器扫描、点位表增删改
│   │   │       ├── StorageViewModel.cs        # 页签③：存储组草稿、建表 / 校验结构
│   │   │       ├── TriggerViewModel.cs        # 页签④：触发规则、报警条、触发记录
│   │   │       ├── TableHistoryViewModel.cs   # 「表记录历史」弹窗：目标表最近记录
│   │   │       └── TagViewModel.cs            # 单张点位卡片 + 滚动曲线
│   │   └── Aoi/                      # AOI 拍照存图的落点（先占位，加第一个文件后可删 .gitkeep）
│   ├── Controls/                     # 通用控件（SectionHeader 等）
│   ├── Services/                     # WPF 侧基础设施：主题切换、绑定错误写进日志
│   ├── Themes/                       # 亮 / 暗两套调色板
│   └── Templates/                    # 机型模板：AIXP|AITN|AIBM|AIIR|AIPC.json，随包发布
├── PlcTests/                         # xunit 测试工程（只测纯逻辑，不连 PLC、不连库）
│   ├── DynamicTableStoreTests.cs     # 标识符白名单 / 列名校验 / 建表语句
│   ├── StorageGroupValidatorTests.cs # 组名校验 / 区段重叠 / 宽表列数上限
│   ├── ConfigStoreTests.cs           # 机型名白名单（含路径穿越）
│   ├── PlcTagTests.cs                # 名称↔列名联动 / 人读地址
│   ├── AcquisitionServiceTests.cs    # 换算公式
│   └── TriggerWatchServiceTests.cs   # 握手时序（用假客户端驱动）
├── c_Test.sln                        # 四个工程的解决方案（dotnet build / dotnet test 用）
└── start.md                          # 运行与测试命令
```

**分层约定**（加新功能时照这个放，就不会往外壳里堆）：

- `HmiCore`：**纯逻辑**，四组服务各归各的目录（通信进 `Comm/`、落库进 `Storage/`、配置与目录进 `Config/`、运行循环进 `Acquisition/`），里面不出现任何 WPF 类型；新功能的相机通信、采集循环、图片落盘都进这里（建议新开 `HmiCore/Imaging/`）。
- `PlcHmi/Views` + `ViewModels/MainViewModel.cs`：**外壳**，只管跨功能的东西（顶栏、状态栏、页签容器、生命周期）。
- `PlcHmi/Features/<功能>`：**一个功能一块**，自带 `Views/` 与 `ViewModels/`。新功能的页签内容做成一个 UserControl 挂进 `MainWindow.xaml` 的 `TabItem`，视图模型挂在 `MainWindow` 的视图模型上，**不要往 `MainViewModel` 里继续加**功能自己的字段。
- 运行期新产物（比如 AOI 图片）加到 `AppPaths` 的 `data/` 下（如 `data/images/<机台号>/<日期>/`），并像 `logs/` 一样定好保留策略 —— 图片是唯一会真把磁盘撑满的东西。

---

## 四、跑起来（两个终端）

先把 `.env.example` 复制成 `.env` 并填好数据库连接，再按 [start.md](file:///d:/taoxi_download_for_use/no_use_do/c_Test/start.md) 分别启动：

```powershell
# 终端 1：仿真 PLC（127.0.0.1:5020，从站 1/2/3）
dotnet run --project PlcSimulator\PlcSimulator.csproj

# 终端 2：上位机界面（默认已填 127.0.0.1:5020，点【连接】→【开始采集】）
dotnet run --project PlcHmi\PlcHmi.csproj
```

不连 PLC 也能验证逻辑（动态表拼 SQL、机型名校验、换算公式这些纯逻辑）：

```powershell
dotnet test          # 根目录执行，走 c_Test.sln，跑 PlcTests
```

**界面推荐操作顺序**：连接 → 「采集点位」页扫描 40001~40040 → 勾选可读地址加入点位表、改名称与 Scale → 「存储映射」页选目标表与长/宽表 → 点【建表 / 校验结构】→ 回看板点【开始采集】看曲线。

---

## 五、核心代码逐个讲

### 1. 通信层：ModbusService

[ModbusService.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Comm/ModbusService.cs)

| 知识点 | 说明 |
|---|---|
| `IDisposable` + `using var` | `TcpClient` 是网络资源，用完必须释放。调用方写 `using var modbus = new ModbusService(1)`，离开作用域自动关连接 |
| `?` 可空引用 | 项目开了 `<Nullable>enable</Nullable>`，`IModbusMaster? _master` 表示「可能还没连」。读之前先判断，把空引用异常换成能看懂的提示（[L34-L37](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Comm/ModbusService.cs#L34-L37)） |
| 类型转换 | 寄存器读回来是 `ushort[]`，业务要按有符号看，所以 `(short)result[0]` |
| 两个读方法 | [ReadRegister](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Comm/ModbusService.cs#L32-L41) 读单点（采集用）；[ReadRegisters](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Comm/ModbusService.cs#L47-L55) 批量读（扫描用，一次最多 125 个） |
| 从站号 | 构造函数固定 `_slaveId`，所以**切换从站要重连**（见下文 MainViewModel） |

单独包一层的好处：以后换 HslCommunication 或别的库，只改这一个文件。

### 2. 数据库连接与表结构

- 连接工厂 [SqlConnectionFactory.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Storage/SqlConnectionFactory.cs)：只做两件事——按 `.env` 的 `DB_SERVER/DB_NAME/DB_USER/DB_PASSWORD` 拼连接串、[OpenAsync](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Storage/SqlConnectionFactory.cs#L30-L43) 开一个连接（开失败会把半开连接释放掉）；缺配置时给一句清晰报错。
- `.env` 由 `DotNetEnv` 加载，[Env.TraversePath().Load()](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/App.xaml.cs#L9-L22) 会从当前目录向上找（没有 `.env` 也允许启动，只在读写库时报错）。
- **表结构不由代码里的实体决定**：目标表名、列名是运行时才定的（长表固定列 / 宽表一个点位一列），所以建表语句由 [DynamicTableStore.BuildCreateScript](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Storage/DynamicTableStore.cs#L54-L79) 现场拼，界面点【建表 / 校验结构】时执行。

> 为什么不用 ORM：EF 的实体是编译期固定的，而这里"一张表长什么样"要等用户在界面上选完点位才知道 —— 硬套 ORM 只能得到一层用不上的抽象。所以全项目统一走 ADO.NET：**值一律参数化，标识符一律过白名单**（见第 5 节）。

### 3. 仿真 PLC：怎么模拟「一个网关挂多台设备」

- [DeviceProfile.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/DeviceProfile.cs)：一台设备的档案。`required` 属性表示「创建时必须赋值」；[Fill](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/DeviceProfile.cs#L20-L21) 是一个 `Action<double, ushort[]>` 委托，把「按秒数刷新寄存器」这条规则当成数据传给框架 —— 这是**把函数当参数**的典型用法。
- [Program.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/Program.cs)：同一端口上 `AddSlave` 多个从站，各自数据互不相干；每秒刷新并打印。
- [LimitedSlaveDataStore.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/LimitedSlaveDataStore.cs#L40-L46)：越界读写抛 `InvalidModbusRequestException(IllegalDataAddress)`，即回异常码 02。**这样仿真器和真实 PLC 行为一致**——不是所有地址都存在，扫描功能才有得验证。
- [Ctrl+C 处理](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcSimulator/Program.cs#L55-L60)：不直接杀进程，而是 `cts.Cancel()` 让循环正常收尾，这是 .NET 推荐的任务取消方式。

### 4. 界面结构：一层外壳 + 四个页签

[MainWindow.xaml](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Views/MainWindow.xaml) 只负责外壳：顶部是共用工具条（左侧机台号大字，右侧上行连接参数、下行开关与状态），底部是状态栏，中间是 `TabControl`。每个 `TabItem` 挂一个 UserControl（内容在 `Features/Plc/Views/`），并把 `DataContext` 指向该页签自己的视图模型：

| 页签 | 内容控件 | 干什么 |
|---|---|---|
| 实时看板 | [DashboardView](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/Views/DashboardView.xaml) | 每点位一张卡片（大数字 + 原始值 + 60 点滚动曲线），按存储组分段、每段可折叠；点卡片里的曲线弹出「表记录历史」窗 |
| 采集点位 | [ScanView](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/Views/ScanView.xaml) | 扫描寄存器 → 勾选加入点位表 → 编辑名称/单位/Scale/Offset/宽表列名 |
| 存储映射 | [StorageView](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/Views/StorageView.xaml) | 选目标表与长表/宽表 → 预览建表语句 → 建表/校验结构；组表与建表预览之间的分隔条可上下拖动调高 |
| 触发记录 | [TriggerView](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/Views/TriggerView.xaml) | 触发规则（信号/应答地址、超时）→ 监听状态与报警条 → 最近的触发记录 |

页签抽成 UserControl 的好处：改一个页签只碰一个文件，`MainWindow.xaml` 不会跟着长；新增功能就是再加一个 `Features/<功能>/Views/xxxView.xaml` 和一个 `TabItem`。

几个 WPF 技巧：

- **卡片列数自适应**：[DashboardView.xaml.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/Views/DashboardView.xaml.cs#L15-L21) 定义了一个依赖属性 `CardColumns`，卡片区尺寸变化时按宽度重算一行放几张，绑给 `UniformGrid.Columns`。用 `UniformGrid` 而不是 `WrapPanel`，是因为前者会均分宽度、把整行排满。
- **状态灯三态**：连上且无错→绿、连上有错→红、未连接→灰，靠 `MultiDataTrigger` 实现（[MainWindow.xaml](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Views/MainWindow.xaml#L205-L225)）。
- **最近记录表列不固定**：长表/宽表列不同，所以「表记录历史」窗里 `AutoGenerateColumns="True"` 直接绑一个 `DataView`（[RecordHistoryWindow.xaml](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Views/Dialogs/RecordHistoryWindow.xaml#L27-L29)）。
- **看板按存储组分段**：`DashboardViewModel.RefreshGrouping` 把卡片按 `PlcTag.Group` 归到 `DashboardGroupViewModel` 里，界面一个 `Expander` 一段；只重排卡片不重建，所以改地址、调区段不会清空曲线。
- **点曲线看记录历史**：卡片曲线挂的是 [PreviewMouseLeftButtonUp](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/Views/DashboardView.xaml.cs#L42-L66)（Preview 不会被图表自己的鼠标处理吃掉），按地址反查覆盖它的存储组 → 目标表，再用非模态小窗展示这张表最近的记录。

### 5. 动态表：DynamicTableStore（本项目最值得看的一段）

[DynamicTableStore.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Storage/DynamicTableStore.cs)

需求是「表名、列名由用户在界面上填」，EF 实体做不到，只能用 ADO.NET 拼 SQL。**拼 SQL 就有注入风险**，所以这里的处理方式是：

1. 表名列名**不能参数化**，一律先过白名单正则 [`^[\u4e00-\u9fa5A-Za-z_][\u4e00-\u9fa5A-Za-z0-9_]{0,63}$`](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Storage/DynamicTableStore.cs#L18-L32)（中英文/数字/下划线，不能数字开头）；
2. 再用 `[ ]` 包裹并转义 `]`（[Quote](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Storage/DynamicTableStore.cs#L296)）；
3. **数据值一律走参数**（时间、原始值、工程值…都是 `@参数`）。

两种存储模式：

| 模式 | 结构 | 特点 |
|---|---|---|
| 长表 Long | 一行一个点位：时间/地址/原始值/工程值 | 点位增减不用改表结构，最通用（`DataRecords` 就是长表） |
| 宽表 Wide | 一行一个时刻，每个点位一列 | 查起来直观、适合做报表；点位增减要同步列 |

宽表有个容易踩的坑，代码专门处理了（[EnsureWideColumnsAsync](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Storage/DynamicTableStore.cs#L136-L196)）：

- 缺的点位列 → `ALTER TABLE ... ADD` 补上；
- 点位列是 `NOT NULL` 的 → 放宽成 `NULL`（某个点位本次读失败时该列写 NULL，不该让整行插入失败）；
- 表里存在**既非空、又不属于当前点位表**的列（比如长表改建出来的 `RawValue`）→ 提前点名为「⚠ 阻断列」，因为插入语句不会写它，SQL Server 会直接报错（[L184-L193](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Storage/DynamicTableStore.cs#L184-L193)）。

`await using var connection = await SqlConnectionFactory.OpenAsync()` + 用完即释放：SQL 连接是稀缺资源，**一次操作一个连接**最稳妥（动态表那几处都是这个写法）。

### 6. 点位扫描与从站探测

[ModbusScanService.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Comm/ModbusScanService.cs)：Modbus 没有「列出所有点位」的指令，只能试读。策略是**分段批量读**，整段成功就整段标记可读；某段失败再**逐点重读**，定位到底是哪几个地址不可读（[L37-L68](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Comm/ModbusScanService.cs#L37-L68)）。每段/每点之间 Sleep 一下，避免把真实 PLC 打满。取消靠 `CancellationToken`，`token.ThrowIfCancellationRequested()` 在每个循环开头检查。

[SlaveProbeService.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Comm/SlaveProbeService.cs)：一个网关后面可能挂多台 PLC。做法是**短超时 + 分批并行**（每批 8 个 [L19](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Comm/SlaveProbeService.cs#L19)）：

- 读到数据、或回了 Modbus 异常码 → 判定「从站存在」（回了异常码说明它活着，只是地址没定义）；
- 连不上/超时 → 判定不存在；
- `Task.WhenAll` 等一批全部结束，`Retries = 0` 免得多等几个超时。

> 局限也写在注释里：个别网关对任何从站号都响应，探测结果只能作参考，最终以现场点表为准。

### 7. 采集循环：异常分层是重点

[PlcAcquisitionService.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Acquisition/PlcAcquisitionService.cs)

| 设计 | 为什么 |
|---|---|
| [Start / StopAsync](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Acquisition/PlcAcquisitionService.cs#L49-L82) | `Task.Run` 把循环扔后台；停止时 `cts.Cancel()` 再 `await` 等它真结束，不乱杀线程 |
| [ReadAll](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Acquisition/PlcAcquisitionService.cs#L129-L148) 逐点读、**只捕获 `SlaveException`** | 地址类错误（异常码 02）只记为该点位失败，继续读下一个；点位表与现场不匹配时，坏点位标灰自己，不拖垮整轮 |
| 传输层异常交给外层 | 断网/连接断开这种重试也没意义的，整轮作废并上报 |
| [PersistAsync](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Acquisition/PlcAcquisitionService.cs#L32-L36) 由外部注入 | 采集服务不认识数据库，只拿到读成功的样本；写哪张表、长表还是宽表由界面决定 |
| 先刷界面、后落库 | 数据库出问题也不能影响实时监控（[L84-L113](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Acquisition/PlcAcquisitionService.cs#L84-L113)） |
| 事件 `BatchCompleted / Failed / PersistFailed` | 发布-订阅解耦：服务只管发，界面只管收 |

### 8. 视图模型：一个外壳 + 四个页签

界面按页签拆成五个视图模型：外壳那只留在 `PlcHmi/ViewModels/`，四个页签的挪到 `PlcHmi/Features/Plc/ViewModels/`。**每个 `TabItem` 的 `DataContext` 指向自己的那个**（见 [MainWindow.xaml](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Views/MainWindow.xaml#L249-L263)），页签内部的绑定因此不用改一层前缀：

| 类 | 管什么 |
|---|---|
| [MainViewModel](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/MainViewModel.cs)（外壳） | 机型配置、连接、采集生命周期、状态栏；把 `Config` / `Modbus` 借给页签 |
| [DashboardViewModel](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/ViewModels/DashboardViewModel.cs) | 点位卡片与曲线、最近记录 |
| [ScanViewModel](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/ViewModels/ScanViewModel.cs) | 寄存器扫描、点位表增删改 |
| [StorageViewModel](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/ViewModels/StorageViewModel.cs) | 存储组草稿、建表 / 校验结构、归组展示 |
| [TriggerViewModel](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/ViewModels/TriggerViewModel.cs) | 触发规则、报警条、触发记录 |

共享状态只有一个来源：**配置对象**（点位表 / 存储组 / 触发规则都在 `Config` 里），页签不互相持有对方。需要跨页签的动作由外壳出面：点位变了调 `OnTagsChanged()`（重建卡片 → 存盘 → 采集中则重启采集），规则变了调 `RestartTriggerWatchAsync()`。

几个共同套路：

- **`[ObservableProperty]`**：写 `private string _statusText = "未连接";`，编译时自动生成 `StatusText` 属性，值一变界面自动刷新（`CommunityToolkit.Mvvm` 源生成器，省掉手写 `INotifyPropertyChanged`）。
- **`[RelayCommand]`**：`[RelayCommand(CanExecute = nameof(CanProbeSlaves))]` 自动生成 `ProbeSlavesCommand`，XAML 里 `Command="{Binding ProbeSlavesCommand}"`；`CanExecute` 控制按钮能否点，所以 `IsProbing` 一变，按钮可用状态跟着联动。
- **开关代替按钮对**：工具条上「连接/断开」与「开始/停止采集」各是一个 `hc:ToggleBlock`，`IsChecked` 双向绑到 `ConnectionSwitch`/`AcquisitionSwitch` 上，由这两个属性的变更处理器执行动作；状态回写（连接失败、切换机型、切从站）经 `_syncingSwitches` 只刷新显示，避免"改状态 → 又触发一遍动作"的循环（[MainViewModel](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/ViewModels/MainViewModel.cs#L289-L356)）。
- **后台线程不能碰界面**：采集回调统一用 `Dispatcher.BeginInvoke` 切回 UI 线程。忘记这一步，程序会随机崩溃。
- **`partial void OnXxxChanged`**：源生成器提供的钩子。改采集周期立即作用到运行中的循环；改从站号则**重连**，因为从站号是 `ModbusService` 构造时固定的（原来在采就接着采）。
- **草稿值 vs 生效值**：存储组在界面上只是草稿，点【建表 / 校验结构】或【保存配置】才提交成生效值并落盘（[CommitMapping](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/ViewModels/StorageViewModel.cs)）。免得边打字边把数据写到还不存在的表上。
- 两个小细节值得学：地址输入支持 `40001` 这种人读地址自动转协议地址（[TryParseAddress](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/ViewModels/ScanViewModel.cs)）；从站号输入框绑的是**字符串**而不是 `byte`，否则打字中间态会弹红色校验错误。

### 9. 排查用得到的两个约定

- **日志**：`data/logs/plc-<日期>.log`，按天滚动、只留 `AppPaths.LogRetentionDays`（30）天，配置在 [App.xaml.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/App.xaml.cs#L45-L55)。状态栏是给操作工看的"现象"，日志是给事后排查看的"时间线"，两者分工。
- **WPF 绑定错误进日志**：绑错属性名时界面不报错、也不崩，只是那一格"没有数据"，肉眼很难发现。[BindingErrorTraceListener](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Services/BindingErrorTraceListener.cs) 把 WPF 的绑定告警接到日志里（这次拆视图模型就是靠它抓到 6 条 `Cannot find source for binding`）。

### 10. 配置与图形

- [PlcTag.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Models/PlcTag.cs) 是普通 `class`（不再是 `record`），因为它在表格里可编辑、要序列化到 json。`OnNameChanged` 里做了「改名称时列名跟着变，但列名被单独改过就不动」的联动（[L31-L37](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Models/PlcTag.cs#L31-L37)）。
- [ConfigStore.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Config/ConfigStore.cs)：机型配置读写，一个实例对一个 `data/configs/<机型>.json`。json 读写 + `UnsafeRelaxedJsonEscaping`（中文直接写出来，配置文件要给人看）+ 配置损坏时退回默认点位保证界面能起；机型名过白名单正则（它要当文件名，顺带挡路径穿越）；配置里带 `ConfigVersion`，格式版本高于程序时按默认点位启动并在状态栏说明。启动选哪个机型见 [ResolveStartupProfile](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Config/ConfigStore.cs#L69-L82)：`--config` > 上次用的 > 已有第一个 > 默认。整套目录布局（统一在 `data/` 下：configs / logs / exports / updates）在 [AppPaths.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/HmiCore/Config/AppPaths.cs)。
- [TagViewModel.cs](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/ViewModels/TagViewModel.cs)：滑动窗口曲线（[MaxPoints = 60](file:///d:/taoxi_download_for_use/no_use_do/c_Test/PlcHmi/Features/Plc/ViewModels/TagViewModel.cs#L16-L19)），每次 `Apply` 加点、删最旧；`MarkFailed` 只标灰自己并显示失败原因。

---

## 六、新手知识点速查

| 语法 / 概念 | 出现在哪 | 一句话记住 |
|---|---|---|
| `namespace` 文件作用域 | 所有 .cs 顶部 | 一个文件一个命名空间，少一层缩进 |
| `?` 可空引用 + 判空 | ModbusService | 编译期提醒你别忘判空 |
| `IDisposable` / `using var` / `await using` | ModbusService、SqlConnectionFactory | 网络、数据库资源必须释放 |
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
| 接口 `IModbusClient` | HmiCore/Services | 面向接口写，单测能塞假实现（握手时序就是这么测的） |
| `Serilog` + `WriteTo.File` | App.xaml.cs | 日志落文件、按天滚动、限制保留天数 |
| 手写组合根 `AppServices` | PlcHmi/Services | 需要 `new` 具体实现的地方收一处，视图模型只拿现成的 |
| 正则 `Regex` | DynamicTableStore | 校验标识符合法性 |
| 连接串从环境变量拼 | SqlConnectionFactory | 账号密码不进源码与 git 历史 |

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
| 界面改了列名但预览没变 | 正常情况下会实时联动；若没变，确认改的是点位表那一行（双击编辑） |
| 启动后点位表不是我预期那台机器 | 机型记的是上次用的那个；看状态栏左侧的「机型」提示，或启动时加 `--config <机型>` |
| 反复提示「连接参数保存失败」 | 程序目录不可写（常见于装在 Program Files），检查是否已回退到 `%ProgramData%\PlcHmi` 下读取配置 |
| 界面某一格一直空白/没数据 | 多半是绑定没找到属性（不报错也不崩）：看 `data/logs/plc-<日期>.log` 里的 `WPF 绑定：` 警告 |
| 现场出了问题但说不清现象 | 先看 `data/logs/plc-<日期>.log`：连接、采集启停、触发、报警、异常都在里面 |

---

## 八、下一步可以练什么

按难度递增，每步都保持「可运行」：

1. 在「采集点位」页把某点位 Scale 从 1 改成 0.1，观察看板数值与落库值的变化；
2. 用【建表 / 校验结构】建一张宽表，对比它与 `DataRecords` 长表在查询上的差异；
3. 给采集加**自动重连**（当前传输层故障只上报，不重连）；
4. 落库改**攒批写入**（现在是每个周期一次插入）；
5. 加历史数据查询页与报警阈值（超限变红 / 触发记录）；
6. 把日志接上**滚动归档**（按大小切分）或加一个「打开日志目录」按钮，现场让人一键取日志；
7. 多设备并行采集：每台 PLC 一个采集任务，按「设备 + 从站号」分别落表。
