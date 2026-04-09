# JT808-2013 车载终端通讯服务器

> 基于 JT/T 808-2013 协议标准的高并发 TCP 服务器
>
> 已完成高并发优化, 单实例支持 10000+ 车载终端在线
>
> **本服务器只解析 2013 协议**, 检测到 2019 数据时仅记录设备信息(不解析消息体)

---

## 🎯 核心特性

- **2013 协议解析** — 标准 JT/T 808-2013 字段长度 (6 字节手机号 / 5 字节制造商 ID / 20 字节型号 / 7 字节终端 ID)
- **2019 设备记录** — 检测到终端发送 2019 版本数据时, 仅记录该终端的存在 (手机号 / 协议版本 / 首次/最后见到的时间 / 消息数 / 远端 IP), 不解析消息体, 方便运维定位需要降级或处理的终端
- **高并发优化** — `SocketAsyncEventArgs` 池化 + `ArrayPool<byte>` 缓冲区, 热路径零分配
- **实时位置存储 (XML)** — 按手机号(12 位标准化)生成 XML 文件, **字段格式与 2019 服务器完全一致**, 上游下游对接无差别
- **位置数据归档** — 可选启用; 按 `yyyyMMdd` 文件夹归档每天的最新位置快照
- **粘包/半包处理** — 完善的消息缓冲区
- **会话管理** — 顶号上线/超时清理/原子时间戳, 全部 lock-free
- **多媒体上传** — 支持分包组装、漏包检测、断点重传
- **可配置启动** — 全部参数走 `appsettings.json`, 不需要改代码

---

## 📊 2013 vs 2019 协议字段对比

| 项目 | 2013 版本 | 2019 版本 |
|------|---------|---------|
| 手机号长度 | **6 字节** ✓ | 10 字节 |
| 制造商 ID | **5 字节** ✓ | 11 字节 |
| 终端型号 | **20 字节** ✓ | 30 字节 |
| 终端 ID | **7 字节** ✓ | 30 字节 |
| 鉴权扩展 | **仅鉴权码** ✓ | 鉴权码 + IMEI + 软件版本 |
| 参数 ID | **1 字节** ✓ | 4 字节 |
| 版本标识 | 无 (本服务器解析) ✓ | 消息体属性 bit14 (本服务器只记录) |
| 协议版本号 | **无** ✓ | 消息头新增 1 字节 |

✓ = 本服务器使用的字段长度

---

## 📁 项目结构

```
808ServerTo2013/
├── JT808.Protocol/                  # 协议层 (2013 字段长度)
│   ├── JT808Constants.cs            # 协议常量
│   ├── JT808Message.cs              # 消息数据结构
│   ├── JT808Decoder.cs              # 协议解码器
│   ├── JT808Encoder.cs              # 协议编码器 (默认 2013 格式)
│   └── JT808MessageBuffer.cs        # 消息缓冲区
│
├── JT808.Server/                    # 服务器层 (高并发优化版)
│   ├── JT808TcpServer.cs            # TCP 服务器主类
│   ├── SessionManager.cs            # 会话管理
│   ├── LocationDataStore.cs         # 实时位置 XML 存储 + 归档双写
│   ├── MediaDataStore.cs            # 多媒体数据 (分包重组)
│   ├── Version2019Recorder.cs       # 2019 设备记录器 (新增)
│   ├── ServerConfig.cs              # 配置数据类
│   ├── Program.cs                   # 启动程序
│   ├── appsettings.json             # 运行时配置
│   └── JT808.Server.csproj
│
├── JT808.TestClient/                # 单连接测试客户端
├── JT808.ConcurrencyTest/           # 并发压测工具
│
├── 808ServerTo2013.sln
├── build.sh
└── README.md
```

---

## 🚀 快速开始

### 环境要求

- **.NET 9.0 SDK** 或更高
- Linux / Windows / macOS

### 编译

```bash
cd /home/shenzheng/XDW/808ServerTo2013
export PATH="$HOME/.dotnet:$PATH"
./build.sh
# 或: dotnet build --configuration Release
```

### 启动服务器

```bash
cd JT808.Server
dotnet run
```

或后台运行:

```bash
nohup dotnet run --configuration Release > server.log 2>&1 &
```

