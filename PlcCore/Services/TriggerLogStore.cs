using System.Data;
using PlcCore.Models;

namespace PlcCore.Services;

/// <summary>
/// 触发记录表：结构固定（时间戳 + 机台号 + 规则名 + 信号/应答地址），所以不走动态表那套按点位建列的机制，
/// 只借它一份标识符转义。只记"事件发生过"，不含点位快照 ——
/// 批次号/卷号这类业务字段由后续处理写入，直接给这张表加列即可。
/// </summary>
public sealed class TriggerLogStore
{
    public const string DefaultTableName = "TriggerLog";

    /// <summary>表不存在就建；已存在就补上后来新增的列（这张表是我们自己的，只增不减）</summary>
    public async Task EnsureTableAsync(string table)
    {
        DynamicTableStore.EnsureValidIdentifier(table, "触发记录表名");

        await using var connection = await DynamicTableStore.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"IF OBJECT_ID(@name, N'U') IS NULL " +
            $"CREATE TABLE {DynamicTableStore.Quote(table)} (" +
            "[Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY, " +
            "[Timestamp] DATETIME2 NOT NULL, " +
            "[MachineNo] NVARCHAR(64) NULL, " +
            "[RuleName] NVARCHAR(64) NOT NULL, " +
            "[SignalAddress] INT NOT NULL, " +
            "[AckAddress] INT NOT NULL); " +
            // 老表补列：机台号是后来加的，不能让现场已经建好的表插入失败
            $"IF COL_LENGTH(@name, N'MachineNo') IS NULL " +
            $"ALTER TABLE {DynamicTableStore.Quote(table)} ADD [MachineNo] NVARCHAR(64) NULL";
        DynamicTableStore.AddParameter(command, "@name", table);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>记一条触发记录；机台号从机型配置里带过来</summary>
    public async Task InsertAsync(string table, TriggerRule rule, DateTime timestamp, string machineNo)
    {
        DynamicTableStore.EnsureValidIdentifier(table, "触发记录表名");

        await using var connection = await DynamicTableStore.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {DynamicTableStore.Quote(table)} " +
            "([Timestamp],[MachineNo],[RuleName],[SignalAddress],[AckAddress]) VALUES (@t,@m,@n,@s,@a)";
        DynamicTableStore.AddParameter(command, "@t", timestamp);
        DynamicTableStore.AddParameter(command, "@m",
            string.IsNullOrWhiteSpace(machineNo) ? DBNull.Value : machineNo);
        DynamicTableStore.AddParameter(command, "@n", rule.Name);
        DynamicTableStore.AddParameter(command, "@s", (int)rule.SignalAddress);
        DynamicTableStore.AddParameter(command, "@a", (int)rule.AckAddress);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>读最近 N 条触发记录</summary>
    public async Task<DataTable> QueryRecentAsync(string table, int count)
    {
        DynamicTableStore.EnsureValidIdentifier(table, "触发记录表名");

        await using var connection = await DynamicTableStore.OpenAsync();
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
