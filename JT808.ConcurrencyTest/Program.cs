using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using JT808.Protocol;

// 注册GBK编码提供程序 (.NET 9需要)
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

Console.WriteLine("=".PadRight(70, '='));
Console.WriteLine("JT808-2013 并发性能测试工具 (配套 808ServerTo2013)");
Console.WriteLine("=".PadRight(70, '='));
Console.WriteLine();

Console.Write("服务器地址 (默认: 127.0.0.1): ");
var host = Console.ReadLine();
if (string.IsNullOrWhiteSpace(host))
    host = "127.0.0.1";

Console.Write("服务器端口 (默认: 8808): ");
var portStr = Console.ReadLine();
if (!int.TryParse(portStr, out var port))
    port = 8808;

Console.Write("并发客户端数量 (默认: 100): ");
var clientCountStr = Console.ReadLine();
if (!int.TryParse(clientCountStr, out var clientCount))
    clientCount = 100;

Console.Write("每个客户端发送消息数 (默认: 10): ");
var messageCountStr = Console.ReadLine();
if (!int.TryParse(messageCountStr, out var messageCount))
    messageCount = 10;

// 默认 2013 (服务器只解析 2013); 选 y 测试 2019 路由 (会被记录)
Console.Write("使用2019版本协议? (y/N): ");
var versionInput = Console.ReadLine();
bool is2019 = !string.IsNullOrWhiteSpace(versionInput) && versionInput.Equals("y", StringComparison.OrdinalIgnoreCase);
if (is2019)
{
    Console.WriteLine("⚠ 已选 2019 协议. 服务器只会把客户端记录到 Version2019Devices.ini, 不解析消息体.");
}

Console.WriteLine();
Console.WriteLine($"测试配置:");
Console.WriteLine($"  服务器: {host}:{port}");
Console.WriteLine($"  并发数: {clientCount} 个客户端");
Console.WriteLine($"  消息数: {messageCount} 条/客户端");
Console.WriteLine($"  协议版本: {(is2019 ? "2019" : "2013")}");
Console.WriteLine($"  总消息数: {clientCount * messageCount} 条");
Console.WriteLine();
Console.Write("按Enter开始测试...");
Console.ReadLine();

var stats = new TestStatistics();
var clients = new List<TestClient>();
var tasks = new List<Task>();

Console.WriteLine("\n开始测试...");
var stopwatch = Stopwatch.StartNew();

// 创建并启动所有客户端
for (int i = 0; i < clientCount; i++)
{
    var phoneNumber = $"1380013{i:D4}"; // 生成不同的手机号
    var client = new TestClient(host, port, phoneNumber, messageCount, is2019, stats);
    clients.Add(client);

    var task = Task.Run(async () => await client.RunTest());
    tasks.Add(task);

    // 避免同时连接导致资源耗尽,分批启动
    if ((i + 1) % 50 == 0)
    {
        await Task.Delay(100);
        Console.WriteLine($"已启动 {i + 1}/{clientCount} 个客户端...");
    }
}

Console.WriteLine($"所有客户端已启动,等待完成...");

// 等待所有测试完成
await Task.WhenAll(tasks);

stopwatch.Stop();

// 显示测试结果
Console.WriteLine();
Console.WriteLine("=".PadRight(70, '='));
Console.WriteLine("测试结果");
Console.WriteLine("=".PadRight(70, '='));
Console.WriteLine();
Console.WriteLine($"总耗时: {stopwatch.Elapsed.TotalSeconds:F2} 秒");
Console.WriteLine($"成功连接: {stats.SuccessfulConnections}/{clientCount}");
Console.WriteLine($"失败连接: {stats.FailedConnections}/{clientCount}");
Console.WriteLine($"成功率: {(stats.SuccessfulConnections * 100.0 / clientCount):F2}%");
Console.WriteLine();
Console.WriteLine($"发送消息数: {stats.MessagesSent}");
Console.WriteLine($"接收应答数: {stats.MessagesReceived}");
Console.WriteLine($"应答率: {(stats.MessagesReceived * 100.0 / stats.MessagesSent):F2}%");
Console.WriteLine();
Console.WriteLine($"吞吐量: {(stats.MessagesSent / stopwatch.Elapsed.TotalSeconds):F2} 消息/秒");
Console.WriteLine($"平均延迟: {stats.GetAverageLatency():F2} ms");
Console.WriteLine($"最小延迟: {stats.MinLatency:F2} ms");
Console.WriteLine($"最大延迟: {stats.MaxLatency:F2} ms");
Console.WriteLine();

