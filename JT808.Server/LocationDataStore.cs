using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using JT808.Protocol;

namespace JT808.Server;

/// <summary>
/// 位置数据存储器 — 高并发优化版
///   - 接收侧无锁: 直接覆盖 ConcurrentDictionary
///   - 异步刷盘: 后台 worker 拉取最新快照写入文件
///   - 去重合并: 同一手机号在一个写周期内多次上报只写一次最新值
///   - XML 格式: 与上游平台标准格式完全一致
/// </summary>
public class LocationDataStore : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly string? _archiveDirectory;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    // 与原版保持一致: 使用带 BOM 的 UTF-8 (Encoding.UTF8 默认行为)
    private static readonly Encoding Utf8WithBom = Encoding.UTF8;

    // 归档目录: worker 单线程访问, 用普通字段缓存"最近确认存在的日期文件夹", 避免每次写都做目录检查
    private string _lastEnsuredArchiveDate = "";

    // 待写入快照: phone -> 最新 XML 内容 (后到的覆盖前面的)
    private readonly ConcurrentDictionary<string, string> _pending = new();

    // 通知通道: 仅用于唤醒 worker, 不携带数据 (数据从 _pending 拉)
    private readonly Channel<string> _notifyChannel;

    // 后台 worker
    private readonly Task _worker;
    private readonly CancellationTokenSource _cts = new();

    public LocationDataStore(string dataDirectory = "LocationData", string? archiveDirectory = null)
    {
        _dataDirectory = dataDirectory;
        if (!Directory.Exists(_dataDirectory))
        {
            Directory.CreateDirectory(_dataDirectory);
        }

        // 归档目录可选; 留空 / null 则不启用归档双写
        _archiveDirectory = string.IsNullOrWhiteSpace(archiveDirectory) ? null : archiveDirectory;
        if (_archiveDirectory != null && !Directory.Exists(_archiveDirectory))
        {
            Directory.CreateDirectory(_archiveDirectory);
        }

        // Bounded + DropOldest: 写盘卡顿时丢弃旧通知 (数据本身在 _pending 字典里, 通知只是唤醒 worker
        // 的信号, 通知重复或丢失不影响正确性, _pending 里 key 数自然有上限 = 在线终端数)
        // 容量 50000 远大于 10K 终端并发上报数, 正常永远不会触发 DropOldest
        _notifyChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(50000)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _worker = Task.Run(WriteLoopAsync);
    }

    /// <summary>
    /// 保存位置信息 — 非阻塞, 仅放入待写队列
    /// </summary>
    public void SaveLocation(string plateNumber, string phoneNumber, LocationInfo location)
    {
        var normalizedPhone = NormalizePhoneNumber(phoneNumber);
        var content = FormatLocationXml(normalizedPhone, location);

        // 后到的覆盖前面的, 同一车一个周期内只写一次最新
        _pending[normalizedPhone] = content;
        _notifyChannel.Writer.TryWrite(normalizedPhone);
    }

    /// <summary>
    /// 后台写入循环
    /// </summary>
    private async Task WriteLoopAsync()
    {
        var reader = _notifyChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                // 批量消费当前所有通知, 去重 (后到覆盖前面)
                var batch = new HashSet<string>();
                while (reader.TryRead(out var phone))
                {
                    batch.Add(phone);
                    if (batch.Count >= 512) break; // 单批上限
                }

                foreach (var phone in batch)
                {
                    if (_pending.TryRemove(phone, out var content))
                    {
                        TryWriteFile(phone, content);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch
        {
            // 后台任务必须吞掉异常, 防止整个进程崩溃
        }
    }

    private void TryWriteFile(string phone, string content)
    {
        // ============ 主存储: LocationData/{phone}.xml (与原版一致, 不变) ============
        var filePath = Path.Combine(_dataDirectory, $"{phone}.xml");
        try
        {
            File.WriteAllText(filePath, content, Utf8WithBom);
        }
        catch
        {
            // 单个文件写失败不影响其他车; 静默吞掉, 不阻塞主流程
        }

        // ============ 归档双写: {ArchiveDir}/{yyyyMMdd}/{phone}.xml ============
        if (_archiveDirectory != null)
        {
            try
            {
                // 用本地日期 (跟 GpsTime 显示一致, 中国时区)
                var dateFolder = DateTime.Now.ToString("yyyyMMdd");
                EnsureArchiveDayFolder(dateFolder);
                var archivePath = Path.Combine(_archiveDirectory, dateFolder, $"{phone}.xml");
                File.WriteAllText(archivePath, content, Utf8WithBom);
            }
            catch
            {
                // 归档失败也静默, 绝不影响主存储
            }
        }
    }

    /// <summary>
    /// 确保归档日期文件夹存在 — worker 单线程, 用字段缓存避免每次都做磁盘检查
    /// 跨天 (00:00) 后会自动创建新一天的文件夹
    /// </summary>
    private void EnsureArchiveDayFolder(string dateFolder)
    {
        if (_lastEnsuredArchiveDate == dateFolder) return;
        var fullPath = Path.Combine(_archiveDirectory!, dateFolder);
        Directory.CreateDirectory(fullPath); // 幂等
        _lastEnsuredArchiveDate = dateFolder;
    }

    /// <summary>
    /// 标准化手机号为12位
    /// 短的左侧补 0; 长的取末尾 12 位 (防异常长字符串当文件名)
    /// </summary>
    private static string NormalizePhoneNumber(string phoneNumber)
    {
        if (string.IsNullOrEmpty(phoneNumber)) return "000000000000";
        var trimmed = phoneNumber.TrimStart('0');
        if (string.IsNullOrEmpty(trimmed)) return "000000000000";
        if (trimmed.Length > 12)
            trimmed = trimmed.Substring(trimmed.Length - 12);
        return trimmed.PadLeft(12, '0');
    }

    /// <summary>
    /// 格式化位置信息为XML — 字段顺序与上游平台标准格式完全一致
    /// </summary>
    private static string FormatLocationXml(string phoneNumber, LocationInfo location)
    {
        // ===== 状态位解析 (JT808 状态字段, 32 位) =====
        uint status = location.Status;
        int accZt     = (int)( status        & 0x01); // bit0: ACC状态     0=关 1=开
        int dingweiZt = (int)((status >>  1) & 0x01); // bit1: 定位状态    0=未定位 1=定位
        int yunYinZt  = (int)((status >>  4) & 0x01); // bit4: 运营状态    0=运营 1=停运

        // ===== 报警标志位解析 (JT808 报警标志, 32 位) =====
        uint alarm = location.AlarmFlag;
        int Bit(int n) => (int)((alarm >> n) & 0x01);

        // ===== 数值字段 =====
        double longitude    = location.GetLongitude();
        double latitude     = location.GetLatitude();
        double altitude     = location.Altitude;
        double speed        = location.GetSpeed();
        double directionRad = location.Direction * Math.PI / 180.0;
        double mileageKm    = GetMileageKm(location);
        double oilLevel     = GetOilLevel(location);

        var sb = new StringBuilder(2048);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<NewDataSet>");
        sb.Append("<table>");

        // ----- 基本信息 -----
        sb.Append($"<PhoneNumber>{phoneNumber}</PhoneNumber>");
        sb.Append($"<Time>{location.GpsTime.ToString("yyyy-MM-dd HH:mm:ss", Inv)}</Time>");

        // ----- 状态字段 -----
        sb.Append($"<ACCZT>{accZt}</ACCZT>");
        sb.Append($"<DingweiZT>{dingweiZt}</DingweiZT>");
        sb.Append($"<YunYinZT>{yunYinZt}</YunYinZT>");

        // ----- 位置/运动字段 -----
        // 注意: 上游平台约定 <Longitude> 标签里写"纬度值", <Latitude> 标签里写"经度值"
        // (字段名与地理含义反着, 是上游历史约定, 不要"修正"它)
        sb.Append($"<Longitude>{latitude.ToString("F6", Inv)}</Longitude>");
        sb.Append($"<Latitude>{longitude.ToString("F6", Inv)}</Latitude>");
        sb.Append($"<GaoDu>{altitude.ToString("F6", Inv)}</GaoDu>");
        sb.Append($"<Speed>{speed.ToString("F1", Inv)}</Speed>");
        sb.Append($"<direction>{directionRad.ToString("F2", Inv)}</direction>");
        sb.Append($"<mileage>{mileageKm.ToString("F6", Inv)}</mileage>");
        sb.Append($"<ilLevel>{oilLevel.ToString("F6", Inv)}</ilLevel>");

        // ===== 报警信号 (28 项, 严格对应 JT808-2019 报警标志位 0~14、18~30) =====
        sb.Append($"<JinjiBJ>{Bit(0)}</JinjiBJ>");
        sb.Append($"<ChaoSuBJ>{Bit(1)}</ChaoSuBJ>");
        sb.Append($"<PiNaoJiaShi>{Bit(2)}</PiNaoJiaShi>");
        sb.Append($"<WeiXianYJ>{Bit(3)}</WeiXianYJ>");
        sb.Append($"<GNSSmokuaiGZ>{Bit(4)}</GNSSmokuaiGZ>");
        sb.Append($"<GNSSTXWJGZ>{Bit(5)}</GNSSTXWJGZ>");
        sb.Append($"<GNSSTXDL>{Bit(6)}</GNSSTXDL>");
        sb.Append($"<ZDZDYQY>{Bit(7)}</ZDZDYQY>");
        sb.Append($"<ZDZDYDD>{Bit(8)}</ZDZDYDD>");
        sb.Append($"<ZDLCDHXSQGZ>{Bit(9)}</ZDLCDHXSQGZ>");
        sb.Append($"<TTSMKGZ>{Bit(10)}</TTSMKGZ>");
        sb.Append($"<SXTGZ>{Bit(11)}</SXTGZ>");
        sb.Append($"<YSZICKMKGZ>{Bit(12)}</YSZICKMKGZ>");
        sb.Append($"<CSYJ>{Bit(13)}</CSYJ>");
        sb.Append($"<PNJSYJ>{Bit(14)}</PNJSYJ>");
        sb.Append($"<DTLJJSCS>{Bit(18)}</DTLJJSCS>");
        sb.Append($"<CSTCBJ>{Bit(19)}</CSTCBJ>");
        sb.Append($"<JCQYBJ>{Bit(20)}</JCQYBJ>");
        sb.Append($"<JCLXBJ>{Bit(21)}</JCLXBJ>");
        sb.Append($"<LDXSSJBZHGC>{Bit(22)}</LDXSSJBZHGC>");
        sb.Append($"<LXPLBJ>{Bit(23)}</LXPLBJ>");
        sb.Append($"<CLVSSGZ>{Bit(24)}</CLVSSGZ>");
        sb.Append($"<CLYLYCBJ>{Bit(25)}</CLYLYCBJ>");
        sb.Append($"<CLBDBJ>{Bit(26)}</CLBDBJ>");
        sb.Append($"<CLFFDHBJ>{Bit(27)}</CLFFDHBJ>");
        sb.Append($"<CLFFWYBJ>{Bit(28)}</CLFFWYBJ>");
        sb.Append($"<PZYJ>{Bit(29)}</PZYJ>");
        sb.Append($"<CFYJ>{Bit(30)}</CFYJ>");

        sb.Append("</table>");
        sb.Append("</NewDataSet>");
        return sb.ToString();
    }

    private static double GetMileageKm(LocationInfo location)
    {
        foreach (var info in location.AdditionalInfoList)
        {
            if (info.Id == LocationAdditionalInfoId.Mileage && info.Length == 4 && info.Content != null)
            {
                return ReadUInt32(info.Content) / 10.0;
            }
        }
        return 0.0;
    }

    private static double GetOilLevel(LocationInfo location)
    {
        foreach (var info in location.AdditionalInfoList)
        {
            if (info.Id == LocationAdditionalInfoId.FuelQuantity && info.Length == 2 && info.Content != null)
            {
                int raw = (info.Content[0] << 8) | info.Content[1];
                return raw / 10.0;
            }
        }
        return 0.0;
    }

    private static uint ReadUInt32(byte[] buffer)
    {
        return (uint)((buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3]);
    }

    public string GetDataDirectory() => _dataDirectory;

    /// <summary>
    /// 待写入条数 (用于监控)
    /// </summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// 优雅停机: 等待 worker 排空
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _notifyChannel.Writer.TryComplete();
            // 给 worker 最多 3 秒排空
            await Task.WhenAny(_worker, Task.Delay(3000)).ConfigureAwait(false);
        }
        finally
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
