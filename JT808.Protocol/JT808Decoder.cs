using System.Text;

namespace JT808.Protocol;

/// <summary>
/// JT808-2019协议解码器
/// </summary>
public static class JT808Decoder
{
    // 缓存 GBK 编码 (避免每次 ReadString 都查 codepage 表)
    private static readonly Encoding Gbk;

    static JT808Decoder()
    {
        // 注册GBK编码提供程序 (.NET 9需要)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding("GBK");
    }
    /// <summary>
    /// 解码消息
    /// </summary>
    public static JT808Message? Decode(byte[] data)
    {
        if (data == null || data.Length < 12)
            return null;

        // 检查标识位
        if (data[0] != JT808Constants.FLAG || data[^1] != JT808Constants.FLAG)
            return null;

        // 去除标识位 (一次分配; unescape 走原地, 不再二次分配)
        int innerLen = data.Length - 2;
        var buffer = new byte[innerLen];
        Buffer.BlockCopy(data, 1, buffer, 0, innerLen);

        // 原地反转义, 返回有效长度
        int validLen = UnescapeInPlace(buffer, innerLen);

        // 校验码 (XOR 0..validLen-2)
        if (!VerifyCheckCode(buffer, validLen))
            return null;

        var message = new JT808Message();
        int offset = 0;

        // 解析消息头
        message.Header = DecodeHeader(buffer, ref offset);

        // 解析消息体
        int bodyLength = message.Header.BodyLength;
        if (bodyLength > 0 && offset + bodyLength <= validLen - 1)
        {
            message.Body = new byte[bodyLength];
            Buffer.BlockCopy(buffer, offset, message.Body, 0, bodyLength);
            offset += bodyLength;
        }

        // 校验码 (最后一字节)
        message.CheckCode = buffer[validLen - 1];

        return message;
    }

    /// <summary>
    /// 解析消息头
    /// </summary>
    private static JT808Header DecodeHeader(byte[] buffer, ref int offset)
    {
        var header = new JT808Header();

        // 消息ID (2字节)
        header.MessageId = ReadUInt16(buffer, ref offset);

        // 消息体属性 (2字节)
        header.MessageBodyProperty = ReadUInt16(buffer, ref offset);

        // 判断版本
        bool is2019 = header.Is2019Version;
        int phoneLength = is2019 ? 10 : 6;

        // 协议版本号 (2019版本才有, 1字节)
        if (is2019)
        {
            header.ProtocolVersion = buffer[offset++];
        }

        // 终端手机号 (2013:6字节, 2019:10字节 BCD码)
        header.PhoneNumber = ReadBCD(buffer, ref offset, phoneLength);

        // 消息流水号 (2字节)
        header.SerialNumber = ReadUInt16(buffer, ref offset);

        // 消息包封装项 (分包时才有)
        if (header.IsPackage)
        {
            header.Package = new JT808PackageInfo
            {
                TotalPackage = ReadUInt16(buffer, ref offset),
                PackageIndex = ReadUInt16(buffer, ref offset)
            };
        }

        return header;
    }

