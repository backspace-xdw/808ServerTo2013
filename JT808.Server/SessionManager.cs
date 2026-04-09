using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using JT808.Protocol;

namespace JT808.Server;

/// <summary>
/// 会话信息 — 高并发优化：池化 SAEA、原子时间戳、per-session 发送锁
/// </summary>
public class SessionInfo
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public Socket Socket { get; init; } = null!;
    public string PhoneNumber { get; set; } = string.Empty;
    public DateTime ConnectTime { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 最后活跃时间 (UTC ticks, 用 Interlocked 原子读写)
    /// </summary>
    private long _lastActiveTicks = DateTime.UtcNow.Ticks;
    public long LastActiveTicks => Interlocked.Read(ref _lastActiveTicks);
    public DateTime LastActiveTime => new DateTime(LastActiveTicks, DateTimeKind.Utc).ToLocalTime();
    public void TouchActive() => Interlocked.Exchange(ref _lastActiveTicks, DateTime.UtcNow.Ticks);

    public bool IsAuthenticated { get; set; } = false;
    public string? AuthCode { get; set; }

    private long _receivedMessages;
    private long _sentMessages;
    public long ReceivedMessages => Interlocked.Read(ref _receivedMessages);
    public long SentMessages => Interlocked.Read(ref _sentMessages);
    public void IncReceived() => Interlocked.Increment(ref _receivedMessages);
    public void IncSent() => Interlocked.Increment(ref _sentMessages);

    /// <summary>
    /// 连续解码错误计数 (恶意/坏客户端识别). 收到正确消息后归零
    /// </summary>
    private int _decodeErrors;
    public int DecodeErrors => Volatile.Read(ref _decodeErrors);
    public int IncDecodeErrors() => Interlocked.Increment(ref _decodeErrors);
    public void ResetDecodeErrors() => Interlocked.Exchange(ref _decodeErrors, 0);

    public JT808MessageBuffer MessageBuffer { get; } = new JT808MessageBuffer();

    // 2019 版本特有信息
    // 默认 false (2013), 收到第一条 2019 消息时由 ProcessMessage 翻转为 true
    // 这样 2013 客户端的应答会用 2013 格式 (默认 true 会导致 2013 客户端被回 2019 格式)
    public bool Is2019Version { get; set; } = false;
    public byte ProtocolVersion { get; set; } = 0x01;
    public string? IMEI { get; set; }
    public string? SoftwareVersion { get; set; }
    public string? TerminalModel { get; set; }
    public string? PlateNumber { get; set; }

    /// <summary>
    /// 复用的接收 SocketAsyncEventArgs (整个会话生命周期一份)
    /// </summary>
    public SocketAsyncEventArgs? ReceiveArgs { get; set; }

    /// <summary>
    /// 接收缓冲区 (从 ArrayPool 租用，会话关闭时归还)
    /// </summary>
    public byte[]? ReceiveBuffer { get; set; }

    /// <summary>
    /// 发送串行锁：防止同一 socket 多线程并发 Send
    /// </summary>
    public object SendLock { get; } = new object();

    /// <summary>
    /// 0 = 活跃, 1 = 已关闭 (用 Interlocked 防止重复释放)
    /// </summary>
    private int _closed;
    public bool IsClosed => Volatile.Read(ref _closed) == 1;
    public bool MarkClosed() => Interlocked.CompareExchange(ref _closed, 1, 0) == 0;
}

/// <summary>
/// 会话管理器 — 支持 10K+ 并发
/// </summary>
public class SessionManager
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _phoneToSession = new();
    // BindPhoneNumber 顶号语义涉及 read-modify-write, 用单独 lock 保证原子性
    private readonly object _bindLock = new();
    private static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Shared;

    /// <summary>
    /// 添加会话
    /// </summary>
    public SessionInfo AddSession(Socket socket)
    {
        var session = new SessionInfo { Socket = socket };
        _sessions.TryAdd(session.SessionId, session);
        return session;
    }

    /// <summary>
    /// 移除会话 (幂等, 自动释放 SAEA + Buffer)
    /// </summary>
    public bool RemoveSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return false;
        if (!session.MarkClosed()) return false; // 已经被关闭过

        // 解绑手机号索引 — 仅当索引仍指向本会话才解绑，避免新会话覆盖后被误删
        if (!string.IsNullOrEmpty(session.PhoneNumber))
        {
            _phoneToSession.TryRemove(
                new KeyValuePair<string, string>(session.PhoneNumber, session.SessionId));
        }

        // 释放 Socket
        try { session.Socket?.Shutdown(SocketShutdown.Both); } catch { }
        try { session.Socket?.Close(); } catch { }

        // 归还 Buffer + 释放 SAEA
        if (session.ReceiveBuffer != null)
        {
            BufferPool.Return(session.ReceiveBuffer, clearArray: false);
            session.ReceiveBuffer = null;
        }
        try { session.ReceiveArgs?.Dispose(); } catch { }
        session.ReceiveArgs = null;

        return true;
    }

    public SessionInfo? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var s);
        return s;
    }

    public SessionInfo? GetSessionByPhone(string phoneNumber)
    {
        if (_phoneToSession.TryGetValue(phoneNumber, out var sid))
            return GetSession(sid);
        return null;
    }

    /// <summary>
    /// 绑定手机号 — 同手机号顶号上线时, 踢掉旧会话
    /// 整个 read-modify-write 流程在 _bindLock 内序列化, 防止两个新会话同时绑同一手机号
    /// 时索引/会话状态不一致
    /// </summary>
    public void BindPhoneNumber(string sessionId, string phoneNumber)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.PhoneNumber == phoneNumber) return;

        lock (_bindLock)
        {
            // 拿锁后 re-check, 防止两个线程同时通过外层快速路径
            if (session.PhoneNumber == phoneNumber) return;

            // 移除本会话上的旧手机号索引
            if (!string.IsNullOrEmpty(session.PhoneNumber))
            {
                _phoneToSession.TryRemove(
                    new KeyValuePair<string, string>(session.PhoneNumber, session.SessionId));
            }

            session.PhoneNumber = phoneNumber;

            // 顶号: 若该手机号已有别的会话, 踢掉它
            // RemoveSession 不会再调 BindPhoneNumber, 不会重入死锁
            if (_phoneToSession.TryGetValue(phoneNumber, out var oldSid) && oldSid != sessionId)
            {
                RemoveSession(oldSid);
            }

            _phoneToSession[phoneNumber] = sessionId;
        }
    }

    public void SetAuthenticated(string sessionId, bool authenticated, string? authCode = null)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
        {
            s.IsAuthenticated = authenticated;
            if (authCode != null) s.AuthCode = authCode;
        }
    }

    public IEnumerable<SessionInfo> GetAllSessions() => _sessions.Values;

    public int GetOnlineCount() => _sessions.Count;

    /// <summary>
    /// 清理超时会话 — 单次扫描 + 直接调用 RemoveSession
    /// </summary>
    public int CleanupTimeoutSessions(int timeoutMinutes = 30)
    {
        var thresholdTicks = DateTime.UtcNow.AddMinutes(-timeoutMinutes).Ticks;
        int removed = 0;
        foreach (var kv in _sessions)
        {
            if (kv.Value.LastActiveTicks < thresholdTicks)
            {
                if (RemoveSession(kv.Key)) removed++;
            }
        }
        return removed;
    }
}
