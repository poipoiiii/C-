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
