using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PlcCore.Data;

/// <summary>
/// 供 `dotnet ef` 命令（设计时）创建 DbContext 用。
/// 设计时会话不会执行 Program.cs，所以这里要自己先加载 .env，否则读不到连接串。
/// 注意：请在 PlcCore 目录下执行 dotnet ef，.env 才在搜索路径上。
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        Env.Load();
        return new AppDbContext();
    }
}
