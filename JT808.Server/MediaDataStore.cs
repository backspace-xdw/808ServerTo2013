using System.Collections.Concurrent;
using JT808.Protocol;

namespace JT808.Server;

/// <summary>
/// 多媒体处理结果
/// </summary>
public class MediaProcessResult
{
    /// <summary>是否完成（所有分包已收齐）</summary>
    public bool IsComplete { get; set; }

    /// <summary>保存的文件路径（完成时有值）</summary>
    public string? FilePath { get; set; }

    /// <summary>多媒体ID (来自首包元数据头)</summary>
    public uint MultimediaId { get; set; }

    /// <summary>需要重传的包ID列表</summary>
    public List<ushort> MissingPackageIds { get; set; } = new();

    /// <summary>当前已收到的包数</summary>
    public int ReceivedCount { get; set; }

    /// <summary>总包数</summary>
    public int TotalCount { get; set; }
}

/// <summary>
/// 多媒体分包缓存项
///
/// JT808-2019 关键约束:
///   - 0x0801 多媒体数据上传若分包, 只有 packageIndex==1 的首包包含 36 字节元数据头
///     (4 Mid + 1 Type + 1 Format + 1 Event + 1 ChannelId + 28 LocationBasic = 36)
///   - packageIndex>=2 的子包 body 全部是裸多媒体数据, 没有元数据头
///   - 同一逻辑上传由消息头中的流水号关联: firstSerial = currentSerial - (packageIndex - 1)
/// </summary>
public class MediaPackageCache
{
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>第一包的消息流水号 (整个 cache 的逻辑 key)</summary>
    public ushort FirstSerial { get; set; }

    public ushort TotalPackages { get; set; }

    /// <summary>按 packageIndex 索引的分包数据 (1..N)</summary>
    public Dictionary<ushort, byte[]> Packages { get; set; } = new();

    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime LastUpdateTime { get; set; } = DateTime.Now;

    // ----- 元数据 (来自首包, 可能延迟到达) -----
    public bool MetadataReady { get; set; }
    public uint MultimediaId { get; set; }
    public MultimediaType Type { get; set; }
    public MultimediaFormat Format { get; set; }
    public MultimediaEvent Event { get; set; }
    public byte ChannelId { get; set; }
    public LocationInfo? Location { get; set; }

    /// <summary>获取缺失的包ID列表</summary>
    public List<ushort> GetMissingPackageIds()
    {
        var missing = new List<ushort>();
        for (ushort i = 1; i <= TotalPackages; i++)
        {
            if (!Packages.ContainsKey(i)) missing.Add(i);
        }
        return missing;
    }
}

/// <summary>
/// 多媒体数据存储器 - 支持分包组装、漏包重传、首包延迟到达
/// </summary>
public class MediaDataStore : IDisposable
{
    private readonly string _dataDirectory;
    private readonly object _saveLockObj = new();

    // 分包缓存: key = "手机号_首包流水号"
    private readonly ConcurrentDictionary<string, MediaPackageCache> _packageCache = new();

    private const int CacheTimeoutMinutes = 10;

    private readonly CancellationTokenSource _cts = new();

