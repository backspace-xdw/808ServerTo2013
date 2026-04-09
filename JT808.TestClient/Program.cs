using System.Net.Sockets;
using System.Text;
using JT808.Protocol;

// 注册GBK编码提供程序 (.NET 9需要)
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine("JT808-2013 测试客户端 (配套 808ServerTo2013)");
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine();

Console.Write("服务器地址 (默认: 127.0.0.1): ");
var host = Console.ReadLine();
if (string.IsNullOrWhiteSpace(host))
    host = "127.0.0.1";

Console.Write("服务器端口 (默认: 8808): ");
var portStr = Console.ReadLine();
if (!int.TryParse(portStr, out var port))
    port = 8808;

Console.Write("手机号 (默认: 13800138000): ");
var phoneNumber = Console.ReadLine();
if (string.IsNullOrWhiteSpace(phoneNumber))
    phoneNumber = "13800138000";

// 默认 2013 (服务器只解析 2013); 输入 y/Y 可以测 2019 路由(只会被记录, 不解析)
Console.Write("使用2019版本协议? (y/N): ");
var versionInput = Console.ReadLine();
bool is2019 = !string.IsNullOrWhiteSpace(versionInput) && versionInput.Equals("y", StringComparison.OrdinalIgnoreCase);
if (is2019)
{
    Console.WriteLine("⚠ 你选了 2019 协议. 服务器会把客户端记录到 Version2019Devices.ini, 不会解析消息体, 也不会回应.");
}

Console.WriteLine();
Console.WriteLine($"连接到 {host}:{port}, 手机号: {phoneNumber}, 协议版本: {(is2019 ? "2019" : "2013")}");
Console.WriteLine();

try
{
    using var client = new TcpClient();
    client.Connect(host, port);
    Console.WriteLine("连接成功!");

    var stream = client.GetStream();
    string? authCode = null;

    // 1. 发送注册消息
    Console.WriteLine("\n[1] 发送终端注册...");
    var registerBody = BuildRegisterBody(is2019);
    var registerMsg = JT808Encoder.Encode(JT808MessageId.TerminalRegister, phoneNumber, registerBody, is2019);
    stream.Write(registerMsg, 0, registerMsg.Length);
    Console.WriteLine($"发送注册消息 ({registerMsg.Length} 字节)");

    // 接收注册应答
    var response = ReceiveMessage(stream);
    if (response != null)
    {
        var msg = JT808Decoder.Decode(response);
        if (msg != null && msg.Header.MessageId == JT808MessageId.TerminalRegisterResponse)
        {
            var result = msg.Body[0];
            if (result == 0 && msg.Body.Length > 1)
            {
                authCode = Encoding.ASCII.GetString(msg.Body, 1, msg.Body.Length - 1);
                Console.WriteLine($"注册成功! 鉴权码: {authCode}");
            }
            else
            {
                Console.WriteLine($"注册失败: {result}");
                return;
            }
        }
    }

    // 2. 发送鉴权消息
    if (!string.IsNullOrEmpty(authCode))
    {
        Console.WriteLine("\n[2] 发送终端鉴权...");
        var authBody = BuildAuthenticationBody(authCode, is2019);
        var authMsg = JT808Encoder.Encode(JT808MessageId.TerminalAuthentication, phoneNumber, authBody, is2019);
        stream.Write(authMsg, 0, authMsg.Length);
        Console.WriteLine($"发送鉴权消息 ({authMsg.Length} 字节)");

        response = ReceiveMessage(stream);
        if (response != null)
        {
            Console.WriteLine("鉴权成功!");
        }
    }

    // 3. 发送位置上报
    Console.WriteLine("\n[3] 发送位置上报 (包含附加信息)...");
    for (int i = 0; i < 5; i++)
    {
        var locationBody = BuildLocationBody(i, is2019);
        var locationMsg = JT808Encoder.Encode(JT808MessageId.LocationReport, phoneNumber, locationBody, is2019);
        stream.Write(locationMsg, 0, locationMsg.Length);
        Console.WriteLine($"发送位置 #{i + 1}: 经度={116.397128 + i * 0.001:F6}, 纬度={39.916527 + i * 0.001:F6}, " +
                         $"速度={60 + i * 5:F1}km/h");

        response = ReceiveMessage(stream);
        Thread.Sleep(1000);
    }

    // 4. 发送心跳
    Console.WriteLine("\n[4] 发送心跳...");
    for (int i = 0; i < 3; i++)
    {
        var heartbeatMsg = JT808Encoder.Encode(JT808MessageId.TerminalHeartbeat, phoneNumber, Array.Empty<byte>(), is2019);
        stream.Write(heartbeatMsg, 0, heartbeatMsg.Length);
        Console.WriteLine($"发送心跳 #{i + 1}");

        response = ReceiveMessage(stream);
        Thread.Sleep(2000);
    }

    Console.WriteLine("\n测试完成!");
}
catch (Exception ex)
{
    Console.WriteLine($"错误: {ex.Message}");
}