if (stats.Errors.Count > 0)
{
    Console.WriteLine($"错误统计 (前10条):");
    foreach (var error in stats.Errors.Take(10))
    {
        Console.WriteLine($"  - {error}");
    }
    if (stats.Errors.Count > 10)
    {
        Console.WriteLine($"  ... 还有 {stats.Errors.Count - 10} 个错误");
    }
}

Console.WriteLine();
Console.WriteLine("测试完成!");

// 测试客户端类
class TestClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _phoneNumber;
    private readonly int _messageCount;
    private readonly bool _is2019;
    private readonly TestStatistics _stats;
    private TcpClient? _client;

    public TestClient(string host, int port, string phoneNumber, int messageCount, bool is2019, TestStatistics stats)
    {
        _host = host;
        _port = port;
        _phoneNumber = phoneNumber;
        _messageCount = messageCount;
        _is2019 = is2019;
        _stats = stats;
    }

    public async Task RunTest()
    {
        try
        {
            // 连接服务器
            _client = new TcpClient();
            await _client.ConnectAsync(_host, _port);
            _stats.IncrementSuccessfulConnections();

            var stream = _client.GetStream();
            string? authCode = null;

            // 1. 终端注册
            var registerBody = BuildRegisterBody();
            var registerMsg = JT808Encoder.Encode(JT808MessageId.TerminalRegister, _phoneNumber, registerBody, _is2019);

            var sw = Stopwatch.StartNew();
            await stream.WriteAsync(registerMsg, 0, registerMsg.Length);
            _stats.IncrementMessagesSent();

            var response = await ReceiveMessageAsync(stream);
            sw.Stop();

            if (response != null)
            {
                _stats.IncrementMessagesReceived();
                _stats.AddLatency(sw.Elapsed.TotalMilliseconds);

                var msg = JT808Decoder.Decode(response);
                if (msg != null && msg.Header.MessageId == JT808MessageId.TerminalRegisterResponse && msg.Body.Length > 1)
                {
                    authCode = Encoding.ASCII.GetString(msg.Body, 1, msg.Body.Length - 1);
                }
            }

            // 2. 终端鉴权
            if (!string.IsNullOrEmpty(authCode))
            {
                var authBody = BuildAuthenticationBody(authCode);
                var authMsg = JT808Encoder.Encode(JT808MessageId.TerminalAuthentication, _phoneNumber, authBody, _is2019);

                sw.Restart();
                await stream.WriteAsync(authMsg, 0, authMsg.Length);
                _stats.IncrementMessagesSent();

                response = await ReceiveMessageAsync(stream);
                sw.Stop();

                if (response != null)
                {
                    _stats.IncrementMessagesReceived();
                    _stats.AddLatency(sw.Elapsed.TotalMilliseconds);
                }
            }

            // 3. 发送位置和心跳
            for (int i = 0; i < _messageCount; i++)
            {
                // 交替发送位置和心跳
                byte[] msg;
                if (i % 2 == 0)
                {
                    var locationBody = BuildLocationBody(i);
                    msg = JT808Encoder.Encode(JT808MessageId.LocationReport, _phoneNumber, locationBody, _is2019);
                }
                else
                {
                    msg = JT808Encoder.Encode(JT808MessageId.TerminalHeartbeat, _phoneNumber, Array.Empty<byte>(), _is2019);
                }

                sw.Restart();
                await stream.WriteAsync(msg, 0, msg.Length);
                _stats.IncrementMessagesSent();

                response = await ReceiveMessageAsync(stream);
                sw.Stop();

                if (response != null)
                {
                    _stats.IncrementMessagesReceived();
                    _stats.AddLatency(sw.Elapsed.TotalMilliseconds);
                }

                await Task.Delay(10); // 避免过快发送
            }
        }
        catch (Exception ex)
        {
            _stats.IncrementFailedConnections();
            _stats.AddError($"Phone {_phoneNumber}: {ex.Message}");
        }
        finally
        {
            _client?.Close();
        }
    }

    private byte[] BuildRegisterBody()
    {
        var body = new List<byte>();
        body.Add(0x00); body.Add(0x01); // 省域ID
        body.Add(0x00); body.Add(0x00); // 市县域ID

        if (_is2019)
        {
            body.AddRange(Encoding.ASCII.GetBytes("TESTMFR2019"));
            body.AddRange(Encoding.ASCII.GetBytes("JT808-2019-CONCURRENT-TEST".PadRight(30, '\0')));
            body.AddRange(Encoding.ASCII.GetBytes(_phoneNumber.PadRight(30, '\0')));
        }
        else
        {
            body.AddRange(Encoding.ASCII.GetBytes("TEST "));
            body.AddRange(Encoding.ASCII.GetBytes("JT808-TEST".PadRight(20, '\0')));
            body.AddRange(Encoding.ASCII.GetBytes("1234567"));
        }

        body.Add(0x01); // 车牌颜色
        body.AddRange(Encoding.GetEncoding("GBK").GetBytes("测试" + _phoneNumber.Substring(7)));
        return body.ToArray();
    }

    private byte[] BuildAuthenticationBody(string authCode)
    {
        var body = new List<byte>();
        if (_is2019)
        {
            var authBytes = Encoding.ASCII.GetBytes(authCode);
            body.Add((byte)authBytes.Length);
            body.AddRange(authBytes);
            body.AddRange(Encoding.ASCII.GetBytes("123456789012345"));
            body.AddRange(Encoding.ASCII.GetBytes("V1.0".PadRight(20, '\0')));
        }
        else
        {
            body.AddRange(Encoding.ASCII.GetBytes(authCode));
        }
        return body.ToArray();
    }

    private byte[] BuildLocationBody(int index)
    {
        var body = new List<byte>();
        body.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // 报警
        body.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x03 }); // 状态

        uint lat = (uint)((39.916527 + index * 0.0001) * 1000000);
        body.Add((byte)(lat >> 24)); body.Add((byte)(lat >> 16));
        body.Add((byte)(lat >> 8)); body.Add((byte)lat);

        uint lon = (uint)((116.397128 + index * 0.0001) * 1000000);
        body.Add((byte)(lon >> 24)); body.Add((byte)(lon >> 16));
        body.Add((byte)(lon >> 8)); body.Add((byte)lon);

        body.Add(0x00); body.Add(0x32); // 高程
        body.Add(0x02); body.Add(0x58); // 速度
        body.Add(0x00); body.Add(0x5A); // 方向

        var now = DateTime.Now;
        body.Add(ToBCD(now.Year % 100)); body.Add(ToBCD(now.Month));
        body.Add(ToBCD(now.Day)); body.Add(ToBCD(now.Hour));
        body.Add(ToBCD(now.Minute)); body.Add(ToBCD(now.Second));

        return body.ToArray();
    }

    private static byte ToBCD(int value) => (byte)(((value / 10) << 4) | (value % 10));

    private async Task<byte[]?> ReceiveMessageAsync(NetworkStream stream)
    {
        try
        {
            var buffer = new byte[2048];
            var cts = new CancellationTokenSource(2000); // 2秒超时
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);

            if (bytesRead > 0)
            {
                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                return data;
            }
        }
        catch { }
        return null;
    }
}

