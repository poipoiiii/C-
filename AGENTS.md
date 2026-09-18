# AGENTS.md · PLC 上位机（C#/.NET 8）项目记忆

> **动手前先读本文件（AGENTS.md）**，再按需查 `README.md`（详细导读）、`todo.md`（待办 + 现场约定）、`update.md`（变更记录，最新在最上）。

## 背景
Modbus TCP 读 PLC 保持寄存器 → `工程值 = 原始值 × Scale + Offset` → 动态建表写 SQL Server → WPF 实时展示。换算规则是唯一业务逻辑，其余是脚手架；代码注释详尽，改前先读 XML 注释。

## 工程与依赖
- `HmiCore`（net8.0 类库）：全部业务逻辑，**不得出现 WPF 类型**。`Comm/`通信、`Acquisition/`采集循环、`Storage/`落库、`Config/`配置与目录、`Models/`模型；`AppServices.cs` 是唯一组合根。
- `PlcHmi`（WPF，TFM 固定 `net8.0-windows10.0.19041.0`）→ 引用 HmiCore；HandyControl + LiveCharts2 + CommunityToolkit.Mvvm。
- `PlcSimulator`（假 PLC，127.0.0.1:5020）、`PlcTests`（net8.0 xunit，只引用 HmiCore，不连 PLC/库）。
- 通信 NModbus；存储 Microsoft.Data.SqlClient（ADO.NET 动态 SQL，**不用 EF**；C_study.md 的 EF 是历史稿）。

## 运行（dotnet 不在 PATH 时先 `$env:PATH += ';C:\Users\tao.xi\AppData\Local\Microsoft\dotnet'`）
- 仿真：`dotnet run --project PlcSimulator\PlcSimulator.csproj -- --slave 1`（1~5=五种机型、6=握手；`--slave 1,6` / `--verbose` / `--help`）
- 界面：`dotnet run --project PlcHmi\PlcHmi.csproj -- --config AIXP`（或 `--machine AIXP999`）
- 测试：`dotnet test`（不需 PLC/库）；打包：`publish.bat`（打包后 `Templates/` 必须与 exe 同层）

## 导航（改什么去哪）
| 改什么 | 去哪 |
|---|---|
| Modbus 收发 / 扫描 / 从站探测 / 连接与重连 | `HmiCore/Comm/`（ModbusService 内置读写互斥、ModbusScanService、SlaveProbeService、ConnectionController） |
| 采集循环、启停与落库编排、握手触发、按钮可用性唯一来源 | `HmiCore/Acquisition/`（PlcAcquisitionService、AcquisitionController、TriggerWatchService、ShellStatusController） |
| 动态建表 / 写入 / 查询 / 存储组校验 | `HmiCore/Storage/DynamicTableStore.cs`、`StorageGroupValidator.cs`（触发记录：TriggerLogStore） |
| 机型选择、本机档案、老配置迁移、data/ 目录布局 | `HmiCore/Config/`（ProfileResolver、MachineStore、MachineProfileController、AppPaths） |
| 机台号 → 机型的唯一依据 | `HmiCore/Models/MachineModel.cs` |
| 界面（外壳、四个页签、启动与日志） | `PlcHmi/ViewModels/MainViewModel.cs`、`Views/MainWindow.xaml`、`Features/Plc/{Views,ViewModels}/`、`App.xaml.cs` |

## 硬约束
1. HmiCore 不放 WPF 类型；后台线程回界面用 `Dispatcher.BeginInvoke`。
2. 动态表：表名/列名过白名单正则后 `[ ]` 包裹，**值一律参数化**。
3. 机型只由机台号前 4 位决定，不给手选；认不出机型不切配置只标黄；改机台号要密码（软门槛 `shjie12345`）。
4. 点位归组由地址落在哪个区段决定，`Group/GroupText` 仅展示、`[JsonIgnore]` 不写配置；存储组界面是草稿，点【建表/校验】才生效；切从站 = 重连（slaveId 构造时固定）。**点位可占多个寄存器**（32 位 2 个 / 64 位 4 个）：宽度看 `PlcTag.DataType`、字序看 `PlcTag.WordOrder`，解码只在 `Models/PlcValueCodec.cs`；判定归属/落库一律用整段（`StorageGroup.CoversRange(tag.Address, tag.EndAddress)`），多寄存器点位要整段在区段内。**同一个寄存器只算一个点位**（`Models/TagLayout.cs`：按起始地址靠前、占得更宽的那个算），被盖住的点位只在「占用」列标红 —— 它是那个大数的半截字节，采集、落库、建表都跳过它（表里一个数据一列）。
5. 配置三份：`PlcHmi/Templates/<代号>.json`（随包只读）→ `data/configs/<代号>.json`（现场副本）→ `data/machine.json`（机台号+连接参数+当前机型）；`data/*`、`publish/`、`.env` 不入库。
6. 新功能：逻辑进 HmiCore 新目录，界面进 `Features/<功能>/{Views,ViewModels}`，不要往 MainViewModel 堆字段。

## 坑与现状
- 宽表：缺点位列 → ALTER ADD，点位列 NOT NULL → 放宽 NULL；表内**非空且不属于当前点位的列 = 阻断列**，插入必失败。
- 长表插入按 200 行切段 + 单事务（绕 2100 参数上限）；采集只捕 `SlaveException`，传输层异常整轮作废、整段失败回退段内逐点读（多寄存器点位按整段重读）；采集拆 125 寄存器上限的小段时**不切开点位**；长表 `RawValue` 是 FLOAT（老表是 SMALLINT 的要 `ALTER COLUMN [RawValue] FLOAT NOT NULL`）；界面某格空白不报错 → 查 `data/logs/plc-<日期>.log` 的 `WPF 绑定：` 警告。
- 现状：P1 区段分组分表、P2 采集落库性能、触发记录、附加列 + 机台号 已完成；P3 写线圈部分完成（缺写点面板 / 白名单 / 审计）；P4 自动重连、P5 报警未做；`Features/Aoi/` 空占位。改动追加记录到 `update.md`。
