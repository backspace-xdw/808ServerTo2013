using System.Text;

namespace JT808.Protocol;

/// <summary>
/// JT808-2019协议编码器
/// </summary>
public class JT808Encoder
{
    // 流水号: 用 int + Interlocked.Increment 保证多线程编码安全
    // 截断到 ushort 通过 (ushort) cast, 自然 wrap 65535 -> 0
    private static int _serialNumber = 0;

    /// <summary>
    /// 编码消息 (支持2019版本)
    /// </summary>
    /// <param name="messageId">消息ID</param>
    /// <param name="phoneNumber">手机号</param>
    /// <param name="body">消息体</param>
    /// <param name="is2019">是否2019版本</param>
    /// <param name="protocolVersion">协议版本号(仅2019版本)</param>
    public static byte[] Encode(ushort messageId, string phoneNumber, byte[] body,
        bool is2019 = false, byte protocolVersion = 0x01)
    {
        var buffer = new List<byte>();

        // 消息ID
        WriteUInt16(buffer, messageId);

        // 消息体属性
        ushort bodyProperty = (ushort)(body?.Length ?? 0);
        if (is2019)
        {
            bodyProperty |= MessageBodyProperty.VersionFlagMask; // 设置2019版本标志位
        }
        WriteUInt16(buffer, bodyProperty);

        // 协议版本号 (2019版本才有)
        if (is2019)
        {
            buffer.Add(protocolVersion);
        }

        // 终端手机号(BCD码, 2013:6字节, 2019:10字节)
        int phoneLength = is2019 ? 10 : 6;
        WriteBCD(buffer, phoneNumber, phoneLength);

        // 消息流水号
        ushort serial = GetNextSerialNumber();
        WriteUInt16(buffer, serial);

        // 消息体
        if (body != null && body.Length > 0)
        {
            buffer.AddRange(body);
        }

        // 校验码
        byte checkCode = CalculateCheckCode(buffer.ToArray());
        buffer.Add(checkCode);

        // 转义
        var escaped = Escape(buffer.ToArray());

        // 添加标识位
        var result = new List<byte> { JT808Constants.FLAG };
        result.AddRange(escaped);
        result.Add(JT808Constants.FLAG);

        return result.ToArray();
    }

    /// <summary>
    /// 平台通用应答
    /// </summary>
    public static byte[] EncodePlatformGeneralResponse(string phoneNumber, ushort responseSerialNumber,
        ushort responseMessageId, byte result, bool is2019 = false)
    {
        var body = new List<byte>();
        WriteUInt16(body, responseSerialNumber);
        WriteUInt16(body, responseMessageId);
        body.Add(result);

        return Encode(JT808MessageId.PlatformGeneralResponse, phoneNumber, body.ToArray(), is2019);
    }

    /// <summary>
    /// 终端注册应答 (2019版本)
    /// </summary>
    public static byte[] EncodeRegisterResponse(string phoneNumber, ushort responseSerialNumber,
        RegisterResult result, string authCode = "", bool is2019 = false)
    {
        var body = new List<byte>();
        WriteUInt16(body, responseSerialNumber);
        body.Add((byte)result);

        if (result == RegisterResult.Success && !string.IsNullOrEmpty(authCode))
        {
            // 2019版本: 鉴权码最长不超过50字节
            var authBytes = Encoding.ASCII.GetBytes(authCode);
            if (authBytes.Length > 50)
            {
                Array.Resize(ref authBytes, 50);
            }
            body.AddRange(authBytes);
        }

        return Encode(JT808MessageId.TerminalRegisterResponse, phoneNumber, body.ToArray(), is2019);
    }

    /// <summary>
    /// 位置信息查询
    /// </summary>
    public static byte[] EncodeLocationQuery(string phoneNumber, bool is2019 = false)
    {
        return Encode(JT808MessageId.LocationQuery, phoneNumber, Array.Empty<byte>(), is2019);
    }

