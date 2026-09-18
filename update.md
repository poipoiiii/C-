2026-09-18  新增 AGENTS.md：项目记忆（分层约定 / 导航表 / 硬约束 / 坑与现状）

【新增】
- AGENTS.md：动手前先读的项目记忆，内容包括
  - 背景与依赖：换算规则是唯一业务逻辑；HmiCore（net8.0 类库，不得出现 WPF 类型）/
    PlcHmi（TFM 固定 net8.0-windows10.0.19041.0）/ PlcSimulator / PlcTests 的职责与依赖方向
  - 运行命令：仿真 `--slave 1`、界面 `--config AIXP` / `--machine AIXP999`、测试、publish.bat
  - 「改什么去哪」导航表：Comm / Acquisition / Storage / Config / Models / 界面各自对应文件
  - 6 条硬约束：HmiCore 不放 WPF 类型、动态表名字过白名单且值参数化、机型只由机台号前 4 位决定、
    点位归组与多寄存器占用规则、配置三份（Templates → data/configs → data/machine.json）不入库、
    新功能放 Features 不往 MainViewModel 堆字段
  - 坑与现状：宽表阻断列、长表 200 行切段、采集异常分层、RawValue 改 FLOAT、绑定告警看哪份日志；
    P1/P2/触发记录已完成，P3 部分完成，P4/P5 未做
- 与 README（详细导读）/ todo.md（待办 + 现场约定）/ update.md（本变更记录）的分工写在文件开头

2026-09-18  重叠点位按"一个数据一列"落表：同一寄存器只算一个点位

【问题】
- 32 位浮点占 40001~40002，可 40002 上往往还挂着一条 16 位点位（现场点表常见：先按地址
  一个个加进去、再把头一个改宽）。建表脚本照旧给它出一列 —— 它读到的只是那个浮点的半截字节，
  出一列没有意义；之前还会直接报"寄存器重叠"把人拦在建表外面

【变更】
- 新增 HmiCore/Models/TagLayout.cs：**同一个寄存器只算一个点位**。按起始地址排序（同地址时占得宽的优先），
  先占的算数，被它盖住的标记为 CoveredBy = 那个点位；Resolve 给出逐个落位，EffectiveTags 给出"算数的点位"
- 建表/宽表补列/点位计数只按算数的点位（StorageViewModel.TagsOfGroup），
  建表预览段首点名"已并入大点位的 X 不出列"，免得人以为少了列
- 采集与落库只采算数的点位（AcquisitionController）；看板一张卡片一个数据，被盖住的不出卡片
- StorageGroupValidator：不再因重叠报错，只保留"多寄存器点位要整段在区段内"；
  宽表列数上限按算数的点位数算
- 点位表「占用」列改为按落位算：被盖住的写「被「温度」占用」并锁住类型；盖住别人的写「压住了「压力」」
  （不锁，能改回 16 位），同一处实现，界面与建表不会再有两套说法

【验证】
- dotnet build c_Test.sln：0 警告 0 错误；dotnet test：162 个用例全通过
  （新增 TagLayoutTests：32/64 位盖住后面的、同地址取宽的、乱序传入；StorageGroupValidatorTests
   的"重叠拦下"改成"重叠不算错、被盖住的不算数据"）

2026-09-18  点位表界面补正：16 位不显示字序、寄存器被占用当场标红并锁住类型

【问题】
- 16 位量只有一个寄存器，点位上却有「字序」下拉，看着像要选（选了也没意义）
- 把某个点位改宽（16 位 → 32 位）会盖住后面的点位，但界面照旧让第二个点位继续选类型，
  只到点【建表 / 校验结构】才报"寄存器重叠"，那时已经觉得莫名其妙

【变更】
- ScanView：字序列在 16 位点位显示「—」（不可点），只有 32/64 位点位才出下拉
- 点位表新增「占用」列：被别的点位压住的一行红字「被「点位3」占用」，压住别人的那行红字「压住了「点位4」」
- **被压住的那行锁住「类型」「字序」**（再改只会把冲突改大）；压住别人的那行不锁 ——
  留一条"把它改回 16 位"的路，否则两边都动不了、只能删配置。冲突一解开锁自动恢复
- PlcTag：新增运行期展示值 OverlapText / HasOverlap / CanEditType（均 [JsonIgnore]，不写配置）
  与 IsMultiRegister；StorageViewModel.RefreshTagGroups 里跟着归组一起重算重叠
