using PlcDataCollector.Data;
using PlcDataCollector.Models;

namespace PlcDataCollector.Services;

/// <summary>
/// 采集 + 换算 + 存储
/// </summary>
public class DataCollectionService
{
    private readonly ModbusService _modbus;
    private readonly AppDbContext _db;
    private readonly ushort _address;
    private readonly double _scale;
    private readonly double _offset;

    public DataCollectionService(ModbusService modbus, AppDbContext db,
        ushort address, double scale, double offset)
    {
        _modbus = modbus;
        _db = db;
        _address = address;
        _scale = scale;
        _offset = offset;
    }

    public async Task CollectOnceAsync()
    {
        short raw = _modbus.ReadRegister(_address);
        double eng = raw * _scale + _offset;

        var record = new DataRecord
        {
            Timestamp = DateTime.Now,
            RegisterAddress = _address,
            RawValue = raw,
            EngineeringValue = eng
        };

        _db.DataRecords.Add(record);
        await _db.SaveChangesAsync();

        Console.WriteLine($"[{record.Timestamp:HH:mm:ss}] " +
            $"地址={_address} 原始={raw} 工程值={eng:F2}");
    }
}
