using DotNetEnv;
using PlcDataCollector.Data;
using PlcDataCollector.Services;

// 加载 .env 中的数据库连接配置（该文件不入库）
Env.Load();

// 目标 PLC：默认走真实 PLC；本地仿真时用命令行覆盖，例如 dotnet run -- 127.0.0.1 5020
string ip = args.Length > 0 ? args[0] : "192.168.1.10";
int port = args.Length > 1 ? int.Parse(args[1]) : 502;

// 初始化数据库（首次运行自动建表）
using (var db = new AppDbContext())
{
    db.Database.EnsureCreated();
}

using var modbus = new ModbusService(slaveId: 1);
modbus.Connect(ip, port);

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
