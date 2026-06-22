// 极空(JK)保护板 BLE 监控:数据通路 + JK02 协议解析
//
// 本程序做四件事:
//   1. 扫描附近 BLE 设备(高亮名称含 "JK" 的,方便识别极空保护板)
//   2. 连接选中的设备,枚举 GATT 服务/特征值并订阅通知
//   3. 把分包通知重组为 300 字节完整帧
//   4. 按帧类型解析:0x02 电芯信息 → 实时电池数据;0x03 设备信息 → 型号/固件/序列号;
//      结果打印到屏幕,并写入 logs\jkbms_YYYYMMDD.jsonl 供事后分析。其余帧(如 0x01)静默。
//
// 协议偏移由 ActiveLayout(当前 JK02_32S,适配 BD6A24S6PD / 固件 V20.08)决定,
// 换固件版本只改布局表,见 JkBmsProtocol.cs。
//
// 运行:见 README.md。Ctrl+C 退出。

using System.Collections.Concurrent;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace JkBmsMonitor;

internal static class Program
{
    // 扫描到的设备:蓝牙地址 -> 设备名(后到的完整名字会覆盖)
    private static readonly ConcurrentDictionary<ulong, string> _devices = new();

    // 设备首次被发现时分配的递增序号,用于按"发现先后"稳定排序(不随名字变化重排)
    private static int _discoveryCounter;
    private static readonly ConcurrentDictionary<ulong, int> _discoveryOrder = new();

    // BLE 通知分包重组器:把零散通知拼成完整 300 字节帧(程序内单实例,线程安全由锁保证)
    private static readonly FrameAssembler _assembler = new();
    private static readonly object _assembleLock = new();

    // 轮询节流状态(UTC):
    //   _lastCellInfoAt —— 上次收到 0x02 电芯帧的时间(由 HandleCompleteFrame 设置)。
    //   _deviceInfoSent —— 是否已发过一次 0x97(连接后仅发一次)。
    //   _lastCellQueryAt —— 上次发 0x96(请求电芯信息)的时间,用于 ~5s 周期重发直至收到 0x02。
    private static DateTime? _lastCellInfoAt;
    private static bool _deviceInfoSent;
    private static DateTime? _lastCellQueryAt;

    // JK02 协议命令字节(与 syssi/esphome-jk-bms 的 COMMAND_* 完全一致):
    //   0x96 = COMMAND_CELL_INFO,请求电芯信息帧(Frame 0x02,即实时电池数据)。
    //   0x97 = COMMAND_DEVICE_INFO,请求设备信息帧(Frame 0x03,型号/固件/密码等)。
    // 注:命令帧的字节 16 必须为 0(见 SendQueryAsync);若写非零计数器,设备会把 0x96 当写操作、回 0x01 而非 0x02。
    private const byte JkCmdCellInfo = 0x96;
    private const byte JkCmdDeviceInfo = 0x97;

    // 当前板子(BD6A24S6PD / 固件 V20.08)用 JK02_32S 布局。
    // ★拓展入口:未来解析 0x03 后,可按型号/固件版本把这里换成别的 JkFrameLayout。
    private static readonly JkFrameLayout ActiveLayout = JkFrameLayout.JK02_32S;

    // JSON Lines 日志(连接成功后创建,退出时释放);HandleCompleteFrame 回调里写数据
    private static JkBmsLogger? _logger;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("================================================");
        Console.WriteLine("   极空(JK)保护板 BLE 诊断工具");
        Console.WriteLine("================================================");
        Console.WriteLine();

        // 1. 扫描并选择设备
        ulong picked = await ScanAndPickDeviceAsync();
        if (picked == 0)
        {
            Console.WriteLine("未选择设备,退出。");
            return 0;
        }