服务器自动检测 stdin 是否被重定向, **后台模式**会每 10s 输出一次统计;
**交互模式**下按任意键查看在线终端列表, 按 `Q` 退出。

### 启动后会显示

```
============================================================
JT808-2013 车载终端通讯服务器
基于 JT/T 808-2013 协议
支持 12000+ 并发连接 (高并发优化版)
仅解析 2013, 检测到 2019 数据时仅记录设备信息
============================================================

当前配置:
  监听地址:       0.0.0.0:8808
  Listen Backlog: 4096
  最大并发连接:   12000
  位置目录:       LocationData
  位置归档:       LocationArchive/yyyyMMdd/
  媒体目录:       MediaData
  2019设备记录:   Version2019Devices.ini
  会话超时:       30 分钟
  日志级别:       Warning
```

### 启动测试客户端

```bash
cd JT808.TestClient
dotnet run
```

按提示选择: 服务器地址 / 端口 (默认 **8808**) / 手机号 / 协议版本 (选 **n=2013**)。

如果用 Y=2019 测试客户端连接, 服务器会**只记录设备信息**, 不解析消息体, 也不发 2019 格式应答。

---

## ⚙️ 配置说明 (appsettings.json)

```json
{
  "ServerConfig": {
    "IpAddress": "0.0.0.0",
    "Port": 8808,
    "Backlog": 4096,
    "MaxConcurrentConnections": 12000,
    "LocationDataDirectory": "LocationData",
    "LocationArchiveDirectory": "LocationArchive",
    "MediaDataDirectory": "MediaData",
    "Version2019RecordPath": "Version2019Devices.ini",
    "Version2019MaxDevices": 50000,
    "Version2019StaleDays": 30,
    "Version2019FlushIntervalMs": 1000,
    "SessionTimeoutMinutes": 30,
    "LogLevel": "Warning"
  }
}
```

| 字段 | 默认值 | 说明 |
|---|---|---|
| `IpAddress` | `0.0.0.0` | 监听地址, `0.0.0.0` 表示所有网卡 |
| `Port` | `8808` | TCP 监听端口 (区别 2019 服务器的 8809) |
| `Backlog` | `4096` | TCP listen 队列长度, 实际值受 `net.core.somaxconn` 限制 |
| `MaxConcurrentConnections` | `12000` | 应用层连接硬上限, 超限新连接被立即拒绝 |
| `LocationDataDirectory` | `LocationData` | 实时位置 XML 主存储目录, 文件名 = 12 位手机号.xml |
| `LocationArchiveDirectory` | `LocationArchive` | 归档目录; 留空 `""` 关闭归档双写 |
| `MediaDataDirectory` | `MediaData` | 多媒体文件存储目录 |
| **`Version2019RecordPath`** | **`Version2019Devices.ini`** | **2019 设备记录文件路径 (INI 格式)** |
| **`Version2019MaxDevices`** | **`50000`** | **2019 设备记录字典最大设备数, 满后按 LastSeen 最老的 LRU 剔除. `0`=无上限** |
| **`Version2019StaleDays`** | **`30`** | **2019 设备过期天数 (TTL), 超过此天数没活动的条目自动清理. `0`=不清理** |
| **`Version2019FlushIntervalMs`** | **`1000`** | **2019 设备记录刷盘节流间隔(毫秒), 调小更实时, 调大省 IO** |
| `SessionTimeoutMinutes` | `30` | 会话最长无活动时间 |
| `LogLevel` | `Warning` | 高并发场景推荐 `Warning`; 调试时改 `Debug` |

---

## 🆕 2019 设备记录

### 触发条件
当某个连接发送的消息**消息体属性 bit14 = 1** (2019 版本标志) 时, 该消息会被路由到 `Version2019Recorder` 而不是正常解析:
- 不解析消息体 (避免按 2013 字段错位读取垃圾数据)
- 不发送 2019 格式应答
- 仅记录该终端的存在和元信息

### 记录内容
INI 格式, 文件路径由 `Version2019RecordPath` 配置. 示例:

