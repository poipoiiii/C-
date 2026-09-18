using HmiCore.Models;

namespace PlcTests;

/// <summary>
/// 寄存器 ↔ 数值的解码：32/64 位量横跨几个寄存器，字序选错数值就是乱的，
/// 所以把每种字序都用固定字节图案钉住（这些图案就是现场设备的实际放法）。
/// </summary>
public class PlcValueCodecTests
{
    [Theory]
    [InlineData(PlcDataType.UInt16, 1)]
    [InlineData(PlcDataType.Int16, 1)]
    [InlineData(PlcDataType.UInt32, 2)]
    [InlineData(PlcDataType.Int32, 2)]
    [InlineData(PlcDataType.Float32, 2)]
    [InlineData(PlcDataType.Float64, 4)]
    public void 各类型占几个寄存器(PlcDataType type, int expected)
    {
        Assert.Equal(expected, PlcValueCodec.RegisterCount(type));
    }

    [Fact]
    public void 十六位量_按有符号与无符号分别解()
    {
        ushort[] registers = [0xFFFF];

        Assert.Equal(-1d, PlcValueCodec.Decode(registers, PlcDataType.Int16, WordOrder.HighWordFirst));
        Assert.Equal(65535d, PlcValueCodec.Decode(registers, PlcDataType.UInt16, WordOrder.HighWordFirst));
    }

    [Fact]
    public void 十六位量_字序不影响结果()
    {
        ushort[] registers = [0x1234];         // 0x1234 = 4660

        Assert.Equal(4660d, PlcValueCodec.Decode(registers, PlcDataType.UInt16, WordOrder.ReverseBytes));
    }

    // 32 位 1.0f = 0x3F800000，四个字节记作 A=3F B=80 C=00 D=00
    [Theory]
    [InlineData(WordOrder.HighWordFirst, new ushort[] { 0x3F80, 0x0000 })]     // ABCD
    [InlineData(WordOrder.LowWordFirst, new ushort[] { 0x0000, 0x3F80 })]      // CDAB
    [InlineData(WordOrder.ByteSwapped, new ushort[] { 0x803F, 0x0000 })]       // BADC
    [InlineData(WordOrder.ReverseBytes, new ushort[] { 0x0000, 0x803F })]      // DCBA
    public void 三十二位浮点_四种字序都解出1_0(WordOrder order, ushort[] registers)
    {
        Assert.Equal(1.0, PlcValueCodec.Decode(registers, PlcDataType.Float32, order), 6);
    }

    [Fact]
    public void 三十二位浮点_负值()
    {
        // -2.5f = 0xC0200000
        Assert.Equal(-2.5, PlcValueCodec.Decode([0xC020, 0x0000], PlcDataType.Float32, WordOrder.HighWordFirst), 6);
    }

    [Fact]
    public void 三十二位整数_有符号与无符号()
    {
        // -2 的 32 位补码是 0xFFFFFFFE
        ushort[] registers = [0xFFFF, 0xFFFE];

        Assert.Equal(-2d, PlcValueCodec.Decode(registers, PlcDataType.Int32, WordOrder.HighWordFirst));
        Assert.Equal(4294967294d, PlcValueCodec.Decode(registers, PlcDataType.UInt32, WordOrder.HighWordFirst));
    }

    [Fact]
    public void 三十二位量_低字在前()
    {
        ushort[] registers = [0xFFFE, 0xFFFF];

        Assert.Equal(-2d, PlcValueCodec.Decode(registers, PlcDataType.Int32, WordOrder.LowWordFirst));
    }

    // 64 位 1.0 = 0x3FF0000000000000，八个字节记作 A=3F B=F0 C..H=00
    [Theory]
    [InlineData(WordOrder.HighWordFirst, new ushort[] { 0x3FF0, 0x0000, 0x0000, 0x0000 })]    // ABCDEFGH
    [InlineData(WordOrder.LowWordFirst, new ushort[] { 0x0000, 0x0000, 0x0000, 0x3FF0 })]     // GHEFCDAB
    [InlineData(WordOrder.ByteSwapped, new ushort[] { 0xF03F, 0x0000, 0x0000, 0x0000 })]      // BADCFEHG
    [InlineData(WordOrder.ReverseBytes, new ushort[] { 0x0000, 0x0000, 0x0000, 0xF03F })]     // HGFEDCBA
    public void 六十四位浮点_四种字序都解出1_0(WordOrder order, ushort[] registers)
    {
        Assert.Equal(1.0, PlcValueCodec.Decode(registers, PlcDataType.Float64, order), 6);
    }

    [Fact]
    public void 六十四位浮点_小数()
    {
        // 1234.5 = 0x40934A0000000000
        ushort[] registers = [0x4093, 0x4A00, 0x0000, 0x0000];

        Assert.Equal(1234.5, PlcValueCodec.Decode(registers, PlcDataType.Float64, WordOrder.HighWordFirst), 6);
    }

    [Fact]
    public void 寄存器个数不够_抛异常()
    {
        Assert.Throws<ArgumentException>(
            () => PlcValueCodec.Decode([0x3F80], PlcDataType.Float32, WordOrder.HighWordFirst));
    }
}