        // 2. 连接并监听
        await ConnectAndListenAsync(picked);
        return 0;
    }

    /// <summary>
    /// 扫描 BLE 设备并让用户选一个。返回蓝牙地址,0 表示未选。
    /// </summary>
    private static async Task<ulong> ScanAndPickDeviceAsync()
    {
        // 默认扫描即可发现极空板(其广告包自带 "JK..." 名称)。
        var watcher = new BluetoothLEAdvertisementWatcher();
        // 主动扫描:主动向设备请求「扫描响应包」,完整名字(Complete Local Name)才在里面
        watcher.ScanningMode = BluetoothLEScanningMode.Active;
        watcher.Received += OnAdvertisementReceived;

        Console.WriteLine("正在扫描 BLE 设备(最多等 20 秒,或你直接输序号回车)...");
        Console.WriteLine("  * 名称含 JK 的会标记 <== JK,优先选它");
        Console.WriteLine("  * 没扫到的话:确认保护板已通电、蓝牙已开、离得近一点");
        Console.WriteLine();

        // 兼容性:无蓝牙硬件 / 蓝牙被禁用时,Start 会抛异常或立即 Abort
        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("✘ 无法启动蓝牙扫描:" + ex.Message);
            Console.WriteLine("  → 多半是没蓝牙或蓝牙没开。请检查:");
            Console.WriteLine("    1. 电脑有没有蓝牙(台式机通常要插 USB 蓝牙适配器)");
            Console.WriteLine("    2. 蓝牙是否已开启:设置 → 蓝牙和其他设备 → 打开蓝牙");
            Console.WriteLine("    3. 服务 'Bluetooth Support Service' 是否在运行");
            return 0;
        }

        // 无蓝牙硬件时 Start 可能不抛异常、但状态会立刻变 Aborted,等一下看状态
        await Task.Delay(400);
        if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
        {
            Console.WriteLine();
            Console.WriteLine("✘ 蓝牙无线电不可用,扫描被中止(状态:Aborted)。");
            Console.WriteLine("  → 请检查:");
            Console.WriteLine("    1. 电脑有没有蓝牙(台式机通常要插 USB 蓝牙适配器)");
            Console.WriteLine("    2. 蓝牙是否已开启:设置 → 蓝牙和其他设备 → 打开蓝牙");
            Console.WriteLine("    3. 服务 'Bluetooth Support Service' 是否在运行(运行框输 services.msc)");
            Console.WriteLine("  开启蓝牙后重新运行本程序即可。");
            return 0;
        }

        // 后台定期刷新设备列表
        var printCts = new CancellationTokenSource();
        string lastHash = string.Empty;
        _ = Task.Run(async () =>
        {
            while (!printCts.Token.IsCancellationRequested)
            {
                var list = OrderedDevices();
                if (list.Count == 0) { await Task.Delay(1500, printCts.Token); continue; }

                var lines = list.Select((kv, i) =>
                {
                    var isJk = kv.Value.Contains("JK", StringComparison.OrdinalIgnoreCase);
                    return $"  [{i}] {kv.Value,-30} {FormatAddress(kv.Key)}{(isJk ? "   <== JK" : "")}";
                });
                var hash = string.Join("|", lines);
                if (hash != lastHash)
                {
                    lastHash = hash;
                    Console.WriteLine("----------------- 发现的设备 -----------------");
                    foreach (var l in lines) Console.WriteLine(l);
                }
                await Task.Delay(1500, printCts.Token);
            }
        }, printCts.Token);

        // 等待用户输入或超时
        var inputTask = Task.Run(() => Console.ReadLine());
        var done = await Task.WhenAny(inputTask, Task.Delay(TimeSpan.FromSeconds(20)));
        printCts.Cancel();
        watcher.Stop();
        await Task.Delay(200); // 等后台打印线程退出,避免输出交错

        if (done != inputTask)
        {
            Console.WriteLine("扫描超时,未选择设备。");
            return 0;
        }

        string input = (await inputTask)?.Trim() ?? "";
        if (!int.TryParse(input, out int idx) || idx < 0) return 0;

        var ordered = OrderedDevices();
        if (idx >= ordered.Count) return 0;
        return ordered[idx].Key;
    }

    private static List<KeyValuePair<ulong, string>> OrderedDevices() =>
        _devices
            .OrderBy(kv => _discoveryOrder.TryGetValue(kv.Key, out int order) ? order : int.MaxValue)
            .ToList();

    private static void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // 首次发现该设备时分配一个递增序号,用于稳定列表顺序(按发现先后,不重排)
        _discoveryOrder.TryAdd(args.BluetoothAddress, Interlocked.Increment(ref _discoveryCounter));

        var raw = args.Advertisement.LocalName;

        // BLE 广告包的 LocalName 不稳定:
        //   1. 完整名字常在「扫描响应包」里,与主广告包异步到达,前几个事件经常是空的
        //   2. 同一设备的不同广告包轮流带/不带名字,空名不能覆盖已拿到的真名
        // 所以:有真名才更新;没真名且字典里还没有该设备,才占位标记。
        if (!string.IsNullOrWhiteSpace(raw))
            _devices[args.BluetoothAddress] = raw;
        else
            _devices.TryAdd(args.BluetoothAddress, "(未知名称)");
    }

    /// <summary>
    /// 连接设备,枚举 GATT 服务/特征值,订阅通知,持续打印原始数据。
    /// </summary>
    private static async Task ConnectAndListenAsync(ulong address)
    {
        Console.WriteLine();
        Console.WriteLine($"正在连接 {FormatAddress(address)} ...");

        BluetoothLEDevice device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device == null)
        {
            Console.WriteLine("连接失败:设备未找到或蓝牙未开启。");
            return;
        }

        // 连接状态变化日志(诊断断连时机用)
        device.ConnectionStatusChanged += (s, _) =>
            Console.WriteLine($"   [连接状态变化] {s.ConnectionStatus}");

        // ⚠️ FromBluetoothAddressAsync 只拿到设备句柄,不代表 GATT 链路已建立!
        // 真正的链路在第一次访问 GATT 服务时才握手。所以这里状态显示 Disconnected 是正常的。
        Console.WriteLine($"设备句柄已取得:{device.Name} (当前状态:{device.ConnectionStatus})");

        // 关键:不要配对!配对会让 Windows 后台独占 BLE 连接,导致极空 app 等其他程序连不上。
        // JK 板的数据通道无需配对即可 GATT 直连;若系统里残留旧配对,主动取消以释放占用。
        await EnsureUnpairedAsync(device);

        // 给链路一点时间稳定再开始枚举(太急着查服务可能 Unreachable)
        Console.WriteLine("等待链路稳定(2 秒)...");
        await Task.Delay(2000);

        // 关键坑:GattDeviceService / GattCharacteristic 必须保留引用,
        // 否则被 GC 回收后通知会静默断开。用这两个列表保活。
        var keepServices = new List<GattDeviceService>();
        var keepChars = new List<GattCharacteristic>();

        // 枚举服务:链路刚建立时可能 Unreachable,带重试 + 切换缓存模式(每次间隔 5 秒)
        GattDeviceServicesResult? servicesResult = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            // 前两次用 Uncached(强制重新发现),后面改 Cached(读 Windows 已缓存的服务表)
            var cacheMode = attempt <= 2 ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
            servicesResult = await device.GetGattServicesAsync(cacheMode);
            if (servicesResult.Status == GattCommunicationStatus.Success && servicesResult.Services.Count > 0)
                break;

            Console.WriteLine($"  第 {attempt} 次枚举服务:{servicesResult.Status}(服务数 {servicesResult.Services.Count}),5 秒后重试...");
            await Task.Delay(5000);
        }

        if (servicesResult == null || servicesResult.Status != GattCommunicationStatus.Success)
        {
            Console.WriteLine($"获取 GATT 服务失败:{servicesResult?.Status}");
            Console.WriteLine("排查清单:");
            Console.WriteLine("  1. 保护板正被手机 App / 其他程序连着?——断开它们(BLE 同一时刻只能一个主机连)");
            Console.WriteLine("  2. 离保护板近一点(BLE 广告能扫到 ≠ GATT 链路稳定)");
            Console.WriteLine("  3. 保护板是否已通电、蓝牙指示灯是否在闪");
            Console.WriteLine("  4. 仍不行:重启电脑蓝牙,或在系统蓝牙设置里删掉该设备后重新运行");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("================ GATT 服务 / 特征值清单 ================");
        foreach (var service in servicesResult.Services)
        {
            keepServices.Add(service);
            Console.WriteLine($"Service: {FormatUuid(service.Uuid)}");

            var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                Console.WriteLine("  (获取特征值失败)");
                continue;
            }

            foreach (var ch in charsResult.Characteristics)
            {
                var props = string.Join(" | ", ch.CharacteristicProperties.ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                Console.WriteLine($"  Characteristic: {FormatUuid(ch.Uuid)}   [{props}]");

                bool canNotify = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                              || ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate);
                if (!canNotify) continue;

                // WinRT BLE 已知坑:对某些特征值订阅时,WriteClientCharacteristicConfigurationDescriptorAsync
                // 会直接抛 COMException(而不是返回失败状态)。常见原因:特征值需要配对/加密、
                // 连接已断开、或该特征值虽标 Notify 但实际不支持 CCCD 写入。这里吞掉异常、跳过即可。
                GattCommunicationStatus status;
                try
                {
                    status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    -> 订阅异常:{ex.GetType().Name}  HRESULT=0x{ex.HResult:X}  {ex.Message}");
                    continue;
                }

                if (status == GattCommunicationStatus.Success)
                {
                    ch.ValueChanged += OnCharacteristicValueChanged;
                    keepChars.Add(ch);
                    Console.WriteLine("    -> 已订阅通知 ✔");
                }
                else
                {
                    Console.WriteLine($"    -> 订阅失败:{status}");
                }
            }
        }

        // 落盘日志:每帧电池数据 + 设备信息写一行 JSON 到 logs\jkbms_YYYYMMDD.jsonl
        _logger = new JkBmsLogger();

        Console.WriteLine();
        Console.WriteLine("================ 开始接收数据(Ctrl+C 退出)===============");
        Console.WriteLine("实时数据会同时打印到屏幕并写入 logs\\jkbms_YYYYMMDD.jsonl。");
        Console.WriteLine();

        var exit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };

        // JK 保护板连上后不会主动推送电芯数据,必须由主机发查询命令触发。
        // 命令必须带 CRC(字节 19 = sum8 0..18),否则设备丢弃、只回模块心跳 AT\r\n。
        if (keepChars.Count == 0)
        {
            Console.WriteLine("⚠ 没有任何可订阅的通道,无法发查询命令。");
        }
        else
        {
            // JK02 数据通道固定是 0xFFE1(服务 0xFFE0 下)。
            // 0xFFE1 上收到的 "AT\r\n" 是串口透传模块的心跳,不影响往它写 JK02 命令;
            // 真正的电池数据帧(头 55 AA EB 90)也从这个通道回。
            var cmdChar = keepChars.FirstOrDefault(c => FormatUuid(c.Uuid) == "0xFFE1");
            if (cmdChar == null)
            {
                var have = string.Join(", ", keepChars.Select(c => FormatUuid(c.Uuid)));
                Console.WriteLine($"⚠ 未找到 0xFFE1 数据通道,无法发查询命令(已订阅通道:{have})。");
            }
            else
            {
                // 握手顺序对齐权威实现 syssi/esphome-jk-bms:
                //   1) 连接建立后只发一次 0x97(→设备信息帧 0x03);
                //   2) 之后每 ~5s 发一次 0x96(→电芯信息帧 0x02),直到收到第一帧 0x02;
                //   3) 收到 0x02 后设备开始自动周期推送,主机即停止重发。
                // 关键:命令帧字节 16 必须为 0(见 SendQueryAsync),否则设备把 0x96 当写操作、回 0x01 而非 0x02。
                Console.WriteLine("已锁定数据通道 0xFFE1:先发 0x97 取设备信息,再每 ~5s 发 0x96 直到收到电芯数据...");
                _ = Task.Run(async () =>
                {
                    while (!exit.IsSet)
                    {
                        // 还没收到任何 0x02 电芯帧 → 按 ESPHome 节奏持续触发
                        if (!_lastCellInfoAt.HasValue)
                        {
                            if (!_deviceInfoSent)
                            {
                                await SendQueryAsync(cmdChar, JkCmdDeviceInfo); // 0x97 → 0x03,仅一次
                                _deviceInfoSent = true;
                            }
                            var now = DateTime.UtcNow;
                            bool cellQueryReady = !_lastCellQueryAt.HasValue
                                               || (now - _lastCellQueryAt.Value).TotalSeconds >= 5;
                            if (cellQueryReady)
                            {
                                await SendQueryAsync(cmdChar, JkCmdCellInfo); // 0x96 → 0x02
                                _lastCellQueryAt = now;
                            }
                        }
                        exit.Wait(1000); // 每秒检查,保证 ~5s 周期发 0x96 不漂移
                    }
                });
            }
        }

        exit.Wait();

        // 清理:释放日志、GATT 服务与设备对象,主动断开底层连接。
        // 否则 Windows 会维持连接数十秒,设备端也认为还连着,下次运行会被旧连接挤掉(反复断连)。
        _logger?.Dispose();
        Console.WriteLine("正在断开连接...");
        foreach (var s in keepServices) s.Dispose();
        device.Dispose();
        Console.WriteLine("已断开,再见。");
    }

    /// <summary>
    /// 如果设备在 Windows 里处于「已配对」状态,主动取消配对。
    /// 配对会让 Windows 后台维持 BLE 连接、独占设备,导致其他程序(如极空 app)连不上。
    /// JK 板的数据通道无需配对即可 GATT 直连,所以主动解除配对、把连接让出来。
    /// </summary>
    private static async Task EnsureUnpairedAsync(BluetoothLEDevice device)
    {
        var pairing = device.DeviceInformation.Pairing;
        if (!pairing.IsPaired)
        {
            Console.WriteLine("设备未配对 → 直连模式,不会占用系统连接。");
            return;
        }

        Console.WriteLine("检测到设备已配对 → 取消配对(否则 Windows 会独占连接,别的应用连不上)...");
        try
        {
            var result = await pairing.UnpairAsync();
            Console.WriteLine($"  取消配对:{result.Status}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  取消配对异常:{ex.GetType().Name}  HRESULT=0x{ex.HResult:X}  {ex.Message}");
        }

        await Task.Delay(1000);
    }

    /// <summary>
    /// 构造并发送一条 20 字节 JK02 查询命令帧,写到指定特征值。
    /// 帧结构严格对齐权威实现 syssi/esphome-jk-bms 的 build_frame():
    ///   AA 55 90 EB | cmd | len=0 | value(4 字节,0) | 其余全 0(含字节 16) | CRC。
    /// CRC = sum8(字节 0..18)。关键:字节 16 必须保持 0 —— ESPHome 从不写它;
    /// 若写成递增计数器,设备会把 0x96 当「写寄存器」并回 0x01 设置帧,而不是回 0x02 电芯信息。
    /// </summary>
    private static async Task SendQueryAsync(GattCharacteristic ch, byte command)
    {
        try
        {
            var frame = new byte[20];       // 默认全 0
            frame[0] = 0xAA;
            frame[1] = 0x55;
            frame[2] = 0x90;
            frame[3] = 0xEB;
            frame[4] = command;             // 0x96(→电芯信息 0x02) / 0x97(→设备信息 0x03)
            // frame[5] = 0x00;             // length:请求帧固定 0
            // frame[6..9] = 0x00000000;    // value:请求帧无值
            // frame[16] = 0x00;            // 计数器位置:必须保持 0(与 ESPHome 一致)
            frame[19] = JkCrc(frame, 19);   // CRC = sum8(字节 0..18)

            var writer = new DataWriter();
            writer.WriteBytes(frame);
            var status = await ch.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
            if (status != GattCommunicationStatus.Success)
                Console.WriteLine($"  [{FormatUuid(ch.Uuid)}] 发送 cmd=0x{command:X2} 失败:{status}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{FormatUuid(ch.Uuid)}] 发送异常:{ex.GetType().Name}  HRESULT=0x{ex.HResult:X}  {ex.Message}");
        }
    }

    /// <summary>JK02 帧校验:起始 length 个字节的累加和低 8 位(sum8)</summary>
    private static byte JkCrc(byte[] frame, int length)
    {
        int sum = 0;
        for (int i = 0; i < length; i++) sum += frame[i];
        return (byte)(sum & 0xFF);
    }

    private static void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var buffer = args.CharacteristicValue;
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);

        // 喂入重组器。BLE 通知可能从任意线程并发触发,这里串行化处理
        List<byte[]> frames;
        lock (_assembleLock)
            frames = _assembler.Feed(bytes).ToList();

        if (frames.Count == 0) return;

        foreach (var frame in frames) HandleCompleteFrame(frame);
    }

    /// <summary>处理一个重组完成的 300 字节帧:按类型分发——电芯信息(0x02)/设备信息(0x03)解析、打印并记日志;
    /// 其余帧(如 0x01 设置帧)在精简模式下静默忽略。</summary>
    private static void HandleCompleteFrame(byte[] frame)
    {
        string time = DateTime.Now.ToString("HH:mm:ss.fff");

        // ---- 0x02 电芯信息帧(实时电池数据)----
        if (JkBmsParser.IsCellInfoFrame(frame, out bool crcOk))
        {
            if (!crcOk)
            {
                Console.WriteLine($"[{time}] ⚠ 电芯信息帧 CRC 校验失败,丢掉。");
                return;
            }

            // 收到有效 0x02 → 记录时间,抑制后续握手补发,把通道让给设备自由周期推送
            _lastCellInfoAt = DateTime.UtcNow;
            var info = JkBmsParser.ParseCellInfo(frame, ActiveLayout);
            Console.WriteLine($"[{time}] ✅ 电芯数据");
            Console.WriteLine(info);
            _logger?.LogCellInfo(info);
            return;
        }

        // ---- 0x03 设备信息帧(型号/固件/序列号等)----
        if (JkBmsParser.IsDeviceInfoFrame(frame, out bool devCrcOk))
        {
            if (!devCrcOk)
            {
                Console.WriteLine($"[{time}] ⚠ 设备信息帧 CRC 校验失败,丢掉。");
                return;
            }
            var dev = JkBmsParser.ParseDeviceInfo(frame);
            Console.WriteLine($"[{time}] 📋 设备信息");
            Console.WriteLine(dev);
            _logger?.LogDeviceInfo(dev);
            return;
        }

        // ---- 其他帧(如 0x01 设置帧):精简模式静默 ----
    }

    private static string FormatAddress(ulong addr)
    {
        var bytes = BitConverter.GetBytes(addr);
        return string.Join(":", bytes.Take(6).Reverse().Select(b => b.ToString("X2")));
    }

    private static string FormatUuid(Guid uuid)
    {
        var s = uuid.ToString().ToUpperInvariant();
        // 标准 BLE 短 UUID(0000xxxx-0000-1000-8000-00805f9b34fb)显示成 0xXXXX
        const string tail = "-0000-1000-8000-00805F9B34FB";
        return s.EndsWith(tail, StringComparison.OrdinalIgnoreCase) ? "0x" + s.Substring(4, 4) : s;
    }

}