```ini
; ============================================================
; 2019 版本设备记录
; 本服务器只解析 JT/T 808-2013 协议
; 检测到 2019 版本数据时, 仅记录设备元信息(不解析消息体)
; 2026-04-08 18:36:30  共 2 台设备
; ============================================================

[013800138000]
ProtocolVersion=0x01
FirstSeen=2026-04-08 18:30:12
LastSeen=2026-04-08 18:35:47
MessageCount=125
LastMessageId=0x0200
LastRemoteEndPoint=172.16.1.10:54321

[014818411623]
ProtocolVersion=0x01
FirstSeen=2026-04-08 18:31:05
LastSeen=2026-04-08 18:36:02
MessageCount=98
LastMessageId=0x0102
LastRemoteEndPoint=172.16.1.20:54322
```

字段含义:
- `PhoneNumber` — 终端手机号 (BCD 解码后 ≤20 位)
- `ProtocolVersion` — 协议版本号 (来自 2019 消息头新增字段)
- `FirstSeen` — 首次见到该终端的本地时间
- `LastSeen` — 最后一次收到消息的本地时间
- `MessageCount` — 累计收到的 2019 消息数
- `LastMessageId` — 最后一次的消息 ID
- `LastRemoteEndPoint` — 最后一次的远端 `IP:Port`, 帮助定位真实设备

### 行为
- **去重**: 同一手机号只占一行, 多次出现仅更新 `LastSeen` / `MessageCount` 等字段
- **持久化**: 启动时若已有文件, 自动加载历史; 每秒最多刷盘一次 (合并写, 避免频繁 IO)
- **优雅停机**: 进程退出时最后刷盘一次, 保证数据不丢

---

## 📦 实时位置数据存储

### 主存储

- **路径**: `LocationDataDirectory/{12位手机号}.xml`
- **行为**: 始终保留每辆车**最新的一条**, 后到的覆盖前面的
- **写入方式**: 后台 worker 异步刷盘 (调用方零阻塞), 同手机号短时多次上报会**合并去重**
- **编码**: UTF-8 with BOM (与上游下游历史一致)

### 归档双写 (可选)

- **路径**: `LocationArchiveDirectory/{yyyyMMdd}/{12位手机号}.xml`
- **触发**: `LocationArchiveDirectory` 非空时启用
- **行为**: 每次主存储写入时, 同时写一份到当天的日期文件夹下
- **跨天**: 午夜后自动建新文件夹, 旧天文件夹保留作为历史归档
- **失败隔离**: 归档写失败不影响主存储, 反之亦然

### XML 字段格式

固定 40 个字段, **与 2019 服务器输出完全一致** (字段名/顺序/精度/编码/上游约定都不变):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<NewDataSet><table>
  <PhoneNumber>014818411623</PhoneNumber>
  <Time>2026-04-07 12:07:28</Time>
  <ACCZT>0</ACCZT>             <!-- bit0  -->
  <DingweiZT>1</DingweiZT>     <!-- bit1  -->
  <YunYinZT>0</YunYinZT>       <!-- bit4  -->
  <Longitude>22.177078</Longitude>   <!-- 注: 上游约定装纬度值 -->
  <Latitude>113.504936</Latitude>    <!-- 注: 上游约定装经度值 -->
  <GaoDu>54.000000</GaoDu>
  <Speed>0.0</Speed>
  <direction>2.36</direction>  <!-- 弧度 -->
  <mileage>707419.687500</mileage>  <!-- km, 来自附加信息 0x01 -->
  <ilLevel>0.000000</ilLevel>       <!-- L,  来自附加信息 0x02 -->
  <!-- 28 个报警字段 (JT808 报警标志位 0~14、18~30) -->
  <JinjiBJ>0</JinjiBJ> <ChaoSuBJ>0</ChaoSuBJ> ... <CFYJ>0</CFYJ>
