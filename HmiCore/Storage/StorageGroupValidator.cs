using HmiCore.Models;

namespace HmiCore.Storage;

/// <summary>
/// 存储组校验：建表/保存前必须全部通过，否则宁可不动数据库。
/// 单独抽成静态方法是为了能直接单测 —— 这些规则全是"拦得住错误配置"的硬约束，
/// 漏一条就可能把两张表写到一起或让采集读到界外地址。
/// </summary>
public static class StorageGroupValidator
{
    /// <summary>
    /// 校验组表：组名唯一非空、表名合法、一张表只归一个组、宽表别超列数上限、区段不重叠。
    /// 顺手把组名与表名两端的空格去掉（就地修改传入的组），后面就不用到处 Trim。
    /// 点位相关只验"算数的点位"（重叠时被盖住的不算数据，见 <see cref="TagLayout"/>）。
    /// </summary>
    public static void Validate(IReadOnlyList<StorageGroup> groups, IReadOnlyList<PlcTag> tags)
    {
        if (groups.Count == 0)
        {
            throw new InvalidOperationException("至少要有一个存储组。");
        }

        var effective = TagLayout.EffectiveTags(tags);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tableOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            group.Name = group.Name?.Trim() ?? string.Empty;
            group.TableName = group.TableName?.Trim() ?? string.Empty;

            if (group.Name.Length == 0)
            {
                throw new InvalidOperationException("组名不能为空。");
            }

            if (!names.Add(group.Name))
            {
                throw new InvalidOperationException($"组名「{group.Name}」重复：组名是区段的标识，必须唯一。");
            }

            if (group.Count == 0)
            {
                throw new InvalidOperationException($"组「{group.Name}」的区段长度是 0，至少要有 1 个寄存器。");
            }

            if (group.EndAddress > 65535)
            {
                throw new InvalidOperationException(
                    $"组「{group.Name}」的区段超出地址范围：起始 {group.StartAddress} + {group.Count} 个，" +
                    $"结束地址 {group.EndAddress} 超过 65535。");
            }

            DynamicTableStore.EnsureValidIdentifier(group.TableName, "表名");

            // 两个组写同一张表，列和点位会互相打架（宽表尤其明显），直接拦下
            if (tableOwners.TryGetValue(group.TableName, out var owner))
            {
                throw new InvalidOperationException($"表「{group.TableName}」同时被组「{owner}」和「{group.Name}」使用，请分开。");
            }

            tableOwners[group.TableName] = group.Name;

            int tagCount = CountTagsIn(group, effective);
            int maxPoints = DynamicTableStore.MaxPointColumns(group.Shape);
            if (group.Mode == StorageMode.Wide && tagCount > maxPoints)
            {
                throw new InvalidOperationException(
                    $"组「{group.Name}」是宽表但有 {tagCount} 个点位，超过单表列数上限" +
                    $"（{maxPoints} 个），请拆成多个组或改用长表。");
            }

            // Id、时间戳、机台号全关掉、又是宽表且区段内一个点位都没有 → 这张表建出来一列都没有
            if (!group.UseIdentityColumn && !group.UseTimestampColumn && !group.UseMachineNoColumn
                && group.Mode == StorageMode.Wide && tagCount == 0)
            {
                throw new InvalidOperationException(
                    $"组「{group.Name}」把 Id、时间戳、机台号都关掉了，区段内又没有点位，这张表会一列都没有。");
            }
        }

        // 区段重叠会让同一个地址同时属于两个组：采集读两遍、落库写两张表，直接拦下
        for (int i = 0; i < groups.Count; i++)
        {
            for (int j = i + 1; j < groups.Count; j++)
            {
                if (groups[i].StartAddress <= groups[j].EndAddress && groups[j].StartAddress <= groups[i].EndAddress)
                {
                    throw new InvalidOperationException(
                        $"组「{groups[i].Name}」（{groups[i].DisplayRange}）和组「{groups[j].Name}」" +
                        $"（{groups[j].DisplayRange}）的区段重叠了，同一个地址不能属于两个组。");
                }
            }
        }

        ValidateTagSpans(groups, effective);
    }

    /// <summary>
    /// 点位占用校验：一个点位可能占好几个寄存器（32/64 位类型），所以它必须整段落在同一个区段里 ——
    /// 只落一半的话区段读回来的是残缺值。传进来的已经是"算数的点位"，
    /// 同一个寄存器只归一个点位（这事由 <see cref="TagLayout"/> 定，不在这里报错）。
    /// 没归组的点位不参与采集与落库，不参与校验。
    /// </summary>
    private static void ValidateTagSpans(IReadOnlyList<StorageGroup> groups, IReadOnlyList<PlcTag> tags)
    {
        foreach (var tag in tags)
        {
            var group = groups.FirstOrDefault(g => g.Covers(tag.Address));
            if (group is null)
            {
                continue;
            }

            if (!group.CoversRange(tag.Address, tag.EndAddress))
            {
                throw new InvalidOperationException(
                    $"点位「{tag.Name}」（{tag.DisplayAddress}）是{PlcValueCodec.Display(tag.DataType)}，" +
                    $"占 {tag.RegisterCount} 个寄存器，跨出了组「{group.Name}」的区段（{group.DisplayRange}）。" +
                    "把区段调大，或把类型改回 16 位。");
            }
        }
    }

    /// <summary>落在某个区段内的点位数（区段是唯一判定依据；多寄存器点位要整段在区段里）</summary>
    private static int CountTagsIn(StorageGroup group, IReadOnlyList<PlcTag> tags) =>
        tags.Count(t => group.CoversRange(t.Address, t.EndAddress));
}