- 界面排版：两张卡片的标题行都改成"标题一行、按钮一行"（用 WrapPanel，窄窗口自动换行）；
  SectionHeader 模板由横向 StackPanel 改成 Grid，灰提示放在 * 列并开字符省略 ——
  之前提示/按钮一多，标题行会被撑得比卡片还宽，直接压到隔壁卡片上（扫描结果那排控件最明显）

【验证】
- dotnet build c_Test.sln：0 警告 0 错误；dotnet test：156 个用例全通过

2026-09-18  点位支持多寄存器（32/64 位量与字序）：扫描时合并、采集按点位边界拆段

【动机】
- 现场很多量是 32 位浮点（也有 64 位 double），在 PLC 里横跨两个/四个 16 位寄存器；
  之前一个点位 = 一个寄存器，宽表就把一个浮点拆成了两列无关的数，落库数据是错的
- 需要在"采集点位"这一步就能把它们标成一个点位，并选对字序（ABCD / CDAB 等）

【新增】
- HmiCore/Models/PlcValueCodec.cs   数据类型（16/32/64 位，有无符号、浮点）+ 字序（高字在前 / 低字在前 /
  字内字节交换 / 字节全反转）+ 解码规则（唯一的实现处）+ 下拉项
- PlcTag：新增 DataType（老配置缺省 Int16，与老行为一致）与 WordOrder（缺省 HighWordFirst）、
  RegisterCount / EndAddress，人读地址在多寄存器时显示成 40001~40002
- ScanViewModel：【合并为点位】放在**采集点位表**上，认点位表里选中的相邻几行 —— 扫描结果只看得见
  一个个 16 位寄存器，认不出哪两个是一个浮点，类型/字序本来就是到点位表这侧才定的。
  合并 = 把第一行改成选定宽度 + 删掉后面的行（名称/单位/换算沿用第一行），合并前先校验不压住别的点位；
  【拆开多寄存器】是它的逆操作，两个按钮并排
- ScanView：扫描结果区只留【全选可读】【加入点位表】；采集点位表区加「合并为 / 字序」下拉与
  【合并为点位】；点位表加「类型」「字序」两个常驻下拉列

【变更】
- Sample.RawValue：short → double（32/64 位量的解码值放得下）；看板原始值按需显示小数
- PlcAcquisitionService：按区段读回后按点位类型/字序解码；**拆 125 寄存器上限的小段时先收窄到不切开任何
  点位的位置**（否则一个 float 的两个寄存器各读一半）；整段失败的回退路径对多寄存器点位整段重读
- 区段归属判定一律改成"整段落在区段内"：StorageGroup.CoversRange、AcquisitionController 的读取区段与落库
  归组、StorageViewModel 的归组展示、DashboardViewModel 找表
- StorageGroupValidator：新增点位占用校验 —— 多寄存器点位要整段在一个区段内、两个点位不能压住同一个寄存器
  （未归组的点位不采集不落库，不参与校验）
- DynamicTableStore：长表 `[RawValue] SMALLINT` → `FLOAT`（32/64 位量的原始值装得下）；
  老库里的长表需执行 `ALTER TABLE <表> ALTER COLUMN [RawValue] FLOAT NOT NULL;`
- ScanViewModel 加入点位表时按"寄存器已被点位占用"跳过（多寄存器点位占的整段都算占用）

【验证】
- dotnet build c_Test.sln：0 警告 0 错误；dotnet test：156 个用例全通过
  （新增 PlcValueCodecTests：四种字序的字节图案、16/32/64 位有符号无符号；
    AcquisitionServiceTests：跨 125 分段不被切开、整段失败按点位整段重读；
    StorageGroupValidatorTests：跨出区段 / 寄存器重叠 / 未归组不校验）

2026-09-18  仿真器加启动开关：--slave 只起指定从站、--verbose 才打每次读请求

【动机】
- 一次起 6 个从站（5 台机台 + 握手）时控制台太杂，调试某台机台时只想要那一个从站
- 「每次读请求」的日志在上位机按区段轮询时一秒十几行，把有用信息淹没

【变更】
- PlcSimulator/Program.cs：加命令行解析（ParseArgs + Usage 文本）
  --slave 1 / --slave 1,6   只把指定从站挂到端口上，其余从站在端口上不存在（探测、扫描结果更干净）
  --verbose / -v            打印主站的每次读请求（默认关闭，省日志）
  --help / -h               打印用法
  参数写错 → 打印错误 + 用法，退出码 1
  不指定 --slave 时行为与原来一致（1~5 机台 + 6 握手）
- start.md：补一条「只起一个从站」的运行命令

