namespace HmiCore.Models;

/// <summary>一个点位在寄存器上的落位：被谁盖住了（<c>CoveredBy</c> 为 null 表示它自己说了算）</summary>
public sealed record TagPlacement(PlcTag Tag, PlcTag? CoveredBy);

/// <summary>
/// 点位在寄存器上的落位规则：**同一个寄存器只归一个点位**。
///
/// 32/64 位量是把几个寄存器拼起来才是"一个数据"，所以它盖住的那几个单寄存器点位不算数据 ——
/// 它们读到的只是这个大数的半截字节。现场点表里常见的"先按地址一个个加进去、再把头一个改宽"
/// 就会造出这种重叠，上位机要能按"一个数据一列"落库，而不是把半截字节也列一列。
///
/// 谁的算数：按起始地址先后，先占的那个说了算（占得更宽的自然盖住后面的）；
/// 起始地址相同则占得宽的那个算。被盖住的点位保留在配置里（界面标红说明），
/// 只是不参与采集、落库、建表。
/// </summary>
public static class TagLayout
{
    /// <summary>算出每个点位的落位：谁算数、谁被谁盖住</summary>
    public static IReadOnlyList<TagPlacement> Resolve(IReadOnlyList<PlcTag> tags)
    {
        var ordered = tags
            .OrderBy(t => t.Address)
            .ThenByDescending(t => t.EndAddress)    // 起始地址相同时，占得宽的那个算
            .ToList();

        var placements = new List<TagPlacement>(ordered.Count);
        PlcTag? occupant = null;                    // 已占到最远位置的那个点位

        foreach (var tag in ordered)
        {
            if (occupant is not null && tag.Address <= occupant.EndAddress)
            {
                placements.Add(new TagPlacement(tag, occupant));
                continue;
            }

            placements.Add(new TagPlacement(tag, null));
            occupant = tag;
        }

        return placements;
    }

    /// <summary>真正算数的点位（采集、落库、建表都只看这一份）</summary>
    public static IReadOnlyList<PlcTag> EffectiveTags(IReadOnlyList<PlcTag> tags) =>
        [.. Resolve(tags).Where(p => p.CoveredBy is null).Select(p => p.Tag)];
}
