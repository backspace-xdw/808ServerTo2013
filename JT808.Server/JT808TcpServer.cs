using System.Buffers;
using System.Net;
using System.Net.Sockets;
using JT808.Protocol;
using Microsoft.Extensions.Logging;

namespace JT808.Server;

/// <summary>
/// JT808-2019 TCP服务器 — 高并发优化版 (10K+ 终端)
/// 关键设计:
///   1. 每个 Session 持有复用的 SocketAsyncEventArgs + ArrayPool 缓冲区, 零分配热路径
///   2. 接收回调内联处理消息 (不用 Task.Run, 避免 ThreadPool 抖动)
///   3. 同一 Socket 的发送由 per-session lock 串行化, 防止并发 Send 错乱
///   4. 热路径只用 LogDebug + IsEnabled 检查, Information 仅在状态变化时输出
///   5. 连接数硬上限保护 + 单文件 PeriodicTimer 清理
/// </summary>
public class JT808TcpServer
{
    private readonly ILogger<JT808TcpServer> _logger;
    private readonly SessionManager _sessionManager;
    private readonly LocationDataStore _locationDataStore;
    private readonly MediaDataStore _mediaDataStore;
    private readonly Version2019Recorder _version2019Recorder;

    private Socket? _serverSocket;
    private SocketAsyncEventArgs? _acceptArgs;
    private volatile bool _isRunning;
    private PeriodicTimer? _cleanupTimer;

    private readonly string _ipAddress;
    private readonly int _port;
    private readonly int _backlog;
    private readonly int _maxConcurrentConnections;
    private readonly int _sessionTimeoutMinutes;

    // 每个 session 的接收缓冲区大小
    private const int ReceiveBufferSize = 4096;

    public JT808TcpServer(
        ILogger<JT808TcpServer> logger,
        string ipAddress = "0.0.0.0",
        int port = 8808,
        int backlog = 4096,
        int maxConcurrentConnections = 12000,
        string locationDataDir = "LocationData",
        string? locationArchiveDir = null,
        string mediaDataDir = "MediaData",
        string version2019RecordPath = "Version2019Devices.ini",
        int version2019MaxDevices = 50000,
        int version2019StaleDays = 30,
        int version2019FlushIntervalMs = 1000,
        int sessionTimeoutMinutes = 30)
    {
        _logger = logger;
        _sessionManager = new SessionManager();
        _locationDataStore = new LocationDataStore(locationDataDir, locationArchiveDir);
        _mediaDataStore = new MediaDataStore(mediaDataDir);
        _version2019Recorder = new Version2019Recorder(
            version2019RecordPath,
            version2019MaxDevices,
            version2019StaleDays,
            version2019FlushIntervalMs);
        _ipAddress = ipAddress;
        _port = port;
        _backlog = backlog;
        _maxConcurrentConnections = maxConcurrentConnections;
        _sessionTimeoutMinutes = sessionTimeoutMinutes;
    }

