@echo off
rem 把 PlcHmi 打包成单个自包含 exe：PlcHmi.exe（含 .NET 运行时与全部原生库）
rem 用法：双击本文件，或在仓库根目录执行 publish.bat
chcp 65001 >nul
setlocal

rem 切到脚本所在目录，保证双击运行时相对路径也对
cd /d "%~dp0"

rem dotnet 不在 PATH 时，回退到用户级安装目录
where dotnet >nul 2>nul
if errorlevel 1 set "PATH=%PATH%;%LOCALAPPDATA%\Microsoft\dotnet"

rem 先清掉上次产物：单文件打包最容易把上一次残留的 dll / pdb 一起发出去
if exist "publish" rd /s /q "publish"

echo [1/2] 正在打包 Release / win-x64 单文件 ...
dotnet publish PlcHmi\PlcHmi.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ./publish
if errorlevel 1 goto fail

echo.
echo [2/2] 打包完成，产物：
for %%F in ("publish\PlcHmi.exe") do echo   PlcHmi.exe   %%~zF 字节
echo   Templates\  机型模板，必须与 exe 一起分发
echo.
echo 输出目录：%~dp0publish
echo 分发时把 PlcHmi.exe、Templates\ 和 .env 放在同一层即可。
exit /b 0

:fail
echo.
echo 打包失败，请查看上面的错误信息。
pause
exit /b 1