</table></NewDataSet>
```

---

## ⚡ 高并发优化要点

| 优化项 | 实现 |
|---|---|
| **接收 SAEA 复用** | 每个 session 持有一份 `SocketAsyncEventArgs` + `ArrayPool<byte>` 缓冲区, 整个连接生命周期复用 |
| **零分配热路径** | `JT808MessageBuffer.Append(byte[], offset, count)` 直接消费接收 buffer 片段, 不再 `new byte[]` |
| **同步内联处理** | 接收回调内同线程直接 `ProcessMessage`, 不走 `Task.Run`, 避免 ThreadPool 抖动 |
| **per-session 发送锁** | 同一 socket 的并发 Send 串行化, 防止数据错乱 |
| **同步发送 + 5s 超时** | JT808 应答包小, sync `Send` 内核 buffer 立即吸收; 慢客户端由 `SendTimeout` 兜底 |
| **位置数据异步通道** | `Channel<string>` + 后台 worker, 同车多次上报去重合并, 调用方零等待 |
| **原子时间戳** | `LastActiveTicks` 用 `Interlocked` 读写, 替代 `DateTime.Now` |
| **PeriodicTimer 清理** | 每 60s 清理超时会话, 不占工作线程 |
| **连接数硬上限** | `MaxConcurrentConnections` 超限直接关闭新连接 |
| **Socket 调优** | `NoDelay = true` (关闭 Nagle), `KeepAlive = true`, `SendTimeout = 5000` |
| **2019 数据零成本路由** | 解码头时即识别 bit14, 直接调记录器, 不浪费 CPU 解析陌生格式 |

---

## 🐧 系统配置建议 (Linux 1 万终端)

```bash
# 进程级 fd 上限
ulimit -n 65536

# 内核 listen 队列
sysctl -w net.core.somaxconn=8192
sysctl -w net.ipv4.tcp_max_syn_backlog=8192

# 持久化: 写入 /etc/sysctl.conf
echo "net.core.somaxconn=8192" >> /etc/sysctl.conf
echo "net.ipv4.tcp_max_syn_backlog=8192" >> /etc/sysctl.conf

# systemd unit 中设置 LimitNOFILE=65536
```

---

## 📝 已实现消息

### 终端通用
- ✅ `0x0001` 终端通用应答
- ✅ `0x8001` 平台通用应答
- ✅ `0x0002` 终端心跳
- ✅ `0x0100` 终端注册 (2013 字段长度)
- ✅ `0x8100` 终端注册应答
- ✅ `0x0102` 终端鉴权 (2013 仅鉴权码)

### 位置信息
- ✅ `0x0200` 位置信息汇报 (含附加信息解析 + XML 落盘)
- ⏳ `0x0704` 定位数据批量上传 (已收应答, 待解析批量数据)

### 多媒体
- ✅ `0x0801` 多媒体数据上传 (分包重组 + 漏包检测 + 断点重传)
- ✅ `0x8800` 多媒体数据上传应答 (重传请求)

### 2019 数据 (仅记录)
- ⚠ 检测到任何 bit14=1 的消息 → 记录到 `Version2019Devices.ini`, 不解析

---

## 🔍 调试和监控

### 在线统计 (交互模式)

启动后按任意键, 显示:
- 在线终端数 / 已鉴权数
- **已记录的 2019 终端数**
- 终端列表: 手机号 / 鉴权 / 收发计数 / 车牌 / 最后活跃

### 后台模式

`stdin` 被重定向 (systemd / docker / nohup) 时自动进入后台模式, 每 10s 输出一行统计。

### 周期统计日志

后台 cleanup worker 每 60 秒输出一行:

```
[Stats] 在线=1234 清理超时=2 待写位置=15 2019终端=8
```

### 调高日志级别

排查问题时把 `appsettings.json` 的 `LogLevel` 改成 `Debug` 即可看到每条消息细节; 默认 `Warning` 是为了高并发下不被日志拖累。

---

## 🔗 与 2019 服务器的关系

本项目从 [`JT808Server2019`](../JT808Server2019/) 派生, 完全独立, 可以同时部署:

| 项 | 2013 服务器 (本项目) | 2019 服务器 |
|---|---|---|
| 端口 | **8808** | 8809 |
| 协议解析 | JT/T 808-2013 | 自动识别 2013/2019 |
| 2019 数据 | 仅记录设备信息 | 完整解析处理 |
| XML 输出格式 | **完全一致** | 完全一致 |
| LocationData 目录 | 默认 `LocationData/` | 默认 `LocationData/` |

> 同台机器同时运行两个服务器时, 建议为各自配置不同的 `LocationDataDirectory` 避免文件冲突。

---

## 📚 参考资料

- **JT/T 808-2013** 道路运输车辆卫星定位系统终端通讯协议及数据格式
- **JT/T 808-2019** 道路运输车辆卫星定位系统终端通讯协议及数据格式 (修订版, 仅用于检测识别)
- **GB/T 19056** 汽车行驶记录仪

---

## 📄 许可证

MIT License

---

**项目就绪, 单机 1 万终端可上线!** 🚀
