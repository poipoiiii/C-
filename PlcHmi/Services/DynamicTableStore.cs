using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using PlcCore.Data;
using PlcHmi.Models;

namespace PlcHmi.Services;

/// <summary>
/// 动态目标表：表名/列名由界面填写，EF 实体是编译期固定的，所以这里用 ADO.NET 拼 SQL。
/// 安全约束：表名列名不能参数化，一律先过 <see cref="EnsureValidIdentifier"/> 白名单校验，
/// 再用 [ ] 包裹转义，杜绝注入。
/// </summary>
public sealed class DynamicTableStore
{
    /// <summary>标识符白名单：中英文、数字、下划线，不能以数字开头，最长 64 字符</summary>
    private static readonly Regex IdentifierPattern =
        new(@"^[\u4e00-\u9fa5A-Za-z_][\u4e00-\u9fa5A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);

    /// <summary>长表固定列</summary>
    private static readonly string[] LongColumns =
        ["Timestamp", "RegisterAddress", "RawValue", "EngineeringValue"];

    /// <summary>SQL Server 单表列数上限</summary>
    public const int MaxColumnsPerTable = 1024;

    /// <summary>两个附加列都开时的点位数上限（<see cref="MaxPointColumns"/> 的最严情形）</summary>
    public const int MaxPointColumnsInWideTable = MaxColumnsPerTable - 2;

    /// <summary>宽表最多放多少个点位列：列数上限扣掉 Id / 时间戳这些非寄存器列</summary>
    public static int MaxPointColumns(TableShape shape) =>
        MaxColumnsPerTable - (shape.UseIdentityColumn ? 1 : 0) - (shape.UseTimestampColumn ? 1 : 0);

    public static void EnsureValidIdentifier(string name, string what)
    {
        if (string.IsNullOrWhiteSpace(name) || !IdentifierPattern.IsMatch(name))
        {
            throw new ArgumentException($"{what}「{name}」不合法：只能包含中英文、数字、下划线，且不能以数字开头（最长 64 字符）。");
        }
    }

    /// <summary>校验点位名/列名，并保证列名不重复</summary>
    public static void EnsureColumnsUsable(IReadOnlyList<PlcTag> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            EnsureValidIdentifier(tag.ColumnName, "列名");
            if (!seen.Add(tag.ColumnName))
            {
                throw new ArgumentException($"列名「{tag.ColumnName}」重复，宽表模式下每个点位要有唯一列名。");
            }
        }
    }

    /// <summary>
    /// 生成建表语句（同时用于界面预览）。
    /// Id / 时间戳这两列算"附加列"，加不加由 <see cref="TableShape"/> 决定 ——
    /// 关掉之后插入语句也不会写它们，所以三种列（Id / 时间戳 / 点位列）至少要剩一样。
    /// </summary>
    public static string BuildCreateScript(
        string table, StorageMode mode, IReadOnlyList<PlcTag> tags, TableShape? shape = null)
    {
        EnsureValidIdentifier(table, "表名");
        var layout = shape ?? TableShape.Default;

        var columns = new List<string>();

        if (layout.UseIdentityColumn)
        {
            columns.Add("[Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY");
        }

        if (layout.UseTimestampColumn)
        {
            columns.Add("[Timestamp] DATETIME2 NOT NULL");
        }

        if (mode == StorageMode.Long)
        {
            columns.Add("[RegisterAddress] INT NOT NULL");
            columns.Add("[RawValue] SMALLINT NOT NULL");
            columns.Add("[EngineeringValue] FLOAT NOT NULL");
        }
        else
        {
            EnsureColumnsUsable(tags);

            // 用可空列：某点位本次没读到（地址不存在等）时该列写 NULL，不阻断整行插入
            columns.AddRange(tags.Select(tag => $"{Quote(tag.ColumnName)} FLOAT NULL"));
        }

        if (columns.Count == 0)
        {
            throw new ArgumentException("这张表一列都没有：Id、时间戳、寄存器列至少要保留一样。");
        }

        return $"CREATE TABLE {Quote(table)} (\r\n    {string.Join(",\r\n    ", columns)}\r\n);";
    }

