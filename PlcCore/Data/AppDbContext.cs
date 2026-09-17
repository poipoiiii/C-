using Microsoft.EntityFrameworkCore;
using PlcCore.Models;

namespace PlcCore.Data;

/// <summary>
/// EF Core 数据库上下文（Code First）
/// </summary>
public class AppDbContext : DbContext
{
    /// <summary>
    /// 连接串从 .env 读取，避免账号密码进入源码和 git 历史
    /// </summary>
    private static string BuildConnectionString()
    {
        string Get(string key) => Environment.GetEnvironmentVariable(key)
            ?? throw new InvalidOperationException($"缺少环境变量 {key}，请检查 .env 文件。");

        return $"Server={Get("DB_SERVER")};Database={Get("DB_NAME")};" +
               $"User Id={Get("DB_USER")};Password={Get("DB_PASSWORD")};" +
               "TrustServerCertificate=True;Connect Timeout=30;";
    }

    public DbSet<DataRecord> DataRecords { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlServer(BuildConnectionString());
    }
}
