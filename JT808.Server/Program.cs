using JT808.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// 加载配置文件
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

// 绑定配置
var serverConfig = new ServerConfig();
configuration.GetSection("ServerConfig").Bind(serverConfig);

// 解析日志级别
var logLevel = Enum.TryParse<LogLevel>(serverConfig.LogLevel, true, out var level)
    ? level
    : LogLevel.Information;

// 创建日志工厂
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddConsole()
        .SetMinimumLevel(logLevel);
});

var logger = loggerFactory.CreateLogger<JT808TcpServer>();

// 创建并启动服务器
var server = new JT808TcpServer(
    logger,
    ipAddress: serverConfig.IpAddress,
    port: serverConfig.Port,
    backlog: serverConfig.Backlog,
    maxConcurrentConnections: serverConfig.MaxConcurrentConnections,
    locationDataDir: serverConfig.LocationDataDirectory,
    locationArchiveDir: serverConfig.LocationArchiveDirectory,
    mediaDataDir: serverConfig.MediaDataDirectory,
    version2019RecordPath: serverConfig.Version2019RecordPath,
    version2019MaxDevices: serverConfig.Version2019MaxDevices,
    version2019StaleDays: serverConfig.Version2019StaleDays,
    version2019FlushIntervalMs: serverConfig.Version2019FlushIntervalMs,
    sessionTimeoutMinutes: serverConfig.SessionTimeoutMinutes);

Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine("JT808-2013 车载终端通讯服务器");
Console.WriteLine("基于 JT/T 808-2013 协议");
Console.WriteLine($"支持 {serverConfig.MaxConcurrentConnections}+ 并发连接 (高并发优化版)");
Console.WriteLine("仅解析 2013, 检测到 2019 数据时仅记录设备信息");
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine();
Console.WriteLine("当前配置:");
Console.WriteLine($"  监听地址:       {serverConfig.IpAddress}:{serverConfig.Port}");
Console.WriteLine($"  Listen Backlog: {serverConfig.Backlog}");
Console.WriteLine($"  最大并发连接:   {serverConfig.MaxConcurrentConnections}");
Console.WriteLine($"  位置目录:       {serverConfig.LocationDataDirectory}");
Console.WriteLine($"  位置归档:       {(string.IsNullOrWhiteSpace(serverConfig.LocationArchiveDirectory) ? "(未启用)" : serverConfig.LocationArchiveDirectory + "/yyyyMMdd/")}");
Console.WriteLine($"  媒体目录:       {serverConfig.MediaDataDirectory}");
Console.WriteLine($"  2019设备记录:   {serverConfig.Version2019RecordPath}");
Console.WriteLine($"  2019最大设备:   {serverConfig.Version2019MaxDevices} 台 (满后 LRU 剔除)");
Console.WriteLine($"  2019过期清理:   {serverConfig.Version2019StaleDays} 天 (0=不清理)");
Console.WriteLine($"  2019刷盘间隔:   {serverConfig.Version2019FlushIntervalMs} ms");
Console.WriteLine($"  会话超时:       {serverConfig.SessionTimeoutMinutes} 分钟");
Console.WriteLine($"  日志级别:       {serverConfig.LogLevel}");
Console.WriteLine();
Console.WriteLine("提示: 高并发场景请确认系统配置:");
Console.WriteLine("  ulimit -n 65536                              # fd 上限");
Console.WriteLine("  sysctl -w net.core.somaxconn=8192            # listen 队列");
Console.WriteLine("  sysctl -w net.ipv4.tcp_max_syn_backlog=8192  # syn 队列");
Console.WriteLine();

// 优雅停机信号: Ctrl+C / SIGINT / SIGTERM 都会让循环退出, finally 里 server.Stop() 才能跑
var shutdownEvent = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // 阻止默认 abort, 让我们自己优雅退出
    Console.WriteLine();
    Console.WriteLine("收到 Ctrl+C, 正在优雅停机...");
    shutdownEvent.Set();
};

try
{
    server.Start();

    // 检查是否在后台运行 (systemd / docker / nohup 等场景下 stdin 会被重定向)
    bool isBackgroundMode = Console.IsInputRedirected;

    if (isBackgroundMode)
    {
        Console.WriteLine("后台模式运行，将每10秒显示一次统计...");
        Console.WriteLine("按 Ctrl+C 退出");
        Console.WriteLine();

        // 后台模式：定时显示统计, 直到收到停机信号
        while (!shutdownEvent.IsSet)
        {
            // 用 Wait(timeout) 替代 Thread.Sleep, 收到信号能立刻退出
            if (shutdownEvent.Wait(10000)) break;
            ShowStatisticsSimple(server);
        }
    }
    else
    {
        Console.WriteLine("按任意键查看统计信息, 按 Q 退出...");
        Console.WriteLine();

        while (!shutdownEvent.IsSet)
        {
            // KeyAvailable 轮询防止 ReadKey 阻塞 Ctrl+C 退出路径
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Q) break;
                ShowStatistics(server);
            }
            else
            {
                Thread.Sleep(100);
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"服务器运行错误: {ex.Message}");
}
finally
{
    server.Stop();
    Console.WriteLine("服务器已停止");
}

static void ShowStatistics(JT808TcpServer server)
{
    Console.Clear();
    Console.WriteLine("=".PadRight(100, '='));
    Console.WriteLine("服务器统计信息 (JT/T 808-2013)");
    Console.WriteLine("=".PadRight(100, '='));
    Console.WriteLine();

    var sessionManager = server.GetSessionManager();
    var sessions = sessionManager.GetAllSessions().ToList();
    var v2019Count = server.GetVersion2019Recorder().Count;

    Console.WriteLine($"在线终端数: {sessions.Count}");
    Console.WriteLine($"已鉴权终端: {sessions.Count(s => s.IsAuthenticated)}");
    Console.WriteLine($"已记录的 2019 终端 (仅记录, 不解析): {v2019Count}");
    Console.WriteLine();

    Console.WriteLine("终端列表:");
    Console.WriteLine("-".PadRight(100, '-'));
    Console.WriteLine($"{"手机号",-20} {"鉴权",-6} {"收/发消息",-15} {"车牌",-12} {"最后活跃",-20}");
    Console.WriteLine("-".PadRight(100, '-'));

    foreach (var session in sessions.Take(15))
    {
        var plate = session.PlateNumber ?? "-";

        Console.WriteLine($"{session.PhoneNumber,-20} " +
                         $"{(session.IsAuthenticated ? "是" : "否"),-6} " +
                         $"{session.ReceivedMessages}/{session.SentMessages,-15} " +
                         $"{plate,-12} " +
                         $"{session.LastActiveTime,-20:yyyy-MM-dd HH:mm:ss}");
    }

    if (sessions.Count > 15)
    {
        Console.WriteLine($"... 还有 {sessions.Count - 15} 个终端未显示");
    }

    Console.WriteLine();
    Console.WriteLine("按任意键刷新, 按 Q 退出...");
}

static void ShowStatisticsSimple(JT808TcpServer server)
{
    var sessionManager = server.GetSessionManager();
    var sessions = sessionManager.GetAllSessions().ToList();
    var v2019Count = server.GetVersion2019Recorder().Count;

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 在线: {sessions.Count}, " +
                     $"已鉴权: {sessions.Count(s => s.IsAuthenticated)}, " +
                     $"2019设备记录: {v2019Count}");
}
