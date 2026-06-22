# 极空(JK)保护板 BLE 数据读取 Demo

一个 Windows 控制台程序,通过蓝牙 BLE 连接极空(JK)保护板,读取并解析 JK02 协议帧,
把电池实时数据(总电压 / 电流 / SOC / 温度 / 各串电压内阻 / 压差 …)和设备信息
(型号 / 固件版本 / 序列号 …)打印到屏幕,同时落盘到 JSON Lines 日志,供事后分析。

协议解析逻辑参照权威实现 [syssi/esphome-jk-bms](https://github.com/syssi/esphome-jk-bms)
的 `decode_jk02_cell_info_()` / `decode_jk02_device_info_()`。

---

## 使用范围(必读)

> 本项目目前是**调试验证阶段的 Demo**,不是常驻采集服务。请按下列范围理解它的能力边界。

### 已验证可用

| 项目 | 值 |
| --- | --- |
| 保护板型号 | `JK_BD6A24S6PD`(24 串) |
| 硬件版本 | `V20H` |
| 固件版本 | `V20.08` |
| 协议布局 | `JK02_32S`(中后段偏移 +32,内阻基址 80) |

**只有上面这一个型号 + 固件组合经过了实机逐字段核对**(总压≈各串电压之和、SOC×标称容量=剩余容量 等均吻合)。

### 适用平台

- 仅支持 **Windows 10 / 11**(代码使用 WinRT `Windows.Devices.Bluetooth` 蓝牙 API,无法跨平台)。
- 必须有可用的蓝牙硬件(台式机通常需插 USB 蓝牙适配器),且运行时蓝牙保持开启。

### 暂不支持 / 未验证

- **其他型号或固件**:不同固件版本的电芯信息帧(0x02)字段偏移可能不同。
  若数值对不上,大概率是偏移差异,切到对应 `JkFrameLayout`(见 [扩展:支持新固件](#扩展支持新固件))。
- **常驻采集 / 自动重连**:目前是交互式一次性运行,BLE 偶发断流后需手动重跑。
- **Web 监控面板**:尚未实现。

---

## 运行环境要求

- Windows 10 1903(18362)及以上,推荐 Windows 11
- .NET 8 SDK(`net8.0-windows10.0.19041.0`)
- 可用的蓝牙无线电(蓝牙已开启,`Bluetooth Support Service` 服务在运行)

---

## 使用方式

### 1. 编译运行

```bash
cd JkBmsMonitor
dotnet run -c Release
```

> 如需直接用编译好的程序:发布后在 `bin/Release/net8.0-windows10.0.19041.0/` 下运行 `JkBmsMonitor.exe`。

### 2. 选择设备

程序启动后会自动扫描附近的 BLE 设备(最多等 20 秒),并把名称含 `JK` 的设备标记 `<== JK`:

```
----------------- 发现的设备 -----------------
  [0] JK_BD6A24S6PD                 AA:BB:CC:DD:EE:FF   <== JK
  [1] (未知名称)                     11:22:33:44:55:66
```

输入对应序号回车即可连接。

### 3. 观察输出

连接成功后,程序会:

1. 打印完整的 GATT 服务 / 特征值清单(拿到真实 UUID);
2. 自动锁定数据通道 `0xFFE1`:先发一次 `0x97` 取设备信息,再每 ~5 秒发一次 `0x96` 直到收到电芯数据;
3. 收到帧后重组、校验、解析,打印结构化数据块。

**设备信息帧(0x03):**

```
[14:32:05.102] 📋 设备信息
┌─────────────── 设备信息 ───────────────
│ 型号     : JK_BD6A24S6PD
│ 硬件版本 : V20H     固件版本 : V20.08
│ 设备名   : JK_BD6A24S6PD
│ 序列号   : 1234567890AB
│ 开机次数 : 42        运行时间 : 3天5时12分
└───────────────────────────────────────
```

**电芯信息帧(0x02):**

```
[14:32:07.218] ✅ 电芯数据
┌─────────────── 电池实时数据 ───────────────
│ 总电压 :   53.240 V        电流 :  -12.300 A
│ 功率   :  -655.35 W  (放电)
│ SOC    :       78 %        SOH  :      100 %
│ 容量   :   156.00/200 Ah   循环 : 18 次
│ 温度   : 探针1=25.4°C  探针2=26.1°C  MOS=32.7°C
│ MOS管  : 充电开  放电开  均衡停
├─────────────── 各串电压 / 内阻 ─────────────
│  串 1:  3.221 V    23 mΩ
│  串 2:  3.219 V    25 mΩ
│  ...
├─────────────────────────────────────────
│  活跃串数: 24   压差: 0.012 V
│  最高: 串3 (3.225 V)   最低: 串1 (3.213 V)
└───────────────────────────────────────────
```

### 4. 落盘日志

每帧电芯数据 / 每次设备信息各写一行 JSON 到:

```
<exe 同目录>/logs/jkbms_YYYYMMDD.jsonl
```

格式为 JSON Lines(每行一个对象),`type` 区分 `cellInfo`(实时数据)与 `deviceInfo`(设备信息),
`ts` 为 ISO 8601 带时区时间。追加写入,断开重连 / 多次运行不覆盖,同日累积到同一文件。
`AutoFlush` 开启,意外退出也不丢已写数据。

```json
{"type":"cellInfo","ts":"2026-06-21T14:32:07.218+08:00","totalVoltage":53.24,"current":-12.3,"power":-655.352,"soc":78,"soh":100,"capacityRemain":156.0,"nominalCapacity":200.0,"cycles":18,"runtimeSeconds":277920,"tempSensor1":25.4,"tempSensor2":26.1,"mosTemp":32.7,"chargingMosfet":true,"dischargingMosfet":true,"balancing":false,"cellVoltages":[3.221,3.219,...],"cellResistances":[0.023,0.025,...]}
```

### 5. 退出

按 `Ctrl + C` 退出。程序会主动释放日志、GATT 服务与设备对象,断开底层连接,
避免 Windows 残留连接挤掉下一次运行。

---

## 代码结构

工程为单项目 `JkBmsMonitor`,三个源文件各司其职:

```
JkBmsMonitor/
├── JkBmsMonitor.csproj   .NET 8 / WinRT 蓝牙工程配置
├── Program.cs            数据通路:扫描 → 连接 → 订阅 → 重组 → 分发
├── JkBmsProtocol.cs      JK02 协议:帧重组 + 解析 + 偏移表 + 数据模型
└── JkBmsLogger.cs        JSON Lines 落盘日志
```

### `Program.cs` —— BLE 数据通路与主流程

负责「把字节从空中搬到内存」这条链路:

- **扫描**:`BluetoothLEAdvertisementWatcher` 主动扫描,记录广告包名称(高亮含 `JK` 的设备),
  按「发现先后」稳定排序。
- **连接与枚举**:`FromBluetoothAddressAsync` 拿设备句柄 → **主动取消配对**(见下)→
  带 5 次重试 + 切换 `Uncached/Cached` 缓存模式枚举 GATT 服务/特征值。
- **订阅通知**:对所有 `Notify/Indicate` 特征值写 CCCD,用 `keepServices`/`keepChars` 两个列表
  **保活引用**(否则被 GC 回收后通知会静默断开)。
- **握手触发推送**:JK 板连上后不主动推数据,必须由主机发查询命令。
  锁定数据通道 `0xFFE1`,按 ESPHome 节奏:连接后仅发一次 `0x97`(→设备信息),之后每 ~5s 发一次
  `0x96`(→电芯信息),直到收到第一帧 `0x02`,设备开始自动周期推送,主机停止重发。
- **分发**:通知回调里用 `FrameAssembler` 重组,把完整 300 字节帧交给 `HandleCompleteFrame`,
  按类型(0x02/0x03)调用 `JkBmsParser`,解析结果打印并写日志;其余帧(如 0x01 设置帧)静默忽略。

> **关于配对**:代码 `EnsureUnpairedAsync` 会**主动取消** Windows 里该设备的配对。
> 因为配对会让 Windows 后台独占 BLE 连接,导致极空 app 等其他程序连不上;JK 板的数据通道
> 无需配对即可 GATT 直连。若系统里有残留配对,程序会自动解除。

### `JkBmsProtocol.cs` —— JK02 协议(独立、可复用)

协议层与蓝牙无关,可单独测试 / 复用:

- **`FrameAssembler`** —— BLE 通知分包重组器。持续喂入通知字节,以帧头 `55 AA EB 90` 定位,
  每凑满 300 字节吐出一帧;缓冲超过 400 字节仍未成帧则清空(防止对齐错乱时无限堆积)。
- **`JkBmsParser`** —— 帧解析。
  - `IsCellInfoFrame` / `IsDeviceInfoFrame`:按帧类型字节(`data[4]` == `0x02` / `0x03`)判别,并校验 CRC。
  - `ParseCellInfo` / `ParseDeviceInfo`:按偏移表提取结构化字段,小端多字节读取。
  - `Crc`:前 N 字节累加和取低 8 位(sum8),与权威实现一致。
- **`JkFrameLayout`** —— **数据驱动**的偏移表(record)。当前内置两套:
  - `JK02_24S`:老固件布局(中后段 off=0,内阻基址 64)
  - `JK02_32S`:本板子布局(BD6A24S6PD / V20.08,中后段 +32,内阻基址 80)
- **数据模型**:`JkCellInfo`(电芯信息:总电压/电流/功率/SOC/SOH/容量/循环/温度/MOS状态/各串电压内阻)
  和 `JkDeviceInfo`(型号/硬件版本/固件版本/设备名/序列号/密码/开机次数/运行时间),
  各自带格式化的 `ToString()`。

`Program.cs` 通过 `ActiveLayout = JkFrameLayout.JK02_32S` 指定当前活跃布局。

### `JkBmsLogger.cs` —— 落盘日志

把 `JkCellInfo` / `JkDeviceInfo` 序列化为 JSON 写入 `logs/jkbms_YYYYMMDD.jsonl`。
线程安全(用 `lock` 保护 `StreamWriter`,BLE 回调与主线程都可能写),
中文设备名不转义为 `\uXXXX` 保持可读,`AutoFlush` 保证落盘。实现 `IDisposable`。

---

## 扩展:支持新固件

不同固件版本的电芯信息帧(0x02)字段偏移不同,但**解析逻辑完全相同,只是偏移表不同**。
所以兼容新固件 = 加偏移表,解析代码零改动:

1. 在 `JkBmsProtocol.cs` 的 `JkFrameLayout` 里新增一个静态实例(对照 syssi 源码或抓包填偏移);
2. 在 `Program.cs` 把 `ActiveLayout` 换成新布局;
3. 进阶:解析 `0x03` 设备信息里的型号 / 固件版本,自动选择对应布局(代码里已预留 `ActiveLayout` 作为拓展入口)。

---

## 常见问题与排错

- **无法启动蓝牙扫描 / 状态 Aborted**:电脑无蓝牙硬件、蓝牙未开启,或 `Bluetooth Support Service` 未运行。
  运行框输 `services.msc` 确认服务状态。
- **枚举服务失败 / Unreachable**:
  1. 保护板正被手机 App 或其他程序连着?(BLE 同一时刻只能一个主机连,先断开它们)
  2. 离保护板近一点(广告能扫到 ≠ GATT 链路稳定);
  3. 保护板是否已通电、蓝牙指示灯是否在闪;
  4. 仍不行:重启电脑蓝牙,或在系统蓝牙设置里删掉该设备后重新运行。
- **设备名显示 "(未知名称)"**:BLE 广告包的 `LocalName` 不稳定(完整名字常在扫描响应包里,异步到达)。
  可接受,靠 MAC 地址选;或等几次扫描后真名会补上(代码不会用空名覆盖已拿到的真名)。
- **拿不到电芯数据(只看到 `AT\r\n` 心跳)**:`0xFFE1` 通道上的 `AT\r\n` 是串口透传模块的心跳,正常现象。
  真正的电池数据帧(头 `55 AA EB 90`)也从这个通道回;若始终没有,确认查询命令发出且 CRC 正确。
- **数值对不上(电压/电流/串数明显错误)**:多半是固件版本不同导致偏移错位。
  抓一行完整 300 字节帧 hex,对照 syssi 源码调整偏移,或新增一个 `JkFrameLayout`。

---

## 协议速查

| 项目 | 值 |
| --- | --- |
| 数据通道(服务 / 特征值) | `0xFFE0` / `0xFFE1` |
| 完整帧长度 | 300 字节 |
| 接收帧头 | `55 AA EB 90` |
| 发送命令帧头 | `AA 55 90 EB` |
| 帧类型字节 | `data[4]`:`0x02`=电芯信息,`0x03`=设备信息,`0x01`=设置帧 |
| CRC | 前 N 字节累加和取低 8 位(sum8) |
| 请求命令 | `0x96`=请求电芯信息,`0x97`=请求设备信息(20 字节,字节 16 必须为 0) |
| 多字节字段 | 小端序 |

---

## 参考与致谢

- 协议权威实现:[syssi/esphome-jk-bms](https://github.com/syssi/esphome-jk-bms)
  (`components/jk_bms_ble/jk_bms_ble.cpp` 的 `decode_jk02_cell_info_()` / `decode_jk02_device_info_()`)

## 许可证

详见根目录 [LICENSE](./LICENSE) 文件。
