# 终端 1：仿真 PLC（从站 1~5 = 五种机台，6 = 握手测试）
$env:PATH += ';C:\Users\tao.xi\AppData\Local\Microsoft\dotnet';cd 'd:\local_web\C#_上位机\Hello_c';dotnet run --project PlcSimulator\PlcSimulator.csproj -- --slave 1

# 终端 2：上位机界面（默认已填现场 PLC 192.168.1.88:502；连仿真器时改成 127.0.0.1:5020，点【连接】即开始）
$env:PATH += ';C:\Users\tao.xi\AppData\Local\Microsoft\dotnet';cd 'd:\local_web\C#_上位机\Hello_c';dotnet run --project PlcHmi\PlcHmi.csproj

# 指定机型启动（不指定就用上次用的那个；机型 = data/configs/<机型>.json）
$env:PATH += ';C:\Users\tao.xi\AppData\Local\Microsoft\dotnet';cd 'd:\local_web\C#_上位机\Hello_c';dotnet run --project PlcHmi\PlcHmi.csproj -- --config 挤出机A

# 跑测试（不需要 PLC，也不需要数据库）
$env:PATH += ';C:\Users\tao.xi\AppData\Local\Microsoft\dotnet';cd 'd:\local_web\C#_上位机\Hello_c';dotnet test
