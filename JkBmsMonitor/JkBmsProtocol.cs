// 极空(JK)保护板 JK02 协议解析
//
// 协议参考(权威实现): https://github.com/syssi/esphome-jk-bms
//   对应文件 components/jk_bms_ble/jk_bms_ble.cpp 的 decode_jk02_cell_info_() / decode_jk02_device_info_()
//
// 本文件做四件事:
//   1. FrameAssembler —— 把 BLE 分包通知重组为完整帧(每帧 300 字节)
//   2. 校验帧头 + CRC(累加和)
//   3. JkBmsParser —— 从电芯信息帧(0x02)/设备信息帧(0x03)提取结构化字段
//   4. JkFrameLayout —— 偏移表(数据驱动):用 JkFrameLayout.JK02_32S / JK02_24S 选布局;
//      未来兼容新固件 = 只需新增一个 JkFrameLayout 静态实例,解析逻辑零改动。
//
// 多字节字段均为小端序。当前活跃布局见 Program.cs 的 ActiveLayout。

using System.Text;

namespace JkBmsMonitor;

/// <summary>一帧"电芯信息"的解析结果</summary>
public sealed class JkCellInfo
{
    public double TotalVoltage;      // 总电压 V
    public double Current;           // 电流 A (正=充电, 负=放电)
    public double Power;             // 功率 W (= 总电压 × 电流)
    public double TempSensor1;       // 温度探头 1 °C
    public double TempSensor2;       // 温度探头 2 °C
    public double MosTemp;           // MOS 管温度 °C
    public int Soc;                  // 剩余电量 % (State of Charge)
    public int Soh;                  // 电池健康度 % (State of Health)
    public double CapacityRemain;    // 剩余容量 Ah
    public double NominalCapacity;   // 标称容量 Ah
    public uint Cycles;              // 充放电循环次数
    public uint RuntimeSeconds;      // 累计运行时间 s
    public bool ChargingMosfet;      // 充电 MOS 是否开启
    public bool DischargingMosfet;   // 放电 MOS 是否开启
    public bool Balancing;           // 是否正在均衡
    public double[] CellVoltages = new double[24];     // 各串电压 V (0 表示该串未用)
    public double[] CellResistances = new double[24];  // 各串内阻 Ω

    public override string ToString()
    {
        var s = new StringBuilder();
        s.AppendLine("┌─────────────── 电池实时数据 ───────────────");
        s.AppendLine($"│ 总电压 : {TotalVoltage,8:F3} V        电流 : {Current,8:F3} A");
        s.AppendLine($"│ 功率   : {Power,8:F2} W  ({(Current >= 0 ? "充电" : "放电")})");
        s.AppendLine($"│ SOC    : {Soc,8} %        SOH  : {Soh,8} %");
        s.AppendLine($"│ 容量   : {CapacityRemain,8:F2}/{NominalCapacity:F0} Ah   循环 : {Cycles} 次");
        s.AppendLine($"│ 温度   : 探针1={TempSensor1:F1}°C  探针2={TempSensor2:F1}°C  MOS={MosTemp:F1}°C");
        s.AppendLine($"│ MOS管  : 充电{(ChargingMosfet ? "开" : "关")}  放电{(DischargingMosfet ? "开" : "关")}  均衡{(Balancing ? "中" : "停")}");
        s.AppendLine("├─────────────── 各串电压 / 内阻 ─────────────");
        int activeCount = 0;
        double minV = double.MaxValue, maxV = double.MinValue;
        int minIdx = 0, maxIdx = 0;
        for (int i = 0; i < CellVoltages.Length; i++)
        {
            double v = CellVoltages[i];
            if (v <= 0) continue;
            activeCount++;
            if (v < minV) { minV = v; minIdx = i + 1; }
            if (v > maxV) { maxV = v; maxIdx = i + 1; }
            s.AppendLine($"│  串{i + 1,2}: {v,6:F3} V   {CellResistances[i] * 1000,5:F0} mΩ");
        }
        if (activeCount > 0)
        {
            s.AppendLine("├─────────────────────────────────────────");
            s.AppendLine($"│  活跃串数: {activeCount}   压差: {maxV - minV:F3} V");
            s.AppendLine($"│  最高: 串{maxIdx} ({maxV:F3} V)   最低: 串{minIdx} ({minV:F3} V)");
        }
        s.Append("└───────────────────────────────────────────");
        return s.ToString();
    }
}