【验证】
- dotnet build PlcSimulator：0 警告 0 错误
- dotnet run -- --slave 1：只挂 1 个从站、每秒一行摘要；--slave 9 与 --help 的提示、退出码正确
- 资源实测（本机自测，供参考）：空载约 0.2% 单核 / 48 MB；
  5 个从站、每秒合计读 2500 个寄存器时约 1.4% 单核 / 57 MB

2026-09-18  仿真器：从站 1~5 = 五种机台，40001~40100 每 5 个一个区段（一张表）

【动机】
- 原来的仿真数据是「挤出机 / 注塑机 / 公用」三台虚构设备，与现场的五个机型
  （AIXP / AITN / AIBM / AIIR / AIPC）对不上，上位机按机台号采的时候没有对应的假数据
- 现场约定：一个寄存器区段就是一张表，仿真器要按这个结构供数

【变更】
- PlcSimulator/DeviceProfile.cs
  - 新增 MachineSpec：机型代号 + 名称 + 5 列（列名 + 原始值量程），顺序即从站号 ——
    从站 1=AIXP 分离封装机、2=AITN 辊压机、3=AIBM 成型机、4=AIIR 提纯机、5=AIPC 大片机
  - 每个机台从站开放 40001~40100（100 个寄存器）= 20 个区段 × 5 个寄存器；
    一个区段的 5 个寄存器就是一张表的 5 列（列 = 寄存器序号 % 5），20 个区段各自独立随机
  - 数据每秒按区段重新随机（在列量程内取值）；量程是占位值，现场点表定了改 MachineSpec.All 即可
  - 握手测试设备挪到从站 6（不占用机台数据区 40001~40100），握手时序不变
- PlcSimulator/Program.cs
  - 启动打印支持多行点位说明（区段范围 AIXP01(40001~40005) ~ AIXP20(40096~40100) + 5 列量程）
  - 每秒摘要改成只打印首尾两个区段，避免 100 个寄存器把控制台刷满
- README：仿真器一节、从站数、扫描范围（40001~40100）、排查表同步更新

【验证】
- dotnet build c_Test.sln：0 警告 0 错误
- 实跑：5 个机台从站各 20 个区段出随机值、各列都落在定义量程内，从站 6 握手日志正常

2026-09-18  目录结构重构：PlcCore → HmiCore，PlcHmi 分成"外壳 + 功能模块"

【动机】
- 后续要往上位机上加别的功能（AOI 拍照存图等），原结构里没有"功能模块"这一层：
  窗口全平铺在 PlcHmi 根目录、真正的服务挤在 PlcCore/Services 一个平层里，
  "相机服务该放哪"没有答案，只能继续往平层与外壳视图模型里塞
- PlcCore 这名字也不合适了：它装的其实是整台上位机的地基，不止 PLC

【变更】
- PlcCore → HmiCore：目录、csproj、RootNamespace / AssemblyName、全部命名空间、
  c_Test.sln、PlcHmi / PlcTests 的项目引用
- HmiCore 按关注点拆开（原 Services 与 Data 两个平层）：
  Comm/         IModbusClient、ModbusService、ModbusScanService、SlaveProbeService、ConnectionController
  Acquisition/  PlcAcquisitionService、AcquisitionController、TriggerWatchService、ShellStatusController
  Storage/      SqlConnectionFactory、DynamicTableStore、TriggerLogStore、StorageGroupValidator
  Config/       AppPaths、ConfigStore、MachineStore、ProfileResolver、MachineProfileController
  Models/       不变；AppServices.cs 提到库根（命名空间 HmiCore）
- PlcHmi 分成外壳与功能两块：
  Views/               MainWindow（顶栏 + 页签容器）
  Views/Dialogs/       机台号 / 密码 / PLC 配置 / 表记录历史四个弹窗
  ViewModels/          MainViewModel（外壳）
  Features/Plc/Views/        四个页签从 MainWindow.xaml 抽成 UserControl：
                             DashboardView / ScanView / StorageView / TriggerView
  Features/Plc/ViewModels/   页签视图模型跟着挪进来
  Features/Aoi/               AOI 拍照存图的落点（仅占位）
- MainWindow.xaml 774 行 → 341 行；CardColumns 依赖属性与曲线点击处理随看板页移到 DashboardView
- README：目录树重写、项目表与文件链接同步、新增「分层约定」一节

【验证】
- dotnet build c_Test.sln：0 警告 0 错误
- dotnet test：125 个用例全通过
- README / start.md / C_study.md 里的文件链接全部指向存在的文件

