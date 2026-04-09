using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace JT808.Server;

/// <summary>
/// 2019 版本设备记录信息
/// </summary>
public class Version2019DeviceInfo
{
    public string PhoneNumber { get; set; } = string.Empty;
    public byte ProtocolVersion { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public long MessageCount { get; set; }
    /// <summary>最后一次见到的消息ID (帮助调试)</summary>
    public ushort LastMessageId { get; set; }
    /// <summary>最后一次的远端 IP:Port (帮助定位真实设备)</summary>
    public string LastRemoteEndPoint { get; set; } = string.Empty;
}

/// <summary>
/// 2019 设备记录器
///
/// 本服务器只解析 JT/T 808-2013 协议. 当检测到终端发送 2019 版本数据时:
///   1. 不解析消息体 (避免解析错误)
///   2. 仅记录该终端的存在 (手机号 / 协议版本 / 首次/最后见到的时间 / 消息数 / 远端 IP)
///   3. 异步写盘到一个 INI 文件, 方便运维定位哪些终端需要升级到 2013 版本
///
/// 文件格式: INI, 每台设备一个 [section], 字段为 key=value
///
/// 高并发优化:
///   - lock-free 字典 (Record 调用零阻塞)
///   - 后台 worker 异步刷盘 (节流可配)
///   - dirty flag 跳过无变化的重复 flush
///   - 设备数硬上限 + LRU evict (按 LastSeen 最老剔除)
///   - 周期清理超过 TTL 的过期条目
///   - Dispose 等待 worker 退出 + 文件锁防写竞态
/// </summary>
public class Version2019Recorder : IDisposable
{
    private readonly string _filePath;
    private readonly int _maxDevices;
    private readonly int _staleDays;
    private readonly int _flushIntervalMs;

    private readonly ConcurrentDictionary<string, Version2019DeviceInfo> _devices = new();
    private readonly Channel<byte> _flushChannel;
    private readonly Task _flushTask;
    private readonly CancellationTokenSource _cts = new();
    // 文件写入互斥锁: 防止 worker 和 Dispose 同时写文件
    private readonly object _writeLock = new();
    // 标记自上次 flush 后是否有变化, 跳过无意义重复写盘
    private long _dirty;

    private static readonly Encoding Utf8WithBom = Encoding.UTF8;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="filePath">记录文件路径, 默认 Version2019Devices.ini</param>
    /// <param name="maxDevices">最大设备数硬上限, 0 = 无上限. 超过时按 LastSeen 最老的 evict</param>
    /// <param name="staleDays">过期天数, 0 = 不清理. 超过此天数未活动的条目会被清理</param>
    /// <param name="flushIntervalMs">刷盘节流间隔(毫秒). worker flush 后至少等这么久才允许下一次 flush</param>
    public Version2019Recorder(
        string filePath = "Version2019Devices.ini",
        int maxDevices = 50000,
        int staleDays = 30,
        int flushIntervalMs = 1000)
    {
        _filePath = filePath;
        _maxDevices = maxDevices;
        _staleDays = staleDays;
        _flushIntervalMs = Math.Max(100, flushIntervalMs); // 最少 100ms

        // 确保父目录存在
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 启动时若已存在历史文件, 加载已记录的设备 (恢复 FirstSeen 等)
        TryLoadExisting();

        // 唤醒通道 (有新数据时塞一个 byte, worker 拉取后批量刷盘)
        _flushChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _flushTask = Task.Run(FlushLoopAsync);
    }