/// <summary>JK02 电芯信息帧(0x02)的字段偏移表(数据驱动)。
/// 不同固件版本只是偏移不同:老 JK02_24S 中后段 off=0、内阻基址 64;
/// 本板子(BD6A24S6PD / 固件 V20.08)走 JK02_32S,中后段 +32、内阻基址 80。
/// ★拓展入口:未来加新固件 = 在这里加一个静态实例,ParseCellInfo 零改动。</summary>
public sealed record JkFrameLayout(
    string Name,
    int CellVoltageBase,    // 各串电压起始偏移(24S/32S 均为 6)
    int CellResistanceBase, // 各串内阻起始偏移(24S=64, 32S=80)
    int TotalVoltage,
    int Current,
    int TempSensor1,
    int TempSensor2,
    int MosTemp,
    int Soc,
    int CapacityRemain,
    int NominalCapacity,
    int Cycles,
    int Soh,
    int RuntimeSeconds,
    int Balancing,
    int ChargingMosfet,
    int DischargingMosfet)
{
    /// <summary>老固件布局(≤24 串):中后段 off=0,内阻基址 64</summary>
    public static readonly JkFrameLayout JK02_24S = new(
        "JK02_24S", 6, 64, 118, 126, 130, 132, 134, 141, 142, 146, 150, 158, 162, 140, 166, 167);

    /// <summary>本板子布局(BD6A24S6PD / 固件 V20.08):中后段 +32,内阻基址 80。已实机逐字段核对</summary>
    public static readonly JkFrameLayout JK02_32S = new(
        "JK02_32S", 6, 80, 150, 158, 162, 164, 144, 173, 174, 178, 182, 190, 194, 172, 198, 199);
}

/// <summary>设备信息帧(0x03)的解析结果。0x03 帧偏移对所有 JK02 设备固定,不随 24S/32S 变化</summary>
public sealed class JkDeviceInfo
{
    public string Model = string.Empty;           // 型号, 如 JK_BD6A24S6PD
    public string HardwareVersion = string.Empty; // 硬件版本, 如 V20H
    public string SoftwareVersion = string.Empty; // 固件版本, 如 V20.08
    public string DeviceName = string.Empty;      // 设备名(蓝牙广播名)
    public string ViewPasscode = string.Empty;    // 查看密码
    public string SerialNumber = string.Empty;    // 序列号
    public string SetupPasscode = string.Empty;   // 管理密码
    public uint UptimeSeconds;                    // 开机后累计运行秒数
    public uint PowerOnCount;                     // 累计开机次数

    public override string ToString()
    {
        var s = new StringBuilder();
        s.AppendLine("┌─────────────── 设备信息 ───────────────");
        s.AppendLine($"│ 型号     : {Model}");
        s.AppendLine($"│ 硬件版本 : {HardwareVersion,-10}  固件版本 : {SoftwareVersion}");
        s.AppendLine($"│ 设备名   : {DeviceName}");
        s.AppendLine($"│ 序列号   : {SerialNumber}");
        s.AppendLine($"│ 开机次数 : {PowerOnCount,-10}  运行时间 : {FormatUptime(UptimeSeconds)}");
        s.Append("└───────────────────────────────────────");
        return s.ToString();
    }

    private static string FormatUptime(uint sec)
    {
        uint d = sec / 86400; sec %= 86400;
        uint h = sec / 3600; sec %= 3600;
        uint m = sec / 60;
        return $"{d}天{h}时{m}分";
    }
}

/// <summary>BLE 通知分包重组器:持续喂入通知字节,吐出完整的 300 字节帧</summary>
public sealed class FrameAssembler
{
    private const int FrameSize = 300;        // JK02 完整帧固定 300 字节
    private const int MaxBufferSize = 400;    // 超过此长度仍未成帧,说明对齐乱了,清空
    private readonly List<byte> _buffer = new();

    /// <summary>喂入一批 BLE 通知数据,返回 0 个或多个完整帧</summary>
    public IEnumerable<byte[]> Feed(byte[] chunk)
    {
        _buffer.AddRange(chunk);

        var frames = new List<byte[]>();
        while (true)
        {
            int idx = FindPreamble(_buffer);
            if (idx < 0)
            {
                // 没找到帧头:保留末尾 3 字节(可能是帧头前 3 字节),其余丢弃
                if (_buffer.Count > 3) _buffer.RemoveRange(0, _buffer.Count - 3);
                break;
            }
            if (idx > 0) _buffer.RemoveRange(0, idx); // 丢弃帧头前的垃圾字节

            if (_buffer.Count < FrameSize) break;     // 还没收满一帧

            var frame = new byte[FrameSize];
            _buffer.CopyTo(0, frame, 0, FrameSize);
            _buffer.RemoveRange(0, FrameSize);
            frames.Add(frame);
        }

        if (_buffer.Count > MaxBufferSize) _buffer.Clear();
        return frames;
    }

    private static int FindPreamble(List<byte> b)
    {
        for (int i = 0; i <= b.Count - 4; i++)
            if (b[i] == 0x55 && b[i + 1] == 0xAA && b[i + 2] == 0xEB && b[i + 3] == 0x90)
                return i;
        return -1;
    }
}

