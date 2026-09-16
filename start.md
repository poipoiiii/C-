# 终端 1：启动仿真 PLC
cd d:\taoxi_download_for_use\no_use_do\c_Test
dotnet run --project PlcSimulator\PlcSimulator.csproj

# 终端 2：采集端连仿真 PLC
cd d:\taoxi_download_for_use\no_use_do\c_Test
dotnet run --project PlcDataCollector\PlcDataCollector.csproj -- 127.0.0.1 5020