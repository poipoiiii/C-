using Microsoft.Data.SqlClient;

namespace PlcCore.Data;

/// <summary>
/// 数据库连接工厂：只做两件事——按 .env 的四个变量拼连接串、按需开一个连接。
///
/// 表结构不由这里负责：目标表名/列名是运行时才定的（见 PlcHmi 的 DynamicTableStore），
/// 所以全项目统一走 ADO.NET 拼 SQL，不引入 ORM，也就没有实体和迁移需要维护。
/// </summary>
public static class SqlConnectionFactory
{
    /// <summary>
    /// 连接串从环境变量拼出来，账号密码不进源码和 git 历史。
    /// 每次现读，这样进程内换过环境变量（测试、切库）也能生效。
    /// </summary>
    public static string ConnectionString
    {
        get
        {
            string Get(string key) => Environment.GetEnvironmentVariable(key)
                ?? throw new InvalidOperationException($"缺少环境变量 {key}，请检查 .env 文件。");

            return $"Server={Get("DB_SERVER")};Database={Get("DB_NAME")};" +
                   $"User Id={Get("DB_USER")};Password={Get("DB_PASSWORD")};" +
                   "TrustServerCertificate=True;Connect Timeout=30;";
        }
    }

    /// <summary>开一个连接；调用方用 await using 释放</summary>
    public static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            // 开失败也要把连接释放掉，否则连接池里会挂着一个半开对象
            await connection.DisposeAsync();
            throw;
        }
    }
}
