# 终端 1：仿真 PLC
$env:PATH += ';C:\Users\tao.xi\AppData\Local\Microsoft\dotnet';cd d:\taoxi_download_for_use\no_use_do\c_Test;dotnet run --project PlcSimulator\PlcSimulator.csproj

# 终端 2：上位机界面（默认已填 127.0.0.1:5020，点【连接】即开始）
$env:PATH += ';C:\Users\tao.xi\AppData\Local\Microsoft\dotnet';dotnet run --project PlcHmi\PlcHmi.csproj

# 指定机型启动（不指定就用上次用的那个；机型 = data/configs/<机型>.json）
$env:PATH += ';C:\Users\tao.xi\AppData\Local\Microsoft\dotnet';dotnet run --project PlcHmi\PlcHmi.csproj -- --config 挤出机A

# 跑测试（不需要 PLC，也不需要数据库）
$env:PATH += ';C:\Users\tao.xi\AppData\Local\Microsoft\dotnet';cd d:\taoxi_download_for_use\no_use_do\c_Test;dotnet test