    /// <summary>
    /// 解析位置信息 (2019版本)
    /// </summary>
    public static LocationInfo? DecodeLocationInfo(byte[] body)
    {
        if (body == null || body.Length < 28)
            return null;

        int offset = 0;
        var location = new LocationInfo
        {
            AlarmFlag = ReadUInt32(body, ref offset),
            Status = ReadUInt32(body, ref offset),
            Latitude = ReadUInt32(body, ref offset),
            Longitude = ReadUInt32(body, ref offset),
            Altitude = ReadUInt16(body, ref offset),
            Speed = ReadUInt16(body, ref offset),
            Direction = ReadUInt16(body, ref offset)
        };

        // BCD码时间 YY-MM-DD-hh-mm-ss (6字节)
        var timeStr = ReadBCD(body, ref offset, 6);
        if (timeStr.Length == 12)
        {
            try
            {
                int year = 2000 + int.Parse(timeStr.Substring(0, 2));
                int month = int.Parse(timeStr.Substring(2, 2));
                int day = int.Parse(timeStr.Substring(4, 2));
                int hour = int.Parse(timeStr.Substring(6, 2));
                int minute = int.Parse(timeStr.Substring(8, 2));
                int second = int.Parse(timeStr.Substring(10, 2));
                location.GpsTime = new DateTime(year, month, day, hour, minute, second);
            }
            catch
            {
                // 时间字段损坏: 标记为 MinValue 让下游能识别失真,
                // 不再静默回退到 DateTime.Now (会让坏数据看起来正常)
                location.GpsTime = DateTime.MinValue;
            }
        }

        // 解析位置附加信息 (2019版本扩展)
        while (offset < body.Length)
        {
            var additionalInfo = new LocationAdditionalInfo
            {
                Id = body[offset++]
            };

            if (offset >= body.Length)
                break;

            additionalInfo.Length = body[offset++];

            if (offset + additionalInfo.Length > body.Length)
                break;

            additionalInfo.Content = new byte[additionalInfo.Length];
            Array.Copy(body, offset, additionalInfo.Content, 0, additionalInfo.Length);
            offset += additionalInfo.Length;

            location.AdditionalInfoList.Add(additionalInfo);
        }

        return location;
    }

    /// <summary>
    /// 解析终端注册信息 (JT/T 808-2013 版本)
    /// 字段长度: 省域(2) + 市县(2) + 制造商ID(5) + 终端型号(20) + 终端ID(7) + 车牌颜色(1) + 车牌号(可变)
    /// 最小长度: 2+2+5+20+7+1 = 37 字节
    /// </summary>
    public static TerminalRegisterInfo? DecodeRegisterInfo(byte[] body)
    {
        if (body == null || body.Length < 37)
            return null;

        int offset = 0;
        var info = new TerminalRegisterInfo
        {
            ProvinceId = ReadUInt16(body, ref offset),
            CityId = ReadUInt16(body, ref offset),
            ManufacturerId = ReadString(body, ref offset, 5),    // 2013: 5 字节
            TerminalModel = ReadString(body, ref offset, 20),    // 2013: 20 字节
            TerminalId = ReadString(body, ref offset, 7),        // 2013: 7 字节
            PlateColor = body[offset++]
        };

        // 车牌号(可变长度, 到消息体结束)
        int plateLength = body.Length - offset;
        if (plateLength > 0)
        {
            info.PlateNumber = ReadString(body, ref offset, plateLength);
        }

        return info;
    }

    /// <summary>
    /// 解析终端鉴权信息 (JT/T 808-2013 版本)
    /// 整个消息体就是鉴权码字符串 (无长度前缀, 无 IMEI, 无软件版本号)
    /// </summary>
    public static TerminalAuthenticationInfo? DecodeAuthenticationInfo(byte[] body)
    {
        if (body == null || body.Length < 1)
            return null;

        var info = new TerminalAuthenticationInfo
        {
            AuthCodeLength = (byte)Math.Min(body.Length, 255),
            AuthCode = Encoding.ASCII.GetString(body, 0, body.Length),
            IMEI = string.Empty,            // 2013 无此字段
            SoftwareVersion = string.Empty, // 2013 无此字段
        };

        return info;
    }

    /// <summary>
    /// 反转义 — 原地写入 (写指针 ≤ 读指针, 因为 unescape 只会缩短不会扩张)
    /// 返回新的有效长度, 调用方根据 length 取前 N 字节即可
    /// </summary>
    private static int UnescapeInPlace(byte[] data, int length)
    {
        int w = 0;
        for (int r = 0; r < length; r++)
        {
            byte b = data[r];
            if (b == JT808Constants.ESCAPE && r + 1 < length)
            {
                byte next = data[r + 1];
                if (next == JT808Constants.ESCAPE_7E)
                {
                    data[w++] = JT808Constants.FLAG;
                    r++;
                }
                else if (next == JT808Constants.ESCAPE_7D)
                {
                    data[w++] = JT808Constants.ESCAPE;
                    r++;
                }
                else
                {
                    data[w++] = b;
                }
            }
            else
            {
                data[w++] = b;
            }
        }
        return w;
    }