    /// <summary>列出库里已有的用户表</summary>
    public async Task<IReadOnlyList<string>> ListTablesAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME";

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public async Task<bool> TableExistsAsync(string table)
    {
        EnsureValidIdentifier(table, "表名");

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME = @name";
        AddParameter(command, "@name", table);

        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    /// <summary>建表：已存在则不动，返回是否新建</summary>
    public async Task<bool> CreateTableIfMissingAsync(
        string table, StorageMode mode, IReadOnlyList<PlcTag> tags, TableShape? shape = null)
    {
        if (await TableExistsAsync(table))
        {
            return false;
        }

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = BuildCreateScript(table, mode, tags, shape);
        await command.ExecuteNonQueryAsync();
        return true;
    }

    /// <summary>宽表结构同步的结果</summary>
    /// <param name="Added">本次新增的点位列</param>
    /// <param name="Relaxed">本次由非空改为可空的点位列（允许个别点位读失败）</param>
    /// <param name="BlockingNotNull">表里存在、但不属于当前点位表的非空列 —— 宽表插入不会写它们，会导致插入失败</param>
    public sealed record WideColumnSyncResult(
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Relaxed,
        IReadOnlyList<string> BlockingNotNull);

    /// <summary>
    /// 宽表结构同步：补齐缺的点位列；把点位表里那些"非空"的列改成可空
    /// （某点位本次读失败时该列写 NULL，不该因此让整行插入失败）。
    /// </summary>
    public async Task<WideColumnSyncResult> EnsureWideColumnsAsync(
        string table, IReadOnlyList<PlcTag> tags, TableShape? shape = null)
    {
        EnsureValidIdentifier(table, "表名");
        EnsureColumnsUsable(tags);

        var layout = shape ?? TableShape.Default;

        await using var connection = await OpenAsync();

        var existing = new Dictionary<string, (bool IsNullable, bool IsIdentity, string TypeName)>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT c.name, c.is_nullable, c.is_identity, ty.name FROM sys.columns c " +
                "JOIN sys.tables t ON t.object_id = c.object_id " +
                "JOIN sys.types ty ON ty.user_type_id = c.user_type_id " +
                "WHERE t.name = @name";
            AddParameter(command, "@name", table);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                existing[reader.GetString(0)] = (reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3));
            }
        }

        var added = new List<string>();
        foreach (var tag in tags.Where(t => !existing.ContainsKey(t.ColumnName)))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {Quote(table)} ADD {Quote(tag.ColumnName)} FLOAT NULL";
            await alter.ExecuteNonQueryAsync();
            added.Add(tag.ColumnName);
        }

        var relaxed = new List<string>();
        foreach (var tag in tags)
        {
            if (!existing.TryGetValue(tag.ColumnName, out var column) || column.IsNullable || column.IsIdentity)
            {
                continue;
            }

            // 只去掉 NOT NULL，列类型保持不变
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {Quote(table)} ALTER COLUMN {Quote(tag.ColumnName)} {column.TypeName} NULL";
            await alter.ExecuteNonQueryAsync();
            relaxed.Add(tag.ColumnName);
        }

        // 长表改建出来的表会留着 RegisterAddress/RawValue/EngineeringValue 这些非空列，
        // 切到宽表后插入不含它们，SQL Server 会直接报“不能将值 NULL 插入列”。这里提前点出来。
        // 同理，时间戳列被关掉时，表里那个非空 [Timestamp] 也会被点出来 —— 那正是插入会失败的原因。
        var written = new HashSet<string>(tags.Select(t => t.ColumnName), StringComparer.OrdinalIgnoreCase);
        if (layout.UseTimestampColumn)
        {
            written.Add("Timestamp");
        }

        var blocking = existing
            .Where(pair => !pair.Value.IsNullable && !pair.Value.IsIdentity && !written.Contains(pair.Key))
            .Select(pair => pair.Key)
            .ToList();

        return new WideColumnSyncResult(added, relaxed, blocking);
    }

    /// <summary>
    /// 长表单条 INSERT 最多带多少行：SQL Server 单命令参数上限 2100，每行 4 个参数 → 理论 525 行，
    /// 这里取 200 留余量。点位规模到几百个时，一次采完整批就会撞上限，必须切段。
    /// </summary>
    private const int MaxRowsPerInsert = 200;

    /// <summary>长表写入：一行一个点位</summary>
    public async Task InsertLongAsync(string table, IReadOnlyList<Sample> batch, TableShape? shape = null)
    {
        EnsureValidIdentifier(table, "表名");
        if (batch.Count == 0)
        {
            return;
        }

        var layout = shape ?? TableShape.Default;
        var columnNames = layout.UseTimestampColumn
            ? "[Timestamp],[RegisterAddress],[RawValue],[EngineeringValue]"
            : "[RegisterAddress],[RawValue],[EngineeringValue]";

        await using var connection = await OpenAsync();

        // 切段写但放在一个事务里：保持「一批要么全进、要么全不进」的语义，不留下半批数据
        await using var transaction = await connection.BeginTransactionAsync();

        for (int offset = 0; offset < batch.Count; offset += MaxRowsPerInsert)
        {
            int count = Math.Min(MaxRowsPerInsert, batch.Count - offset);
            var sql = new StringBuilder($"INSERT INTO {Quote(table)} ({columnNames}) VALUES ");
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;

            for (int i = 0; i < count; i++)
            {
                var sample = batch[offset + i];
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.Append(layout.UseTimestampColumn ? $"(@t{i},@a{i},@r{i},@e{i})" : $"(@a{i},@r{i},@e{i})");
                if (layout.UseTimestampColumn)
                {
                    AddParameter(command, $"@t{i}", sample.Timestamp);
                }

                AddParameter(command, $"@a{i}", (int)sample.Tag.Address);
                AddParameter(command, $"@r{i}", sample.RawValue);
                AddParameter(command, $"@e{i}", sample.EngineeringValue);
            }

            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>宽表写入：一行一个时刻，点位成列</summary>
    public async Task InsertWideAsync(string table, IReadOnlyList<Sample> batch, TableShape? shape = null)
    {
        EnsureValidIdentifier(table, "表名");
        if (batch.Count == 0)
        {
            return;
        }

        var layout = shape ?? TableShape.Default;
        var columnNames = new List<string>();
        var valueSlots = new List<string>();

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();

        if (layout.UseTimestampColumn)
        {
            columnNames.Add("[Timestamp]");
            valueSlots.Add("@t");
            AddParameter(command, "@t", batch[0].Timestamp);
        }

        for (int i = 0; i < batch.Count; i++)
        {
            var sample = batch[i];
            EnsureValidIdentifier(sample.Tag.ColumnName, "列名");
            columnNames.Add(Quote(sample.Tag.ColumnName));
            valueSlots.Add($"@v{i}");
            AddParameter(command, $"@v{i}", sample.EngineeringValue);
        }

        command.CommandText =
            $"INSERT INTO {Quote(table)} ({string.Join(",", columnNames)}) VALUES ({string.Join(",", valueSlots)})";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>读最近 N 条，列是动态的所以直接返回 DataTable</summary>
    public async Task<DataTable> QueryRecentAsync(string table, int count)
    {
        EnsureValidIdentifier(table, "表名");

        await using var connection = await OpenAsync();

        // Id 可以被用户关掉，那就退到 Timestamp；两个都没有只能不排序（表里没有顺序依据）
        string? orderColumn = await ResolveOrderColumnAsync(connection, table);

        await using var command = connection.CreateCommand();
        command.CommandText = orderColumn is null
            ? $"SELECT TOP (@count) * FROM {Quote(table)}"
            : $"SELECT TOP (@count) * FROM {Quote(table)} ORDER BY {Quote(orderColumn)} DESC";
        AddParameter(command, "@count", count);

        var result = new DataTable();
        await using var reader = await command.ExecuteReaderAsync();
        result.Load(reader);
        return result;
    }

    /// <summary>排序列：优先 Id，其次 Timestamp；两列都不存在则返回 null</summary>
    private static async Task<string?> ResolveOrderColumnAsync(DbConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CASE WHEN COL_LENGTH(@name, N'Id') IS NOT NULL THEN N'Id' " +
            "WHEN COL_LENGTH(@name, N'Timestamp') IS NOT NULL THEN N'Timestamp' END";
        AddParameter(command, "@name", table);

        return await command.ExecuteScalarAsync() as string;
    }

    // 下面三个是拼 SQL 的工具方法。internal 是为了让 TriggerLogStore 复用同一份
    // 标识符转义逻辑 —— 转义规则散成两份迟早会走偏。

    /// <summary>开一个数据库连接：动态表的建表/写入/查询都走这里，调用方用 await using 释放</summary>
    internal static async Task<DbConnection> OpenAsync() => await SqlConnectionFactory.OpenAsync();

    internal static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>标识符转义：[ ] 包裹并把内部的 ] 转义成 ]]</summary>
    internal static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
}
