using System.Data;
using PlcCore.Data;
using PlcHmi.Models;

namespace PlcHmi.Services;

/// <summary>
/// 触发记录表：结构固定（时间戳 + 规则名 + 信号/应答地址），所以不走动态表那套按点位建列的机制，
/// 只借它一份标识符转义。只记"事件发生过"，不含点位快照 ——
/// 机台号/批次号/卷号这类业务字段由后续处理写入，直接给这张表加列即可。
/// </summary>
public sealed class TriggerLogStore
{
    public const string DefaultTableName = "TriggerLog";

    /// <summary>表不存在就建；已存在不动</summary>
    public async Task EnsureTableAsync(string table)
    {
        DynamicTableStore.EnsureValidIdentifier(table, "触发记录表名");

        await using var db = new AppDbContext();
        var connection = await DynamicTableStore.OpenAsync(db);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"IF OBJECT_ID(@name, N'U') IS NULL " +
            $"CREATE TABLE {DynamicTableStore.Quote(table)} (" +
            "[Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY, " +
            "[Timestamp] DATETIME2 NOT NULL, " +
            "[RuleName] NVARCHAR(64) NOT NULL, " +
            "[SignalAddress] INT NOT NULL, " +
            "[AckAddress] INT NOT NULL)";
        DynamicTableStore.AddParameter(command, "@name", table);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>记一条触发记录</summary>
    public async Task InsertAsync(string table, TriggerRule rule, DateTime timestamp)
    {
        DynamicTableStore.EnsureValidIdentifier(table, "触发记录表名");

        await using var db = new AppDbContext();
        var connection = await DynamicTableStore.OpenAsync(db);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {DynamicTableStore.Quote(table)} " +
            "([Timestamp],[RuleName],[SignalAddress],[AckAddress]) VALUES (@t,@n,@s,@a)";
        DynamicTableStore.AddParameter(command, "@t", timestamp);
        DynamicTableStore.AddParameter(command, "@n", rule.Name);
        DynamicTableStore.AddParameter(command, "@s", (int)rule.SignalAddress);
        DynamicTableStore.AddParameter(command, "@a", (int)rule.AckAddress);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>读最近 N 条触发记录</summary>
    public async Task<DataTable> QueryRecentAsync(string table, int count)
    {
        DynamicTableStore.EnsureValidIdentifier(table, "触发记录表名");

        await using var db = new AppDbContext();
        var connection = await DynamicTableStore.OpenAsync(db);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT TOP (@count) * FROM {DynamicTableStore.Quote(table)} ORDER BY [Id] DESC";
        DynamicTableStore.AddParameter(command, "@count", count);

        var result = new DataTable();
        await using var reader = await command.ExecuteReaderAsync();
        result.Load(reader);
        return result;
    }
}