    /// <summary>
    /// 校验码验证 (XOR 0..length-2 与 data[length-1] 比较)
    /// </summary>
    private static bool VerifyCheckCode(byte[] data, int length)
    {
        if (length < 2) return false;
        byte checkCode = data[length - 1];
        byte calculated = 0;
        for (int i = 0; i < length - 1; i++)
        {
            calculated ^= data[i];
        }
        return checkCode == calculated;
    }

    private static ushort ReadUInt16(byte[] buffer, ref int offset)
    {
        ushort value = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        offset += 2;
        return value;
    }

    private static uint ReadUInt32(byte[] buffer, ref int offset)
    {
        uint value = (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) |
                           (buffer[offset + 2] << 8) | buffer[offset + 3]);
        offset += 4;
        return value;
    }

    private static string ReadBCD(byte[] buffer, ref int offset, int length)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < length; i++)
        {
            byte b = buffer[offset + i];
            sb.Append((b >> 4).ToString("X"));
            sb.Append((b & 0x0F).ToString("X"));
        }
        offset += length;
        return sb.ToString();
    }

    private static string ReadString(byte[] buffer, ref int offset, int length)
    {
        var str = Gbk.GetString(buffer, offset, length).TrimEnd('\0');
        offset += length;
        return str;
    }

    /// <summary>
    /// 解析多媒体数据上传 (0x0801)
    /// </summary>
    public static MultimediaDataUpload? DecodeMultimediaDataUpload(byte[] body)
    {
        // 最小长度: 4(ID) + 1(类型) + 1(格式) + 1(事件) + 1(通道) + 28(位置) = 36字节
        if (body == null || body.Length < 36)
            return null;

        int offset = 0;
        var multimedia = new MultimediaDataUpload
        {
            MultimediaId = ReadUInt32(body, ref offset),
            Type = (MultimediaType)body[offset++],
            Format = (MultimediaFormat)body[offset++],
            Event = (MultimediaEvent)body[offset++],
            ChannelId = body[offset++]
        };

        // 解析位置信息 (28字节基本位置信息，不含附加信息)
        if (offset + 28 <= body.Length)
        {
            var locationData = new byte[28];
            Array.Copy(body, offset, locationData, 0, 28);
            multimedia.Location = DecodeLocationInfoBasic(locationData);
            offset += 28;
        }

        // 剩余为多媒体数据
        if (offset < body.Length)
        {
            int dataLength = body.Length - offset;
            multimedia.Data = new byte[dataLength];
            Array.Copy(body, offset, multimedia.Data, 0, dataLength);
        }

        return multimedia;
    }

    /// <summary>
    /// 解析基本位置信息 (28字节，不含附加信息)
    /// </summary>
    private static LocationInfo DecodeLocationInfoBasic(byte[] data)
    {
        int offset = 0;
        var location = new LocationInfo
        {
            AlarmFlag = ReadUInt32(data, ref offset),
            Status = ReadUInt32(data, ref offset),
            Latitude = ReadUInt32(data, ref offset),
            Longitude = ReadUInt32(data, ref offset),
            Altitude = ReadUInt16(data, ref offset),
            Speed = ReadUInt16(data, ref offset),
            Direction = ReadUInt16(data, ref offset)
        };

        // BCD码时间 YY-MM-DD-hh-mm-ss (6字节)
        var timeStr = ReadBCD(data, ref offset, 6);
        if (timeStr.Length == 12)
        {
            try
            {
                int year = 2000 + int.Parse(timeStr.Substring(0, 2));
                int month = int.Parse(timeStr.Substring(2, 2));
                int day = int.Parse(timeStr.Substring(4, 2));
                int hour = int.Parse(timeStr.Substring(6, 2));
                int minute = int.Parse(timeStr.Substring(8, 2));
                int second = int.Parse(timeStr.Substring(10, 2));
                location.GpsTime = new DateTime(year, month, day, hour, minute, second);
            }
            catch
            {
                // 时间字段损坏: 标记为 MinValue 让下游能识别失真,
                // 不再静默回退到 DateTime.Now (会让坏数据看起来正常)
                location.GpsTime = DateTime.MinValue;
            }
        }

        return location;
    }
}