// 统计信息类
class TestStatistics
{
    private int _successfulConnections = 0;
    private int _failedConnections = 0;
    private int _messagesSent = 0;
    private int _messagesReceived = 0;
    private readonly List<double> _latencies = new();
    private readonly List<string> _errors = new();
    private readonly object _lock = new();

    public int SuccessfulConnections => _successfulConnections;
    public int FailedConnections => _failedConnections;
    public int MessagesSent => _messagesSent;
    public int MessagesReceived => _messagesReceived;
    public double MinLatency => _latencies.Count > 0 ? _latencies.Min() : 0;
    public double MaxLatency => _latencies.Count > 0 ? _latencies.Max() : 0;
    public List<string> Errors => _errors;

    public void IncrementSuccessfulConnections() => Interlocked.Increment(ref _successfulConnections);
    public void IncrementFailedConnections() => Interlocked.Increment(ref _failedConnections);
    public void IncrementMessagesSent() => Interlocked.Increment(ref _messagesSent);
    public void IncrementMessagesReceived() => Interlocked.Increment(ref _messagesReceived);

    public void AddLatency(double latency)
    {
        lock (_lock)
        {
            _latencies.Add(latency);
        }
    }

    public void AddError(string error)
    {
        lock (_lock)
        {
            _errors.Add(error);
        }
    }

    public double GetAverageLatency()
    {
        lock (_lock)
        {
            return _latencies.Count > 0 ? _latencies.Average() : 0;
        }
    }
}