    /// <summary>
    /// 记录一台 2019 终端 (lock-free, 调用方零阻塞)
    /// </summary>
    public void Record(string phoneNumber, byte protocolVersion, ushort messageId, string remoteEndPoint)
    {
        if (string.IsNullOrEmpty(phoneNumber)) return;

        var now = DateTime.Now;
        _devices.AddOrUpdate(
            phoneNumber,
            // 新设备
            _ => new Version2019DeviceInfo
            {
                PhoneNumber = phoneNumber,
                ProtocolVersion = protocolVersion,
                FirstSeen = now,
                LastSeen = now,
                MessageCount = 1,
                LastMessageId = messageId,
                LastRemoteEndPoint = remoteEndPoint,
            },
            // 已存在: 更新统计
            (_, existing) =>
            {
                existing.LastSeen = now;
                existing.MessageCount++;
                existing.LastMessageId = messageId;
                existing.LastRemoteEndPoint = remoteEndPoint;
                if (protocolVersion != 0) existing.ProtocolVersion = protocolVersion;
                return existing;
            });

        // 标脏 + 唤醒 worker
        Interlocked.Exchange(ref _dirty, 1);
        _flushChannel.Writer.TryWrite(0);
    }

    /// <summary>
    /// 当前已记录的 2019 设备数
    /// </summary>
    public int Count => _devices.Count;