/// <summary>JK02 帧解析</summary>
public static class JkBmsParser
{
    // ---- 小端读取(与 syssi 的 jk_get_16bit/32bit 一致) ----
    private static ushort U16(byte[] d, int i) => (ushort)(d[i] | (d[i + 1] << 8));
    private static uint U32(byte[] d, int i) => (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24));
    private static short I16(byte[] d, int i) => (short)U16(d, i);
    private static int I32(byte[] d, int i) => (int)U32(d, i);

    /// <summary>CRC:前 len 字节的累加和,取低 8 位</summary>
    public static byte Crc(byte[] data, int len)
    {
        int sum = 0;
        for (int i = 0; i < len; i++) sum += data[i];
        return (byte)(sum & 0xFF);
    }

    /// <summary>判断是否电芯信息帧(data[4]==0x02),并校验 CRC</summary>
    public static bool IsCellInfoFrame(byte[] frame, out bool crcOk)
    {
        crcOk = false;
        if (frame.Length < FrameConstants.FrameSize) return false;
        crcOk = Crc(frame, FrameConstants.FrameSize - 1) == frame[FrameConstants.FrameSize - 1];
        return frame[4] == 0x02;
    }

    /// <summary>从电芯信息帧(0x02)解析出结构化数据。偏移由 layout 决定:
    /// 本板子传 JkFrameLayout.JK02_32S(已实机逐字段核对:总压≈各串和、SOC×标称=剩余)。</summary>
    public static JkCellInfo ParseCellInfo(byte[] d, JkFrameLayout layout)
    {
        var info = new JkCellInfo
        {
            TotalVoltage = U32(d, layout.TotalVoltage) * 0.001,
            Current = I32(d, layout.Current) * 0.001,        // 有符号:负=放电, 正=充电
            TempSensor1 = I16(d, layout.TempSensor1) * 0.1,  // 探头1若未接会返回异常负值,如 -200°C
            TempSensor2 = I16(d, layout.TempSensor2) * 0.1,
            MosTemp = I16(d, layout.MosTemp) * 0.1,
            Soc = d[layout.Soc],
            CapacityRemain = U32(d, layout.CapacityRemain) * 0.001,
            NominalCapacity = U32(d, layout.NominalCapacity) * 0.001,
            Cycles = U32(d, layout.Cycles),
            Soh = d[layout.Soh],
            RuntimeSeconds = U32(d, layout.RuntimeSeconds),
            Balancing = d[layout.Balancing] != 0x00,
            ChargingMosfet = d[layout.ChargingMosfet] != 0x00,
            DischargingMosfet = d[layout.DischargingMosfet] != 0x00,
        };
        info.Power = info.TotalVoltage * info.Current;

        // ---- 各串电压 + 内阻,每串 2 字节小端,0 表示该串未用 ----
        for (int i = 0; i < info.CellVoltages.Length; i++)
        {
            info.CellVoltages[i] = U16(d, layout.CellVoltageBase + i * 2) * 0.001;
            info.CellResistances[i] = U16(d, layout.CellResistanceBase + i * 2) * 0.001;
        }
        return info;
    }

    /// <summary>判断是否设备信息帧(data[4]==0x03),并校验 CRC</summary>
    public static bool IsDeviceInfoFrame(byte[] frame, out bool crcOk)
    {
        crcOk = false;
        if (frame.Length < FrameConstants.FrameSize) return false;
        crcOk = Crc(frame, FrameConstants.FrameSize - 1) == frame[FrameConstants.FrameSize - 1];
        return frame[4] == 0x03;
    }

    /// <summary>从设备信息帧(0x03)解析出型号/版本/序列号等。
    /// 0x03 帧字段偏移对所有 JK02 设备固定,不随 24S/32S 变化;
    /// ASCII 字段读到 \0 或字段宽度上限截断。为"未来按型号/版本选布局"提供数据基础。</summary>
    public static JkDeviceInfo ParseDeviceInfo(byte[] d)
    {
        return new JkDeviceInfo
        {
            Model = Ascii(d, 6, 16),
            HardwareVersion = Ascii(d, 22, 8),
            SoftwareVersion = Ascii(d, 30, 8),
            UptimeSeconds = U32(d, 38),
            PowerOnCount = U32(d, 42),
            DeviceName = Ascii(d, 46, 16),
            ViewPasscode = Ascii(d, 62, 24),
            SerialNumber = Ascii(d, 86, 32),
            SetupPasscode = Ascii(d, 118, 16),
        };
    }

    /// <summary>从 start 起读 ASCII,遇 \0 或读满 maxLen 字节停止,去掉尾部空白</summary>
    private static string Ascii(byte[] d, int start, int maxLen)
    {
        int end = start;
        while (end < start + maxLen && end < d.Length && d[end] != 0) end++;
        return Encoding.ASCII.GetString(d, start, end - start).TrimEnd();
    }

    /// <summary>构建"请求电芯信息"命令帧(20 字节),连接后写给 write 特征值可触发主动推送</summary>
    public static byte[] BuildCellInfoRequest()
    {
        var frame = new byte[20];
        frame[0] = 0xAA;
        frame[1] = 0x55;
        frame[2] = 0x90;
        frame[3] = 0xEB;
        frame[4] = 0x96; // COMMAND_CELL_INFO
        frame[5] = 0x00; // length
        // frame[6..9] = value = 0
        frame[19] = Crc(frame, 19);
        return frame;
    }
}

/// <summary>常量集中处</summary>
public static class FrameConstants
{
    public const int FrameSize = 300;
}