static byte[] BuildRegisterBody(bool is2019)
{
    var body = new List<byte>();

    // 省域ID (2字节) - 北京
    body.Add(0x00);
    body.Add(0x01);

    // 市县域ID (2字节)
    body.Add(0x00);
    body.Add(0x00);

    if (is2019)
    {
        // 制造商ID (11字节, 2019版本)
        body.AddRange(Encoding.ASCII.GetBytes("TESTMFR2019"));

        // 终端型号 (30字节, 2019版本)
        var model = "JT808-2019-TEST-MODEL-V1.0";
        body.AddRange(Encoding.ASCII.GetBytes(model.PadRight(30, '\0')));

        // 终端ID (30字节, 2019版本)
        var terminalId = "2019TEST123456789012345678";
        body.AddRange(Encoding.ASCII.GetBytes(terminalId.PadRight(30, '\0')));
    }
    else
    {
        // 制造商ID (5字节, 2013版本)
        body.AddRange(Encoding.ASCII.GetBytes("TEST "));

        // 终端型号 (20字节, 2013版本)
        var model = "JT808-2013-V1.0     ";
        body.AddRange(Encoding.ASCII.GetBytes(model));

        // 终端ID (7字节, 2013版本)
        body.AddRange(Encoding.ASCII.GetBytes("1234567"));
    }

    // 车牌颜色 (1字节) - 1:蓝色
    body.Add(0x01);

    // 车牌号
    body.AddRange(Encoding.GetEncoding("GBK").GetBytes("京A12345"));

    return body.ToArray();
}

static byte[] BuildAuthenticationBody(string authCode, bool is2019)
{
    var body = new List<byte>();

    if (is2019)
    {
        // 鉴权码长度 (2019新增)
        var authBytes = Encoding.ASCII.GetBytes(authCode);
        body.Add((byte)authBytes.Length);
        body.AddRange(authBytes);

        // IMEI (15字节)
        body.AddRange(Encoding.ASCII.GetBytes("123456789012345"));

        // 软件版本号 (20字节)
        body.AddRange(Encoding.ASCII.GetBytes("V2019.01.001".PadRight(20, '\0')));
    }
    else
    {
        // 2013版本只有鉴权码
        body.AddRange(Encoding.ASCII.GetBytes(authCode));
    }

    return body.ToArray();
}

static byte[] BuildLocationBody(int index, bool is2019)
{
    var body = new List<byte>();

    // 报警标志 (4字节)
    body.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });

    // 状态 (4字节): bit0=ACC开, bit1=已定位
    body.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x03 }); // ACC开+定位

    // 纬度 (4字节) - 39.916527
    uint lat = (uint)((39.916527 + index * 0.001) * 1000000);
    body.Add((byte)(lat >> 24));
    body.Add((byte)(lat >> 16));
    body.Add((byte)(lat >> 8));
    body.Add((byte)lat);

    // 经度 (4字节) - 116.397128
    uint lon = (uint)((116.397128 + index * 0.001) * 1000000);
    body.Add((byte)(lon >> 24));
    body.Add((byte)(lon >> 16));
    body.Add((byte)(lon >> 8));
    body.Add((byte)lon);

    // 高程 (2字节) - 50米
    body.Add(0x00);
    body.Add(0x32);

    // 速度 (2字节) - (60+index*5)km/h * 10
    ushort speed = (ushort)((60 + index * 5) * 10);
    body.Add((byte)(speed >> 8));
    body.Add((byte)speed);

    // 方向 (2字节) - 90度
    body.Add(0x00);
    body.Add(0x5A);

    // 时间 (6字节BCD) - 当前时间
    var now = DateTime.Now;
    body.Add(ToBCD(now.Year % 100));
    body.Add(ToBCD(now.Month));
    body.Add(ToBCD(now.Day));
    body.Add(ToBCD(now.Hour));
    body.Add(ToBCD(now.Minute));
    body.Add(ToBCD(now.Second));

    // 2019版本: 添加位置附加信息
    if (is2019)
    {
        // 里程 (ID=0x01)
        body.Add(0x01); // ID
        body.Add(0x04); // 长度
        uint mileage = (uint)((1000 + index * 10) * 10); // 1/10km
        body.Add((byte)(mileage >> 24));
        body.Add((byte)(mileage >> 16));
        body.Add((byte)(mileage >> 8));
        body.Add((byte)mileage);

        // 油量 (ID=0x02)
        body.Add(0x02); // ID
        body.Add(0x02); // 长度
        ushort fuel = (ushort)((50 - index) * 10); // 1/10L
        body.Add((byte)(fuel >> 8));
        body.Add((byte)fuel);

        // 无线信号强度 (ID=0x30, 2019新增)
        body.Add(0x30); // ID
        body.Add(0x01); // 长度
        body.Add((byte)(75 + index)); // 信号强度75-79
    }

    return body.ToArray();
}

static byte ToBCD(int value)
{
    return (byte)(((value / 10) << 4) | (value % 10));
}

static byte[]? ReceiveMessage(NetworkStream stream, int timeout = 5000)
{
    try
    {
        stream.ReadTimeout = timeout;
        var buffer = new byte[2048];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);
        if (bytesRead > 0)
        {
            var data = new byte[bytesRead];
            Array.Copy(buffer, data, bytesRead);
            Console.WriteLine($"  收到应答 ({bytesRead} 字节)");
            return data;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  接收消息超时或错误: {ex.Message}");
    }
    return null;
}