    /// <summary>
    /// 后台刷盘 worker
    /// 收到通知 → 清理过期/超量 → flush (若 dirty) → 节流 sleep
    /// </summary>
    private async Task FlushLoopAsync()
    {
        var reader = _flushChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                // 排空 channel 中所有信号 (合并写)
                while (reader.TryRead(out _)) { }

                // 维护字典: 清理过期 + 限制大小
                MaintainDevices();

                // 仅在有变化时才 flush, 避免无意义重复写盘
                if (Interlocked.Exchange(ref _dirty, 0) == 1)
                {
                    FlushToFile();
                }

                // 节流
                try { await Task.Delay(_flushIntervalMs, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch
        {
            // 后台任务必须吞异常, 防止整个进程崩溃
        }
    }

    /// <summary>
    /// 维护字典: 清理过期条目 + 强制保持在 _maxDevices 以内
    /// </summary>
    private void MaintainDevices()
    {
        try
        {
            // 1. 清理超过 staleDays 天没活动的条目
            if (_staleDays > 0)
            {
                var threshold = DateTime.Now.AddDays(-_staleDays);
                foreach (var kv in _devices)
                {
                    if (kv.Value.LastSeen < threshold)
                    {
                        if (_devices.TryRemove(kv.Key, out _))
                        {
                            Interlocked.Exchange(ref _dirty, 1);
                        }
                    }
                }
            }

            // 2. 强制限制最大设备数: 超出时 evict LastSeen 最老的
            if (_maxDevices > 0 && _devices.Count > _maxDevices)
            {
                int overflow = _devices.Count - _maxDevices;
                var oldest = _devices.Values
                    .OrderBy(d => d.LastSeen)
                    .Take(overflow)
                    .Select(d => d.PhoneNumber)
                    .ToList();
                foreach (var phone in oldest)
                {
                    if (_devices.TryRemove(phone, out _))
                    {
                        Interlocked.Exchange(ref _dirty, 1);
                    }
                }
            }
        }
        catch
        {
            // 清理失败不阻塞主流程
        }
    }

    /// <summary>
    /// 同步刷盘 — 覆盖写整个 INI 文件 (持有 _writeLock)
    /// 每台设备一个 [phone] section, 字段为 key=value
    /// </summary>
    private void FlushToFile()
    {
        // 加锁防止 Dispose 和 worker 并发写同一文件
        lock (_writeLock)
        {
            try
            {
                var sb = new StringBuilder(4096);

                // 文件头注释
                sb.Append("; ============================================================\n");
                sb.Append("; 2019 版本设备记录\n");
                sb.Append("; 本服务器只解析 JT/T 808-2013 协议\n");
                sb.Append("; 检测到 2019 版本数据时, 仅记录设备元信息(不解析消息体)\n");
                sb.Append("; ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                  .Append("  共 ").Append(_devices.Count).Append(" 台设备\n");
                sb.Append("; ============================================================\n");
                sb.Append('\n');

                foreach (var d in _devices.Values.OrderBy(d => d.PhoneNumber, StringComparer.Ordinal))
                {
                    sb.Append('[').Append(d.PhoneNumber).Append(']').Append('\n');
                    sb.Append("ProtocolVersion=0x").Append(d.ProtocolVersion.ToString("X2")).Append('\n');
                    sb.Append("FirstSeen=").Append(d.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
                    sb.Append("LastSeen=").Append(d.LastSeen.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
                    sb.Append("MessageCount=").Append(d.MessageCount).Append('\n');
                    sb.Append("LastMessageId=0x").Append(d.LastMessageId.ToString("X4")).Append('\n');
                    sb.Append("LastRemoteEndPoint=").Append(d.LastRemoteEndPoint).Append('\n');
                    sb.Append('\n');
                }

                File.WriteAllText(_filePath, sb.ToString(), Utf8WithBom);
            }
            catch
            {
                // 单次写失败不影响下次, 静默吞掉
            }
        }
    }

    /// <summary>
    /// 程序启动时从已有 INI 文件加载历史记录 (FirstSeen 等)
    /// 简单 INI 解析: 跳过空行/注释 (; 或 #), [section] 开新设备, key=value 填字段
    /// </summary>
    private void TryLoadExisting()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var lines = File.ReadAllLines(_filePath, Utf8WithBom);
            Version2019DeviceInfo? current = null;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;

                // [section] = phone
                if (line[0] == '[' && line[^1] == ']')
                {
                    // 上一台设备 commit
                    if (current != null && !string.IsNullOrEmpty(current.PhoneNumber))
                    {
                        _devices[current.PhoneNumber] = current;
                    }
                    var phone = line.Substring(1, line.Length - 2).Trim();
                    current = new Version2019DeviceInfo { PhoneNumber = phone };
                    continue;
                }

                // key=value
                if (current == null) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();

                try
                {
                    switch (key)
                    {
                        case "ProtocolVersion":
                            current.ProtocolVersion = Convert.ToByte(value.Replace("0x", "").Replace("0X", ""), 16);
                            break;
                        case "FirstSeen":
                            current.FirstSeen = DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                            break;
                        case "LastSeen":
                            current.LastSeen = DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                            break;
                        case "MessageCount":
                            current.MessageCount = long.Parse(value, CultureInfo.InvariantCulture);
                            break;
                        case "LastMessageId":
                            current.LastMessageId = Convert.ToUInt16(value.Replace("0x", "").Replace("0X", ""), 16);
                            break;
                        case "LastRemoteEndPoint":
                            current.LastRemoteEndPoint = value;
                            break;
                    }
                }
                catch { /* 单字段解析失败跳过 */ }
            }

            // 文件结尾的最后一台设备 commit
            if (current != null && !string.IsNullOrEmpty(current.PhoneNumber))
            {
                _devices[current.PhoneNumber] = current;
            }
        }
        catch { /* 加载失败不阻断启动 */ }
    }

    /// <summary>
    /// 优雅停机:
    ///   1. 关闭 channel 让 worker 不再接受新唤醒
    ///   2. 取消 worker (打断 Task.Delay)
    ///   3. 等待 worker 退出 (最多 2 秒)
    ///   4. 最后再 flush 一次, 确保停机前的修改都落盘
    /// </summary>
    public void Dispose()
    {
        try
        {
            _flushChannel.Writer.TryComplete();
            _cts.Cancel();
            // 等 worker 退出, 防止 Dispose 和 worker 同时写文件
            // (即便不等, FlushToFile 内部的 _writeLock 也会保证序列化)
            try { _flushTask.Wait(TimeSpan.FromSeconds(2)); } catch { }

            // 强制最后刷一次盘 (无论 dirty 与否, 保证停机前的状态全部落盘)
            FlushToFile();
        }
        catch { }
        try { _cts.Dispose(); } catch { }
    }
}
