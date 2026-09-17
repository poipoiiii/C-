# 终端 1：仿真 PLC
$env:PATH += ';C:\Users\tao.xi\AppData\Local\Microsoft\dotnet';cd d:\taoxi_download_for_use\no_use_do\c_Test;dotnet run --project PlcSimulator\PlcSimulator.csproj

# 终端 2：上位机界面（默认已填 127.0.0.1:5020，点【连接】即开始）
$env:PATH += ';C:\Users\tao.xi\AppData\Local\Microsoft\dotnet';dotnet run --project PlcHmi\PlcHmi.csproj