2026-09-18  业务层下沉到 PlcCore：模型与服务脱离 WPF，测试工程不再依赖界面

【变更】
- PlcHmi 的 Models 8 个（点位 / 机型 / 存储 / 触发规则等）与 Services 12 个（配置 / 动态表 /
  采集 / 扫描 / 触发等）整体迁入 PlcCore，命名空间变为 PlcCore.Models / PlcCore.Services
- PlcCore 新增 AcquisitionController、ConnectionController；补 CommunityToolkit.Mvvm
  （领域模型是可观察对象）与 InternalsVisibleTo PlcTests
- PlcHmi 只留界面：新增浅 / 深主题（Themes/PaletteLight|Dark、AppTheme）、SectionHeader 控件、
  密码窗口与点位配置窗口
- PlcTests：由引用 PlcHmi 改为引用 PlcCore，TargetFramework 从 net8.0-windows 回到 net8.0，
  测纯逻辑不再需要 WPF 目标平台

【验证】
- dotnet build c_Test.sln：0 警告 0 错误
- dotnet test：100 个用例全通过

2026-09-18  新增 publish.bat：一键打包单文件 exe，并忽略 publish/ 输出

【新增】
- publish.bat：把 PlcHmi 打成单个自包含 exe，等价于
  dotnet publish PlcHmi\PlcHmi.csproj -c Release -r win-x64 --self-contained true
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ./publish
  - 先切到脚本所在目录，双击也能跑；dotnet 不在 PATH 时回退到 %LOCALAPPDATA%\Microsoft\dotnet
  - 打包前清空 publish/，避免上一次残留的 dll / pdb 混进安装包；只在失败时 pause

【变更】
- .gitignore：新增 publish/（构建产物，不入库）

【说明】
- 单文件只打包托管程序集与原生库；Templates\*.json 是 Content 内容文件，仍以文件夹形式与 exe 同层，
  运行时 AppPaths.Templates 找的就是 exe 同目录，分发时两者必须一起拷

【验证】
- 实测：publish/ 下只有 PlcHmi.exe（约 208.6 MB）与 Templates/ 5 个 json

2026-09-17  界面：机台号左上角大字，机型不给手选，改机台号要密码，顶部栏重排

【变更】
- MainWindow.xaml 顶部栏重排成三列：左边机台号大字（30px）+ 下面一行机型说明；
  竖线右边分两行 —— 上行「PLC IP / 端口 / 从站号 / 探测从站 / 探测结果」，
  下行「采集周期 + 连接/采集/落库三个开关 + 状态灯 + 状态文字」
- 机台号不再在状态栏编辑：状态栏只留配置文件路径、状态与告警，去重
- 去掉「机型」下拉：机型只由机台号前 4 位决定，不给手工选
  （MainViewModel 去掉 Profiles / SelectedProfile / _switchingProfile，状态栏不再重复机型）
- 机台号小写自动补大写（aixp001 → AIXP001，MachineModels.Normalize）
- 认不出机型时不切机型、只标黄提示，避免一个笔误就换掉配置
- 更新日志由 update.log 更名为 update.md

【新增】
- 改机台号走 MachineNoWindow 的密码校验（requirePassword: true，密码 shjie12345）；
  首次进入填写不用密码。密码是防误改的软门槛，注释里写明了它不是安全机制
- PlcTests：MachineModelsTests 补 Normalize 用例（大小写、空格、未知代号）

【验证】
- dotnet build c_Test.sln：0 警告 0 错误
- dotnet test：92 个用例全通过

2026-09-17  机型由机台号决定：机型模板随包发布，机器级配置独立成 data/machine.json

【动机】
- 每个机型一份配置、同型号完全可复用；但机台号与连接参数原先写在机型配置里，导致同名机也各不相同
- 打包（dotnet publish）不带 data/，现场装机后拿不到配置，只能退回内置默认点位

【新增】
- PlcHmi/Models/MachineModel.cs     机台号 → 机型：AIXP 分离封装机 / AITN 辊压机 / AIBM 成型机 /
                                    AIIR 提纯机 / AIPC 大片机；机台号 = 4 字母 + 3 数字
- PlcHmi/Models/MachineProfile.cs   本机档案：机台号 + 连接参数 + 当前机型
- PlcHmi/Services/MachineStore.cs   读写 data/machine.json
- PlcHmi/Services/ProfileResolver.cs 启动选机型：--config > --machine > 档案里的已知机型 >
                                    机台号 > 自定义档案 > 已知机型第一个