    /// <summary>
    /// 文本信息下发
    /// </summary>
    public static byte[] EncodeTextMessage(string phoneNumber, string text,
        byte flag = 0x01, bool is2019 = false)
    {
        var body = new List<byte>();
        body.Add(flag); // 文本信息标志

        var textBytes = Encoding.GetEncoding("GBK").GetBytes(text);
        body.AddRange(textBytes);

        return Encode(JT808MessageId.TextMessageDownload, phoneNumber, body.ToArray(), is2019);
    }

    /// <summary>
    /// 设置终端参数
    /// </summary>
    public static byte[] EncodeSetTerminalParameters(string phoneNumber,
        List<TerminalParameter> parameters, bool is2019 = false)
    {
        var body = new List<byte>();
        body.Add((byte)parameters.Count); // 参数总数

        foreach (var param in parameters)
        {
            // 参数ID (2019为4字节, 2013为1字节)
            if (is2019)
            {
                WriteUInt32(body, param.Id);
            }
            else
            {
                body.Add((byte)param.Id);
            }

            body.Add(param.Length);
            body.AddRange(param.Value);
        }

        return Encode(JT808MessageId.SetTerminalParameters, phoneNumber, body.ToArray(), is2019);
    }

    /// <summary>
    /// 多媒体数据上传应答 (0x8800)
    /// </summary>
    /// <param name="phoneNumber">手机号</param>
    /// <param name="multimediaId">多媒体ID</param>
    /// <param name="retransmitPackageIds">需要重传的包ID列表（为空表示成功无需重传）</param>
    /// <param name="is2019">是否2019版本</param>
    public static byte[] EncodeMultimediaDataUploadResponse(string phoneNumber, uint multimediaId,
        List<ushort>? retransmitPackageIds = null, bool is2019 = false)
    {
        var body = new List<byte>();

        // 多媒体ID (4字节)
        WriteUInt32(body, multimediaId);

        // 重传包数量
        byte retransmitCount = (byte)(retransmitPackageIds?.Count ?? 0);
        body.Add(retransmitCount);

        // 重传包ID列表
        if (retransmitPackageIds != null && retransmitPackageIds.Count > 0)
        {
            foreach (var packageId in retransmitPackageIds)
            {
                WriteUInt16(body, packageId);
            }
        }

        return Encode(JT808MessageId.MultimediaDataUploadResponse, phoneNumber, body.ToArray(), is2019);
    }

    /// <summary>
    /// 转义处理
    /// </summary>
    private static byte[] Escape(byte[] data)
    {
        var result = new List<byte>();
        foreach (byte b in data)
        {
            if (b == JT808Constants.FLAG)
            {
                result.Add(JT808Constants.ESCAPE);
                result.Add(JT808Constants.ESCAPE_7E);
            }
            else if (b == JT808Constants.ESCAPE)
            {
                result.Add(JT808Constants.ESCAPE);
                result.Add(JT808Constants.ESCAPE_7D);
            }
            else
            {
                result.Add(b);
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// 计算校验码
    /// </summary>
    private static byte CalculateCheckCode(byte[] data)
    {
        byte checkCode = 0;
        foreach (byte b in data)
        {
            checkCode ^= b;
        }
        return checkCode;
    }

    private static void WriteUInt16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)(value & 0xFF));
    }

    private static void WriteUInt32(List<byte> buffer, uint value)
    {
        buffer.Add((byte)(value >> 24));
        buffer.Add((byte)(value >> 16));
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)(value & 0xFF));
    }

    private static void WriteBCD(List<byte> buffer, string value, int length)
    {
        // 填充到指定长度
        value = value.PadLeft(length * 2, '0');
        if (value.Length > length * 2)
            value = value.Substring(value.Length - length * 2);

        for (int i = 0; i < length; i++)
        {
            string hex = value.Substring(i * 2, 2);
            buffer.Add(Convert.ToByte(hex, 16));
        }
    }

    private static ushort GetNextSerialNumber()
    {
        // Interlocked.Increment 原子加 1, 强制转 ushort 自然 wrap
        return (ushort)Interlocked.Increment(ref _serialNumber);
    }
}
