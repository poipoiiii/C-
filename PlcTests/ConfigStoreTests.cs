using HmiCore.Models;
using HmiCore.Config;

namespace PlcTests;

/// <summary>机型名会被拼进配置文件名，白名单既是命名规范也是路径穿越的防线。</summary>
public class ConfigStoreTests
{
    [Theory]
    [InlineData("默认")]
    [InlineData("挤出机A")]
    [InlineData("line_1")]
    [InlineData("1号机")]
    [InlineData("A-1")]
    public void 合法机型名_通过(string name)
    {
        Assert.True(ConfigStore.IsValidProfileName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../evil")]      // 路径穿越
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("a.json")]       // 点号不在白名单，避免出现 机型.json.json
    [InlineData("a b")]
    [InlineData("机型?")]
    public void 非法机型名_拦下(string? name)
    {
        Assert.False(ConfigStore.IsValidProfileName(name));
    }

    [Fact]
    public void 机型名超过32字符_拦下()
    {
        Assert.False(ConfigStore.IsValidProfileName(new string('机', 33)));
    }

    // ===== 以下盯 Load / Migrate：启动即写盘的迁移逻辑，错了现场直接起不来 =====
    // 全部用临时目录里的文件（ConfigStore.OpenFile），绝不碰仓库的 data/configs。

    [Fact]
    public void 配置文件不存在_返回默认点位_不抛异常()
    {
        string dir = NewTempDir();
        try
        {
            var store = ConfigStore.OpenFile(Path.Combine(dir, "新机型.json"));

            var config = store.Load();

            Assert.Null(store.LoadWarning);
            Assert.Equal(PlcConfig.CurrentVersion, config.ConfigVersion);
            Assert.Equal("新机型", config.MachineName);
            Assert.Equal(PlcTag.Default.Count, config.Tags.Count);
            Assert.Single(config.Groups);
            Assert.False(File.Exists(store.FilePath));   // 读不到只给默认值，不凭空落盘
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 配置内容非法JSON_退回默认并告警_原文件不动()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "坏配置.json");
            File.WriteAllText(path, "{ 这不是合法 JSON ");
            var store = ConfigStore.OpenFile(path);

            var config = store.Load();

            string warning = store.LoadWarning ?? string.Empty;
            Assert.NotEmpty(warning);
            Assert.Contains("解析失败", warning);
            Assert.Equal(PlcTag.Default.Count, config.Tags.Count);
            Assert.Equal(PlcConfig.CurrentVersion, config.ConfigVersion);
            Assert.Equal("{ 这不是合法 JSON ", File.ReadAllText(path));   // 告警里承诺"原文件未改动"
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 旧格式配置_补齐版本与机型_原有内容原样保留并写回()
    {
        // 只有 Tags / Groups / Triggers：没有 ConfigVersion、MachineName、Connection
        const string legacyJson = """
        {
          "Tags": [
            { "Address": 0, "Name": "热封数量", "Unit": "件", "Scale": 1, "Offset": 0, "ColumnName": "热封数量" },
            { "Address": 3, "Name": "热封温度", "Unit": "℃", "Scale": 0.1, "Offset": 0, "ColumnName": "热封温度" }
          ],
          "Groups": [
            { "Name": "默认组", "TableName": "DataRecords", "Mode": 1, "StartAddress": 0, "Count": 20, "UseIdentityColumn": true, "UseTimestampColumn": true }
          ],
          "Triggers": [
            { "Name": "热封触发", "SignalAddress": 5, "AckAddress": 6, "Enabled": true, "TimeoutMs": 5000 }
          ]
        }
        """;

        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "旧机型.json");
            File.WriteAllText(path, legacyJson);
            var store = ConfigStore.OpenFile(path);

            var config = store.Load();

            // 版本 / 机型被补齐
            Assert.Null(store.LoadWarning);
            Assert.Equal(PlcConfig.CurrentVersion, config.ConfigVersion);
            Assert.Equal("旧机型", config.MachineName);

            // 原有内容原样保留
            Assert.Equal(2, config.Tags.Count);
            Assert.Equal(new ushort[] { 0, 3 }, config.Tags.Select(t => t.Address).ToArray());
            Assert.Equal("热封数量", config.Tags[0].Name);
            Assert.Equal(0.1, config.Tags[1].Scale);
            Assert.Equal("默认组", config.Groups[0].Name);
            Assert.Equal("DataRecords", config.Groups[0].TableName);
            Assert.Equal(StorageMode.Wide, config.Groups[0].Mode);
            Assert.Equal((ushort)20, config.Groups[0].Count);
            Assert.Equal((ushort)5, config.Triggers[0].SignalAddress);
            Assert.Equal((ushort)6, config.Triggers[0].AckAddress);

            // 迁移结果已写回文件（未迁移时 changed=true 会走 Save）
            string written = File.ReadAllText(path);
            Assert.Contains("\"ConfigVersion\": 1", written);
            Assert.Contains("\"MachineName\": \"旧机型\"", written);
            Assert.Contains("热封数量", written);
            Assert.DoesNotContain("\"Storage\"", written);   // 迁移完的字段不再写回
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 配置版本高于程序_退回默认并告警()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "未来版本.json");
            File.WriteAllText(path, """
            {
              "ConfigVersion": 999,
              "MachineName": "未来版本",
              "Tags": [ { "Address": 0, "Name": "温度", "Unit": "℃", "Scale": 0.1, "Offset": 0, "ColumnName": "温度" } ]
            }
            """);
            var store = ConfigStore.OpenFile(path);

            var config = store.Load();

            string warning = store.LoadWarning ?? string.Empty;
            Assert.NotEmpty(warning);
            Assert.Contains("高于", warning);
            Assert.Equal(PlcConfig.CurrentVersion, config.ConfigVersion);   // 不是 999，而是退回默认版本
            Assert.Equal(PlcTag.Default.Count, config.Tags.Count);          // 退回内置默认点位
            Assert.Contains("\"ConfigVersion\": 999", File.ReadAllText(path));   // 高版本配置不被程序改写
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 保存后读回一致_中文名称在json里不转义()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "热封机型.json");
            var store = ConfigStore.OpenFile(path);
            var config = new PlcConfig
            {
                Tags =
                [
                    new PlcTag(0, "热封数量", "件", 1, 0),
                    new PlcTag(1, "热封温度", "℃", 0.1, 0)
                ],
                Groups = [new StorageGroup(StorageGroup.DefaultName, "DataRecords", StorageMode.Wide, 0, 20)],
                Triggers = [new TriggerRule("热封触发", 5, 6)]
            };

            store.Save(config);

            string raw = File.ReadAllText(path);
            Assert.Contains("热封数量", raw);
            Assert.Contains("热封温度", raw);
            Assert.DoesNotContain("\\u", raw);   // UnsafeRelaxedJsonEscaping：配置文件要给人看

            var reloaded = ConfigStore.OpenFile(path).Load();
            Assert.Equal(PlcConfig.CurrentVersion, reloaded.ConfigVersion);
            Assert.Equal("热封机型", reloaded.MachineName);
            Assert.Equal(2, reloaded.Tags.Count);
            Assert.Equal("热封数量", reloaded.Tags[0].Name);
            Assert.Equal(0.1, reloaded.Tags[1].Scale);
            Assert.Equal("DataRecords", reloaded.Groups[0].TableName);
            Assert.Equal((ushort)20, reloaded.Groups[0].Count);
            Assert.Equal((ushort)5, reloaded.Triggers[0].SignalAddress);
            Assert.Equal((ushort)6, reloaded.Triggers[0].AckAddress);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>每个用例自己建临时目录，finally 里删干净，不往仓库或 %TEMP% 留垃圾</summary>
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "PlcTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
