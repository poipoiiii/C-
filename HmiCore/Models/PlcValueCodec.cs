using System.Globalization;

namespace HmiCore.Models;

/// <summary>
/// 点位的数据类型：决定一个点位占几个寄存器、以及怎么把寄存器拼成一个数。
/// 现场 PLC 里 32/64 位量（浮点、长整型）是横跨连续几个 16 位寄存器的，
/// 所以"一个点位 = 一个寄存器"只在 16 位量上成立。
/// </summary>
public enum PlcDataType
{
    /// <summary>16 位无符号（1 个寄存器）</summary>
    UInt16,

    /// <summary>16 位有符号（1 个寄存器）；历史配置没有类型字段，缺省按它读，与老行为一致</summary>
    Int16,

    /// <summary>32 位无符号（2 个寄存器）</summary>
    UInt32,

    /// <summary>32 位有符号（2 个寄存器）</summary>
    Int32,

    /// <summary>32 位浮点（2 个寄存器）</summary>
    Float32,

    /// <summary>64 位浮点 double（4 个寄存器）</summary>
    Float64
}

/// <summary>
/// 多寄存器数值的字序：同样四个字节，设备之间放法不一样，选错了数值就是乱的。
/// 现场设备一般固定一种，所以按点位单独选（默认高字在前，最常见）。
/// 记号按 Modbus 惯例：以 A B C D 表示 32 位的四个字节（A 是第一个寄存器的高字节），
/// 64 位用 A B C D E F G H。
/// </summary>
public enum WordOrder
{
    /// <summary>32 位 ABCD / 64 位 ABCDEFGH：寄存器顺序就是高字在前</summary>
    HighWordFirst,

    /// <summary>32 位 CDAB / 64 位 GHEFCDAB：两个（多个）16 位字整体倒过来，小端设备常见</summary>
    LowWordFirst,

    /// <summary>32 位 BADC / 64 位 BADCFEHG：每个字内部的两个字节交换</summary>
    ByteSwapped,

    /// <summary>32 位 DCBA / 64 位 HGFEDCBA：所有字节整体倒序</summary>
    ReverseBytes
}

/// <summary>类型下拉项：值 + 中文说明（界面直接绑 <see cref="Display"/>）</summary>
public sealed record PlcDataTypeOption(PlcDataType Value, string Display);

/// <summary>字序下拉项：值 + 中文说明</summary>
public sealed record WordOrderOption(WordOrder Value, string Display);

/// <summary>
/// 寄存器 ↔ 数值的编解码：本项目所有"多个寄存器拼一个数"的规则都只在这里实现，
/// 采集、测试、仿真都用它，避免同一条规则抄成好几份。
/// </summary>
public static class PlcValueCodec
{
    /// <summary>这个类型占几个 16 位寄存器</summary>
    public static int RegisterCount(PlcDataType type) => type switch
    {
        PlcDataType.UInt16 or PlcDataType.Int16 => 1,
        PlcDataType.UInt32 or PlcDataType.Int32 or PlcDataType.Float32 => 2,
        PlcDataType.Float64 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知数据类型")
    };

    /// <summary>
    /// 把一段寄存器解码成原始值。16 位量直接用寄存器本身；32/64 位量先摊成字节流
    /// （每个寄存器高字节在前），再按字序重排，最后按大端拼起来。
    /// </summary>
    public static double Decode(ReadOnlySpan<ushort> registers, PlcDataType type, WordOrder order)
    {
        int count = RegisterCount(type);
        if (registers.Length < count)
        {
            throw new ArgumentException($"解码需要 {count} 个寄存器，只给了 {registers.Length} 个", nameof(registers));
        }

        if (count == 1)
        {
            return type == PlcDataType.UInt16 ? registers[0] : (short)registers[0];
        }

        Span<byte> bytes = stackalloc byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            bytes[i * 2] = (byte)(registers[i] >> 8);
            bytes[i * 2 + 1] = (byte)registers[i];
        }

        switch (order)
        {
            case WordOrder.LowWordFirst:
                SwapWords(bytes);
                break;
            case WordOrder.ByteSwapped:
                SwapBytesInsideWords(bytes);
                break;
            case WordOrder.ReverseBytes:
                bytes.Reverse();
                break;
        }

        ulong bits = 0;
        foreach (byte b in bytes)
        {
            bits = (bits << 8) | b;
        }

        return type switch
        {
            PlcDataType.UInt32 => bits,
            PlcDataType.Int32 => (int)(uint)bits,
            PlcDataType.Float32 => BitConverter.Int32BitsToSingle((int)(uint)bits),
            PlcDataType.Float64 => BitConverter.Int64BitsToDouble((long)bits),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知数据类型")
        };
    }

    /// <summary>
    /// 扫描预览：把本行这一个寄存器与紧跟它的下一个拼成 32 位浮点显示出来。
    /// 现场 PLC 把浮点写成两个连续寄存器，而扫描结果里只看得见一个个 16 位寄存器，
    /// 有了这一列，配上字序下拉就能一眼认出「哪两个寄存器是一个浮点、该用哪种字序」。
    /// </summary>
    public static string PreviewFloat32(ushort first, ushort second, WordOrder order) =>
        Decode([first, second], PlcDataType.Float32, order).ToString("G7", CultureInfo.InvariantCulture);

    /// <summary>把 16 位字整体倒过来（32 位两个、64 位四个）</summary>
    private static void SwapWords(Span<byte> bytes)
    {
        for (int left = 0, right = bytes.Length - 2; left < right; left += 2, right -= 2)
        {
            (bytes[left], bytes[right]) = (bytes[right], bytes[left]);
            (bytes[left + 1], bytes[right + 1]) = (bytes[right + 1], bytes[left + 1]);
        }
    }

    /// <summary>每个字内部的两个字节交换</summary>
    private static void SwapBytesInsideWords(Span<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i += 2)
        {
            (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        }
    }

    /// <summary>类型下拉项（点位表用，含 16 位）</summary>
    public static IReadOnlyList<PlcDataTypeOption> DataTypeOptions { get; } =
    [
        new(PlcDataType.Int16, "16 位有符号"),
        new(PlcDataType.UInt16, "16 位无符号"),
        new(PlcDataType.Int32, "32 位有符号"),
        new(PlcDataType.UInt32, "32 位无符号"),
        new(PlcDataType.Float32, "32 位浮点"),
        new(PlcDataType.Float64, "64 位浮点")
    ];

    /// <summary>合并下拉项：只列占多个寄存器的类型（合并成 16 位没有意义）</summary>
    public static IReadOnlyList<PlcDataTypeOption> MultiRegisterTypeOptions { get; } =
        [.. DataTypeOptions.Where(o => RegisterCount(o.Value) > 1)];

    /// <summary>字序下拉项</summary>
    public static IReadOnlyList<WordOrderOption> WordOrderOptions { get; } =
    [
        new(WordOrder.HighWordFirst, "高字在前 ABCD"),
        new(WordOrder.LowWordFirst, "低字在前 CDAB"),
        new(WordOrder.ByteSwapped, "字内字节交换 BADC"),
        new(WordOrder.ReverseBytes, "字节全反转 DCBA")
    ];

    /// <summary>类型的中文名，拼提示语用</summary>
    public static string Display(PlcDataType type) =>
        DataTypeOptions.FirstOrDefault(o => o.Value == type)?.Display ?? type.ToString();

    /// <summary>字序的中文名，拼提示语用</summary>
    public static string Display(WordOrder order) =>
        WordOrderOptions.FirstOrDefault(o => o.Value == order)?.Display ?? order.ToString();
}
