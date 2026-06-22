// JSON Lines 日志:把每帧实时数据 / 每次设备信息落盘,便于事后回看分析。
//
// 文件:exe 同目录 logs\jkbms_YYYYMMDD.jsonl,每行一个 JSON 对象(追加写,不覆盖)。
// 线程安全:BLE 回调与主线程都可能写,用 lock 保护 StreamWriter。
// 字段与控制台一致;time 字段为 ISO 8601(带时区)。实现 IDisposable。

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace JkBmsMonitor;

/// <summary>JSON Lines 日志:每帧电池数据 + 设备信息各写一行,追加到 logs\jkbms_YYYYMMDD.jsonl</summary>
public sealed class JkBmsLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        // 中文设备名等不转义为 \uXXXX,日志保持可读
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false, // 每行一个对象,紧凑
    };

    public JkBmsLogger()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"jkbms_{DateTime.Now:yyyyMMdd}.jsonl");
        // 追加写:断开重连 / 多次运行不覆盖,同日累积到同一文件
        _writer = new StreamWriter(file, append: true, new UTF8Encoding(false))
        {
            AutoFlush = true, // 写完立即落盘,意外退出不丢数据
        };
    }

    /// <summary>连接后记录一次设备信息</summary>
    public void LogDeviceInfo(JkDeviceInfo dev)
    {
        var obj = new
        {
            type = "deviceInfo",
            ts = DateTimeOffset.Now.ToString("o"),
            model = dev.Model,
            hardwareVersion = dev.HardwareVersion,
            softwareVersion = dev.SoftwareVersion,
            deviceName = dev.DeviceName,
            serialNumber = dev.SerialNumber,
            viewPasscode = dev.ViewPasscode,
            setupPasscode = dev.SetupPasscode,
            uptimeSeconds = dev.UptimeSeconds,
            powerOnCount = dev.PowerOnCount,
        };
        WriteLine(obj);
    }

    /// <summary>每帧记录一次实时电池数据</summary>
    public void LogCellInfo(JkCellInfo c)
    {
        var obj = new
        {
            type = "cellInfo",
            ts = DateTimeOffset.Now.ToString("o"),
            totalVoltage = c.TotalVoltage,
            current = c.Current,
            power = c.Power,
            soc = c.Soc,
            soh = c.Soh,
            capacityRemain = c.CapacityRemain,
            nominalCapacity = c.NominalCapacity,
            cycles = c.Cycles,
            runtimeSeconds = c.RuntimeSeconds,
            tempSensor1 = c.TempSensor1,
            tempSensor2 = c.TempSensor2,
            mosTemp = c.MosTemp,
            chargingMosfet = c.ChargingMosfet,
            dischargingMosfet = c.DischargingMosfet,
            balancing = c.Balancing,
            cellVoltages = c.CellVoltages,
            cellResistances = c.CellResistances,
        };
        WriteLine(obj);
    }

    private void WriteLine(object obj)
    {
        string json = JsonSerializer.Serialize(obj, _jsonOpts);
        lock (_lock)
        {
            _writer.WriteLine(json);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}