- PlcHmi/Templates/AI{XP,TN,BM,IR,PC}.json 机型模板（csproj 里 Content + PreserveNewest，publish 会带上）

【变更】
- PlcConfig：去掉 Connection 与 MachineNo，改为只读老文件的 LegacyConnection / LegacyMachineNo
- MainViewModel：机台号改一下即自动切到对应机型（认不出则提示，可从下拉手工选）；
  连接参数读写改走本机档案；机型下拉 = 已知机型 + data/configs 里已有的自定义档案（不再自由敲名字）
- ConfigStore：新增 EnsureConfigFile（data/configs 缺就从 Templates 复制）与 CopyConfigFile（迁移用）；
  去掉 ResolveStartupProfile / RememberProfile（由 ProfileResolver + machine.json 取代）
- PlcTag.GroupText 标 [JsonIgnore]：展示值不进配置，同名机的文件才不会有差异
- AppPaths：新增 MachineFile / Templates，去掉 LastProfileFile

【迁移】
- 老配置里的机台号、连接参数搬到 data/machine.json 并清掉老字段
- 机台号认出的机型若还没有档案，直接把老档案整份复制成 <代号>.json 当初始内容（老档案保留，下拉可选回）

【打包 / 版本库】
- .gitignore 改为 data/* 全忽略；随代码走的是 PlcHmi/Templates/*.json
- 修正原规则 data/configs/ + data/configs/** 与注释矛盾（实际把配置也忽略了）的写法

【验证】
- dotnet build c_Test.sln：0 警告 0 错误；模板已随构建复制到输出目录 Templates/
- dotnet test：87 个用例全通过（新增 MachineModelsTests：格式、大小写、未知代号）

2026-09-17  仓库卫生：运行产物不入库，data/ 只放行机型配置

【变更】
- .gitignore 新增规则：
  - data/*              运行产物：日志 / 导出 / 更新包 / 上次机型，各机器自己生成
  - !data/configs/**    例外放行：机型配置 <机型>.json 跟着代码走
  - .write-probe-*      AppPaths 在根目录试写探针后删除，异常退出可能残留
  - TestResults/、*.trx  dotnet test --collect / --logger trx 的产物
- 写法说明：不能直接写 "data/"，被排除的目录 git 不会进入，后面的 ! 放行会失效，
  所以用 data/* + !data/configs/** 组合

【核对结果】
- 不入库：data/logs/plc-*.log、data/last-profile.txt、.env、各项目 bin/ 与 obj/
- 仍入库：data/configs/<机型>.json（配置）、update.log（本日志）

【推送】
- 本地提交已推送到 origin/main（file:////rd-test/git-test/Hello_c.git）

2026-09-17  重构：把地基逻辑抽离到独立类库 PlcCore

【新增】
- PlcCore 类库（net8.0，RootNamespace=PlcCore）
  - Models/DataRecord.cs            数据库实体
  - Data/AppDbContext.cs            EF Core 上下文（连接串来自 .env）
  - Data/AppDbContextFactory.cs     给 dotnet ef 用的设计时工厂
  - Services/ModbusService.cs       Modbus TCP 通信封装
  - Migrations/                     EF Core 迁移脚本（3 个文件）
  - 命名空间统一为 PlcCore.Models / PlcCore.Data / PlcCore.Services / PlcCore.Migrations

【变更】
- PlcDataCollector：由「采集核心」改为「命令行最小示例」
  - 只保留 Program.cs、Services/DataCollectionService.cs
  - 包引用精简为仅 DotNetEnv，改为 ProjectReference 引用 PlcCore
- PlcHmi：ProjectReference 由 PlcDataCollector 改为 PlcCore
  - 4 处 using 由 PlcDataCollector.* 改为 PlcCore.*
    文件：MainViewModel.cs / DynamicTableStore.cs / PlcAcquisitionService.cs / ModbusScanService.cs
  - 业务逻辑未改动
- README.md：项目表、目录树、9 处文件链接同步更新
- C_study.md：未改动（按老结构写的学习笔记）

【影响】
- dotnet ef 迁移命令需改在 PlcCore 目录执行：
    dotnet ef migrations add <名称> --project PlcCore\PlcCore.csproj
- PlcSimulator：无改动

【验证】
- dotnet build PlcCore / PlcDataCollector / PlcHmi / PlcSimulator 全部成功，0 警告 0 错误
- dotnet ef migrations list --no-connect --project PlcCore：
  EF 已能发现 PlcCore 中的设计时工厂，仅因本地缺 .env 未列出迁移（未完成项，需在有 .env 的环境复验）