    public MediaDataStore(string dataDirectory = "MediaData")
    {
        _dataDirectory = dataDirectory;
        if (!Directory.Exists(_dataDirectory))
        {
            Directory.CreateDirectory(_dataDirectory);
        }

        _ = Task.Run(() => CleanupCacheTask(_cts.Token));
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    /// <summary>
    /// 处理首包或单包 (body 包含完整 36 字节元数据头 + 多媒体数据)
    /// </summary>
    /// <param name="phoneNumber">终端手机号</param>
    /// <param name="currentSerial">本条消息的流水号</param>
    /// <param name="totalPackages">总包数 (0 或 1 表示不分包)</param>
    /// <param name="packageIndex">本包序号 (0 或 1 表示不分包/首包)</param>
    /// <param name="body">完整 0x0801 消息体</param>
    public MediaProcessResult ProcessFirstOrSingle(
        string phoneNumber,
        ushort currentSerial,
        ushort totalPackages,
        ushort packageIndex,
        byte[] body)
    {
        var multimedia = JT808Decoder.DecodeMultimediaDataUpload(body);
        if (multimedia == null)
        {
            return new MediaProcessResult { IsComplete = false };
        }

        var normalizedPhone = NormalizePhoneNumber(phoneNumber);

        // 不分包或只有 1 包: 直接保存
        if (totalPackages <= 1)
        {
            return new MediaProcessResult
            {
                MultimediaId = multimedia.MultimediaId,
                IsComplete = true,
                FilePath = SaveMediaFile(normalizedPhone, multimedia),
                TotalCount = 1,
                ReceivedCount = 1,
            };
        }

        // 多包的首包 (packageIndex == 1)
        // 注意: 即便首包不是顺序到达 (子包先到), 此处的 firstSerial 仍等于 currentSerial,
        // 因为首包的 packageIndex == 1 → firstSerial == currentSerial
        ushort firstSerial = currentSerial;
        var cacheKey = MakeCacheKey(normalizedPhone, firstSerial);

        var cache = _packageCache.GetOrAdd(cacheKey, _ => new MediaPackageCache
        {
            PhoneNumber = normalizedPhone,
            FirstSerial = firstSerial,
            TotalPackages = totalPackages,
        });

        lock (cache)
        {
            // 写入元数据 (首包独有)
            cache.MultimediaId = multimedia.MultimediaId;
            cache.Type = multimedia.Type;
            cache.Format = multimedia.Format;
            cache.Event = multimedia.Event;
            cache.ChannelId = multimedia.ChannelId;
            cache.Location = multimedia.Location;
            cache.MetadataReady = true;

            // 首包的多媒体数据 = body 减去前 36 字节头, 已经在 multimedia.Data 里
            cache.Packages[packageIndex] = multimedia.Data;
            cache.LastUpdateTime = DateTime.Now;

            return CheckCompletion(cache, packageIndex, cacheKey);
        }
    }

    /// <summary>
    /// 处理后续子包 (packageIndex >= 2): body 全部是裸多媒体数据, 没有元数据头
    /// </summary>
    /// <param name="phoneNumber">终端手机号</param>
    /// <param name="currentSerial">本条消息的流水号</param>
    /// <param name="totalPackages">总包数</param>
    /// <param name="packageIndex">本包序号 (>= 2)</param>
    /// <param name="rawBody">本子包的整个消息体, 全部当裸数据</param>
    public MediaProcessResult ProcessSubsequent(
        string phoneNumber,
        ushort currentSerial,
        ushort totalPackages,
        ushort packageIndex,
        byte[] rawBody)
    {
        var normalizedPhone = NormalizePhoneNumber(phoneNumber);

        // 反推首包流水号: firstSerial = currentSerial - (packageIndex - 1)
        // 用 unchecked ushort 处理 65535→0 的环回
        ushort firstSerial = unchecked((ushort)(currentSerial - (packageIndex - 1)));
        var cacheKey = MakeCacheKey(normalizedPhone, firstSerial);

        var cache = _packageCache.GetOrAdd(cacheKey, _ => new MediaPackageCache
        {
            PhoneNumber = normalizedPhone,
            FirstSerial = firstSerial,
            TotalPackages = totalPackages,
            MetadataReady = false, // 首包还没到, 等
        });

        lock (cache)
        {
            cache.Packages[packageIndex] = rawBody;
            cache.LastUpdateTime = DateTime.Now;

            // 后到的 totalPackages 信息也要更新一下 (理论上首/后子包应该一致)
            if (cache.TotalPackages == 0) cache.TotalPackages = totalPackages;

            return CheckCompletion(cache, packageIndex, cacheKey);
        }
    }

    /// <summary>
    /// 完成度检查 — 必须 holds cache lock 调用
    /// </summary>
    private MediaProcessResult CheckCompletion(MediaPackageCache cache, ushort currentIndex, string cacheKey)
    {
        var result = new MediaProcessResult
        {
            MultimediaId = cache.MultimediaId,
            ReceivedCount = cache.Packages.Count,
            TotalCount = cache.TotalPackages,
        };

        // 收齐所有包 + 元数据齐备 → 组装并保存
        if (cache.Packages.Count >= cache.TotalPackages && cache.MetadataReady)
        {
            var missing = cache.GetMissingPackageIds();
            if (missing.Count == 0)
            {
                var completeData = AssemblePackages(cache);
                var fullMultimedia = new MultimediaDataUpload
                {
                    MultimediaId = cache.MultimediaId,
                    Type = cache.Type,
                    Format = cache.Format,
                    Event = cache.Event,
                    ChannelId = cache.ChannelId,
                    Location = cache.Location,
                    Data = completeData,
                };

                result.IsComplete = true;
                result.FilePath = SaveMediaFile(cache.PhoneNumber, fullMultimedia);

                _packageCache.TryRemove(cacheKey, out _);
                return result;
            }
        }

        // 当前是最后一包, 且仍有漏包 → 触发重传
        if (currentIndex == cache.TotalPackages)
        {
            var missing = cache.GetMissingPackageIds();
            if (missing.Count > 0)
            {
                result.MissingPackageIds = missing;
            }
        }

        result.IsComplete = false;
        return result;
    }

    /// <summary>
    /// 组装分包数据 (按 packageIndex 升序拼接)
    /// </summary>
    private byte[] AssemblePackages(MediaPackageCache cache)
    {
        var orderedPackages = cache.Packages
            .OrderBy(p => p.Key)
            .Select(p => p.Value)
            .ToList();

        var totalLength = orderedPackages.Sum(p => p.Length);
        var result = new byte[totalLength];
        var offset = 0;
        foreach (var package in orderedPackages)
        {
            Array.Copy(package, 0, result, offset, package.Length);
            offset += package.Length;
        }
        return result;
    }

    /// <summary>
    /// 保存多媒体文件
    /// </summary>
    private string SaveMediaFile(string phoneNumber, MultimediaDataUpload multimedia)
    {
        var dateDir = Path.Combine(_dataDirectory, DateTime.Now.ToString("yyyy-MM-dd"));
        if (!Directory.Exists(dateDir))
        {
            Directory.CreateDirectory(dateDir);
        }

        var timeStr = DateTime.Now.ToString("HH-mm-ss");
        var formatCode = (int)multimedia.Format;
        var eventCode = (int)multimedia.Event;
        var extension = multimedia.GetFileExtensionUpper();
        var fileName = $"{phoneNumber}_{formatCode}_{eventCode}_{timeStr}{extension}";
        var filePath = Path.Combine(dateDir, fileName);

        if (File.Exists(filePath))
        {
            timeStr = DateTime.Now.ToString("HH-mm-ss-fff");
            fileName = $"{phoneNumber}_{formatCode}_{eventCode}_{timeStr}{extension}";
            filePath = Path.Combine(dateDir, fileName);
        }

        lock (_saveLockObj)
        {
            File.WriteAllBytes(filePath, multimedia.Data);
        }

        return filePath;
    }

    private static string MakeCacheKey(string phone, ushort firstSerial) => $"{phone}_{firstSerial}";

    /// <summary>
    /// 标准化手机号为12位 (长的截取末尾 12 位防异常长串)
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
    /// 清理超时缓存 — 支持 cancellation, Dispose() 后能优雅退出
    /// </summary>
    private async Task CleanupCacheTask(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
                var expiredKeys = _packageCache
                    .Where(kv => (DateTime.Now - kv.Value.LastUpdateTime).TotalMinutes > CacheTimeoutMinutes)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _packageCache.TryRemove(key, out _);
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // 忽略清理错误
            }
        }
    }

    /// <summary>当前缓存的分包上传数</summary>
    public int GetPendingPackageCount() => _packageCache.Count;

    public string GetDataDirectory() => _dataDirectory;
}