    public void Start()
    {
        if (_isRunning)
        {
            _logger.LogWarning("服务器已经在运行中");
            return;
        }

        try
        {
            _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _serverSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            var ipAddr = _ipAddress == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(_ipAddress);
            _serverSocket.Bind(new IPEndPoint(ipAddr, _port));
            _serverSocket.Listen(_backlog);

            _isRunning = true;
            _logger.LogInformation("JT808-2013 服务器启动成功, 监听 {Addr}:{Port}, backlog={Backlog}, maxConn={MaxConn}",
                _ipAddress, _port, _backlog, _maxConcurrentConnections);

            // 复用单个 accept SAEA
            _acceptArgs = new SocketAsyncEventArgs();
            _acceptArgs.Completed += OnAcceptCompleted;

            // 启动周期清理任务 — fire-and-forget
            // PeriodicTimer.Dispose() (在 Stop 中) 会让 WaitForNextTickAsync 抛 OperationCanceledException, worker 自动退出
            _cleanupTimer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            _ = Task.Run(CleanupLoopAsync);

            StartAccept();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "服务器启动失败");
            throw;
        }
    }

    /// <summary>
    /// 优雅停机:
    ///   1. 关闭 listen socket / 取消 accept
    ///   2. 关闭所有 session 释放 buffer / SAEA
    ///   3. 等待 LocationDataStore 后台 worker 把 pending 位置数据刷盘
    ///   4. 把 Version2019Recorder 最后一次刷盘
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        try
        {
            _cleanupTimer?.Dispose();
            _cleanupTimer = null;
            _serverSocket?.Close();
            _serverSocket = null;
            _acceptArgs?.Dispose();
            _acceptArgs = null;

            // 关闭所有会话
            foreach (var s in _sessionManager.GetAllSessions().ToList())
            {
                _sessionManager.RemoveSession(s.SessionId);
            }

            // 等 LocationDataStore 后台 worker 把 pending 数据刷完盘
            // (DisposeAsync 内部最多等 3 秒)
            try
            {
                _locationDataStore.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocationDataStore 停机刷盘失败");
            }

            // 把 2019 设备记录最后刷一次盘
            try { _version2019Recorder.Dispose(); } catch { }

            _logger.LogInformation("服务器已停止");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "服务器停止时发生错误");
        }
    }

    // ============================================================
    //                          ACCEPT
    // ============================================================
    private void StartAccept()
    {
        if (!_isRunning || _serverSocket == null || _acceptArgs == null) return;

        _acceptArgs.AcceptSocket = null; // 必须复位, 否则 AcceptAsync 会失败
        try
        {
            if (!_serverSocket.AcceptAsync(_acceptArgs))
            {
                ProcessAccept(_acceptArgs);
            }
        }
        catch (ObjectDisposedException) { /* server stopped */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AcceptAsync 异常");
            // 发生异常仍尝试继续接受
            if (_isRunning) StartAccept();
        }
    }

    private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs args) => ProcessAccept(args);

    private void ProcessAccept(SocketAsyncEventArgs args)
    {
        try
        {
            if (args.SocketError == SocketError.Success && args.AcceptSocket != null)
            {
                var clientSocket = args.AcceptSocket;

                // —— 连接数上限保护 ——
                int online = _sessionManager.GetOnlineCount();
                if (online >= _maxConcurrentConnections)
                {
                    _logger.LogWarning("达到最大连接数 {Max}, 拒绝新连接", _maxConcurrentConnections);
                    try { clientSocket.Close(); } catch { }
                }
                else
                {
                    // —— Socket 调优 ——
                    try
                    {
                        clientSocket.NoDelay = true;
                        clientSocket.SendTimeout = 5000;
                        clientSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    }
                    catch { }

                    var session = _sessionManager.AddSession(clientSocket);

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("新连接 {EP}, Sid={Sid}, 在线={Cnt}",
                            clientSocket.RemoteEndPoint, session.SessionId, online + 1);
                    }

                    // 给会话分配复用的接收 SAEA + 池化 buffer
                    StartReceive(session);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessAccept 异常");
        }
        finally
        {
            // 立即接受下一个连接
            if (_isRunning) StartAccept();
        }
    }

    // ============================================================
    //                          RECEIVE
    // ============================================================
    private void StartReceive(SessionInfo session)
    {
        try
        {
            // 租用复用的 buffer 和 SAEA (Buffer 在 SessionManager.RemoveSession 中归还)
            var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
            var args = new SocketAsyncEventArgs();
            args.UserToken = session;
            args.SetBuffer(buffer, 0, buffer.Length);
            args.Completed += OnReceiveCompleted;

            session.ReceiveBuffer = buffer;
            session.ReceiveArgs = args;

            DoReceive(session, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartReceive 异常, Sid={Sid}", session.SessionId);
            _sessionManager.RemoveSession(session.SessionId);
        }
    }

    private void DoReceive(SessionInfo session, SocketAsyncEventArgs args)
    {
        if (session.IsClosed) return;
        try
        {
            // 关键: 每次都重置到 buffer 起点 (修复原版 offset 累加 bug)
            args.SetBuffer(0, ReceiveBufferSize);
            if (!session.Socket.ReceiveAsync(args))
            {
                // 同步完成 — 立即处理 (用 while 循环防止深递归栈溢出)
                ProcessReceiveSync(args);
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(ex, "DoReceive 异常, Sid={Sid}", session.SessionId);
            _sessionManager.RemoveSession(session.SessionId);
        }
    }

    /// <summary>
    /// 同步完成路径 — 用循环避免递归栈溢出
    /// 加 MaxSyncIterations 上限防止单个高频客户端长时间独占 IOCP 线程,
    /// 超过上限后通过 Task.Run 让出 IOCP 线程让其他连接得到处理
    /// </summary>
    private const int MaxSyncIterations = 16;

    private void ProcessReceiveSync(SocketAsyncEventArgs args)
    {
        int iterations = 0;
        while (true)
        {
            var session = (SessionInfo)args.UserToken!;
            if (!HandleReceiveOnce(session, args)) return;

            // 同步路径上限到了 — 让出 IOCP 线程, 后续 receive 通过 ThreadPool 调度
            if (++iterations >= MaxSyncIterations)
            {
                _ = Task.Run(() => DoReceive(session, args));
                return;
            }

            // 继续接收
            args.SetBuffer(0, ReceiveBufferSize);
            try
            {
                if (session.Socket.ReceiveAsync(args)) return; // 进入异步等待
            }
            catch (ObjectDisposedException) { return; }
            catch
            {
                _sessionManager.RemoveSession(session.SessionId);
                return;
            }
        }
    }

    private void OnReceiveCompleted(object? sender, SocketAsyncEventArgs args)
    {
        var session = (SessionInfo)args.UserToken!;
        if (!HandleReceiveOnce(session, args)) return;
        DoReceive(session, args);
    }

    /// <summary>
    /// 处理一次接收 — 返回 true 表示继续, false 表示连接已断开
    /// </summary>
    private bool HandleReceiveOnce(SessionInfo session, SocketAsyncEventArgs args)
    {
        if (args.SocketError != SocketError.Success || args.BytesTransferred <= 0)
        {
            // 对端关闭或错误
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("连接断开 Sid={Sid} Phone={Phone} Err={Err}",
                    session.SessionId, session.PhoneNumber, args.SocketError);
            _sessionManager.RemoveSession(session.SessionId);
            return false;
        }

        try
        {
            // 直接把 args.Buffer 的有效片段塞进消息缓冲区
            session.MessageBuffer.Append(args.Buffer!, args.Offset, args.BytesTransferred);

            // 提取所有完整消息
            var messages = session.MessageBuffer.ExtractMessages();

            session.TouchActive();

            // —— 内联处理 — 不用 Task.Run, 避免 ThreadPool 抖动 ——
            for (int i = 0; i < messages.Count; i++)
            {
                session.IncReceived();
                ProcessMessage(session, messages[i]);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleReceiveOnce 异常 Sid={Sid}", session.SessionId);
            _sessionManager.RemoveSession(session.SessionId);
            return false;
        }
    }

    // 连续解码失败超过此次数 → 视为恶意/坏客户端, 强制断开
    private const int MaxConsecutiveDecodeErrors = 16;

    // ============================================================
    //                          MESSAGE PROCESSING
    // ============================================================
    private void ProcessMessage(SessionInfo session, byte[] data)
    {
        try
        {
            var message = JT808Decoder.Decode(data);
            if (message == null)
            {
                int errors = session.IncDecodeErrors();
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("消息解码失败 Sid={Sid} 累计={Err}", session.SessionId, errors);
                if (errors >= MaxConsecutiveDecodeErrors)
                {
                    _logger.LogWarning("Sid={Sid} 连续解码错误 {Err} 次, 强制断开", session.SessionId, errors);
                    _sessionManager.RemoveSession(session.SessionId);
                }
                return;
            }
            // 收到正确消息 → 重置错误计数
            session.ResetDecodeErrors();

            // ===== 关键: 本服务器只处理 2013 协议 =====
            // 检测到 2019 版本数据时, 仅记录该终端的存在 (手机号 + 协议版本 + 时间 + 远端 IP),
            // 不解析消息体, 也不发送 2019 格式应答, 直接返回
            if (message.Header.Is2019Version)
            {
                var remoteEp = "";
                try { remoteEp = session.Socket.RemoteEndPoint?.ToString() ?? ""; } catch { }
                _version2019Recorder.Record(
                    message.Header.PhoneNumber,
                    message.Header.ProtocolVersion,
                    message.Header.MessageId,
                    remoteEp);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("收到 2019 版本数据 Phone={Phone} MsgId=0x{Id:X4} 已记录, 跳过业务处理",
                        message.Header.PhoneNumber, message.Header.MessageId);
                }
                return;
            }

            // 绑定手机号 (仅首次)
            if (string.IsNullOrEmpty(session.PhoneNumber) && !string.IsNullOrEmpty(message.Header.PhoneNumber))
            {
                _sessionManager.BindPhoneNumber(session.SessionId, message.Header.PhoneNumber);
            }

            byte[]? response = null;
            switch (message.Header.MessageId)
            {
                case JT808MessageId.TerminalRegister:
                    response = HandleRegister(session, message);
                    break;
                case JT808MessageId.TerminalAuthentication:
                    response = HandleAuthentication(session, message);
                    break;
                case JT808MessageId.TerminalHeartbeat:
                    response = HandleHeartbeat(session, message);
                    break;
                case JT808MessageId.LocationReport:
                    response = HandleLocationReport(session, message);
                    break;
                case JT808MessageId.LocationBatchUpload:
                    response = HandleLocationBatchUpload(session, message);
                    break;
                case JT808MessageId.MultimediaDataUpload:
                    response = HandleMultimediaDataUpload(session, message);
                    break;
                default:
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("未处理消息 0x{Id:X4}", message.Header.MessageId);
                    response = JT808Encoder.EncodePlatformGeneralResponse(
                        message.Header.PhoneNumber,
                        message.Header.SerialNumber,
                        message.Header.MessageId,
                        (byte)CommonResult.NotSupported,
                        session.Is2019Version);
                    break;
            }

            if (response != null)
            {
                SendData(session, response);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessMessage 异常 Sid={Sid}", session.SessionId);
        }
    }

    private byte[] HandleRegister(SessionInfo session, JT808Message message)
    {
        var registerInfo = JT808Decoder.DecodeRegisterInfo(message.Body);
        if (registerInfo != null)
        {
            // 注册是状态事件, 用 Information
            _logger.LogInformation("终端注册 Phone={Phone} Plate={Plate} Term={Term} Mfr={Mfr}",
                message.Header.PhoneNumber, registerInfo.PlateNumber,
                registerInfo.TerminalId, registerInfo.ManufacturerId);

            session.TerminalModel = registerInfo.TerminalModel;
            session.PlateNumber = registerInfo.PlateNumber;

            string authCode = Guid.NewGuid().ToString("N").Substring(0, 20);
            _sessionManager.SetAuthenticated(session.SessionId, false, authCode);

            return JT808Encoder.EncodeRegisterResponse(
                message.Header.PhoneNumber,
                message.Header.SerialNumber,
                RegisterResult.Success,
                authCode,
                session.Is2019Version);
        }

        return JT808Encoder.EncodeRegisterResponse(
            message.Header.PhoneNumber,
            message.Header.SerialNumber,
            RegisterResult.NoVehicleInDatabase,
            "",
            session.Is2019Version);
    }

    private byte[] HandleAuthentication(SessionInfo session, JT808Message message)
    {
        var authInfo = JT808Decoder.DecodeAuthenticationInfo(message.Body);
        if (authInfo != null)
        {
            _logger.LogInformation("终端鉴权 Phone={Phone} IMEI={IMEI} SwVer={Sw}",
                message.Header.PhoneNumber, authInfo.IMEI, authInfo.SoftwareVersion);
            session.IMEI = authInfo.IMEI;
            session.SoftwareVersion = authInfo.SoftwareVersion;
        }

        _sessionManager.SetAuthenticated(session.SessionId, true);

        return JT808Encoder.EncodePlatformGeneralResponse(
            message.Header.PhoneNumber,
            message.Header.SerialNumber,
            message.Header.MessageId,
            (byte)CommonResult.Success,
            session.Is2019Version);
    }

    private byte[] HandleHeartbeat(SessionInfo session, JT808Message message)
    {
        // 心跳频率最高, 完全不打日志
        return JT808Encoder.EncodePlatformGeneralResponse(
            message.Header.PhoneNumber,
            message.Header.SerialNumber,
            message.Header.MessageId,
            (byte)CommonResult.Success,
            session.Is2019Version);
    }

    private byte[] HandleLocationReport(SessionInfo session, JT808Message message)
    {
        var location = JT808Decoder.DecodeLocationInfo(message.Body);
        if (location != null)
        {
            // 位置上报是次高频, 只在 Debug 级别打日志
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("位置 Phone={Phone} Plate={Plate} Lon={Lon:F6} Lat={Lat:F6} Spd={Spd:F1}",
                    message.Header.PhoneNumber, session.PlateNumber ?? "-",
                    location.GetLongitude(), location.GetLatitude(), location.GetSpeed());
            }

            // 异步入队 — 非阻塞
            _locationDataStore.SaveLocation(
                session.PlateNumber ?? string.Empty,
                message.Header.PhoneNumber,
                location);
        }

        return JT808Encoder.EncodePlatformGeneralResponse(
            message.Header.PhoneNumber,
            message.Header.SerialNumber,
            message.Header.MessageId,
            (byte)CommonResult.Success,
            session.Is2019Version);
    }

    private byte[] HandleLocationBatchUpload(SessionInfo session, JT808Message message)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("批量位置上传 Phone={Phone} Len={Len}",
                message.Header.PhoneNumber, message.Body.Length);

        return JT808Encoder.EncodePlatformGeneralResponse(
            message.Header.PhoneNumber,
            message.Header.SerialNumber,
            message.Header.MessageId,
            (byte)CommonResult.Success,
            session.Is2019Version);
    }

    private byte[] HandleMultimediaDataUpload(SessionInfo session, JT808Message message)
    {
        // 提取分包信息
        ushort totalPackages = 0;
        ushort packageIndex = 0;
        if (message.Header.IsPackage && message.Header.Package != null)
        {
            totalPackages = message.Header.Package.TotalPackage;
            packageIndex = message.Header.Package.PackageIndex;
        }

        // ===== 关键: JT808-2019 多媒体分包语义 =====
        // 单包 (totalPackages<=1) 或首包 (packageIndex<=1):
        //   body 包含完整 36 字节元数据头 (Mid + Type + Format + Event + ChannelId + Location)
        // 后续子包 (packageIndex >= 2):
        //   body 全部是裸多媒体数据, 没有任何元数据头, 不能调用 DecodeMultimediaDataUpload
        try
        {
            MediaProcessResult result;
            if (totalPackages <= 1 || packageIndex <= 1)
            {
                // 单包或首包: 解析元数据头
                result = _mediaDataStore.ProcessFirstOrSingle(
                    message.Header.PhoneNumber,
                    message.Header.SerialNumber,
                    totalPackages,
                    packageIndex,
                    message.Body);
            }
            else
            {
                // 后续子包: 直接传裸 body
                result = _mediaDataStore.ProcessSubsequent(
                    message.Header.PhoneNumber,
                    message.Header.SerialNumber,
                    totalPackages,
                    packageIndex,
                    message.Body);
            }

            if (result.IsComplete)
            {
                _logger.LogInformation("多媒体保存 Mid={Mid} -> {File}",
                    result.MultimediaId, result.FilePath);
                return JT808Encoder.EncodeMultimediaDataUploadResponse(
                    message.Header.PhoneNumber, result.MultimediaId, null, session.Is2019Version);
            }
            if (result.MissingPackageIds.Count > 0)
            {
                _logger.LogWarning("多媒体漏包 Mid={Mid} 收到={R}/{T} 缺={Missing}",
                    result.MultimediaId, result.ReceivedCount, result.TotalCount,
                    result.MissingPackageIds.Count);
                return JT808Encoder.EncodeMultimediaDataUploadResponse(
                    message.Header.PhoneNumber, result.MultimediaId,
                    result.MissingPackageIds, session.Is2019Version);
            }
            // 静默累积中
            return JT808Encoder.EncodePlatformGeneralResponse(
                message.Header.PhoneNumber, message.Header.SerialNumber,
                message.Header.MessageId, (byte)CommonResult.Success, session.Is2019Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理多媒体上传失败 Phone={Phone} Serial={Serial} Idx={Idx}/{Tot}",
                message.Header.PhoneNumber, message.Header.SerialNumber, packageIndex, totalPackages);
            return JT808Encoder.EncodePlatformGeneralResponse(
                message.Header.PhoneNumber, message.Header.SerialNumber,
                message.Header.MessageId, (byte)CommonResult.Failure, session.Is2019Version);
        }
    }

    // ============================================================
    //                          SEND
    // ============================================================
    /// <summary>
    /// 发送数据 — 立刻返回, 实际发送在 ThreadPool 工作线程上跑
    /// 这样 IOCP 线程不会被慢客户端的 sync Send 阻塞,
    /// per-session SendLock 仍然保证同一 socket 的发送顺序
    /// </summary>
    private void SendData(SessionInfo session, byte[] data)
    {
        if (session.IsClosed) return;
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            var (server, sess, payload) = state;
            server.SendDataSync(sess, payload);
        }, (this, session, data), preferLocal: false);
    }

    /// <summary>
    /// per-session 串行锁内的同步发送
    /// JT808 应答包很小 (&lt;100 字节), 内核 send buffer 会立即吸收
    /// SendTimeout=5s 是慢客户端兜底; 超时即抛, 触发会话关闭
    /// </summary>
    private void SendDataSync(SessionInfo session, byte[] data)
    {
        if (session.IsClosed) return;

        try
        {
            lock (session.SendLock)
            {
                if (session.IsClosed) return;
                int total = 0;
                while (total < data.Length)
                {
                    int sent = session.Socket.Send(data, total, data.Length - total, SocketFlags.None);
                    if (sent <= 0) break;
                    total += sent;
                }
            }
            session.IncSent();
        }
        catch (SocketException sex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Send 失败 Sid={Sid} Err={Err}", session.SessionId, sex.SocketErrorCode);
            _sessionManager.RemoveSession(session.SessionId);
        }
        catch (ObjectDisposedException)
        {
            _sessionManager.RemoveSession(session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Send 异常 Sid={Sid}", session.SessionId);
            _sessionManager.RemoveSession(session.SessionId);
        }
    }

    // ============================================================
    //                          CLEANUP LOOP
    // ============================================================
    private async Task CleanupLoopAsync()
    {
        var timer = _cleanupTimer;
        if (timer == null) return;

        try
        {
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                if (!_isRunning) break;
                try
                {
                    int removed = _sessionManager.CleanupTimeoutSessions(_sessionTimeoutMinutes);
                    int online = _sessionManager.GetOnlineCount();
                    int pending = _locationDataStore.PendingCount;
                    int v2019 = _version2019Recorder.Count;

                    _logger.LogInformation(
                        "[Stats] 在线={Online} 清理超时={Removed} 待写位置={Pending} 2019终端={V2019}",
                        online, removed, pending, v2019);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CleanupLoop 异常");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ============================================================
    //                          PUBLIC API
    // ============================================================
    public SessionManager GetSessionManager() => _sessionManager;
    public LocationDataStore GetLocationDataStore() => _locationDataStore;
    public Version2019Recorder GetVersion2019Recorder() => _version2019Recorder;
}
