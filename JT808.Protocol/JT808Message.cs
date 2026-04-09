namespace JT808.Protocol;

/// <summary>
/// JT808-2019消息头
/// </summary>
public class JT808Header
{
    /// <summary>
    /// 消息ID
    /// </summary>
    public ushort MessageId { get; set; }

    /// <summary>
    /// 消息体属性
    /// </summary>
    public ushort MessageBodyProperty { get; set; }

    /// <summary>
    /// 协议版本号 (2019新增, 1字节)
    /// 仅当版本标识位为1时存在
    /// </summary>
    public byte ProtocolVersion { get; set; } = 0x01; // 默认2019版本

    /// <summary>
    /// 终端手机号(BCD码)
    /// 2013: 6字节
    /// 2019: 10字节
    /// </summary>
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>
    /// 消息流水号
    /// </summary>
    public ushort SerialNumber { get; set; }

    /// <summary>
    /// 消息包封装项
    /// </summary>
    public JT808PackageInfo? Package { get; set; }

    /// <summary>
    /// 获取消息体长度
    /// </summary>
    public int BodyLength => MessageBodyProperty & Protocol.MessageBodyProperty.LengthMask;

    /// <summary>
    /// 是否分包
    /// </summary>
    public bool IsPackage => (MessageBodyProperty & Protocol.MessageBodyProperty.PackageFlagMask) != 0;

    /// <summary>
    /// 数据加密方式(0:不加密 1:RSA)
    /// </summary>
    public byte EncryptionType => (byte)((MessageBodyProperty & Protocol.MessageBodyProperty.EncryptionMask) >> Protocol.MessageBodyProperty.EncryptionShift);

    /// <summary>
    /// 是否2019版本
    /// </summary>
    public bool Is2019Version => (MessageBodyProperty & Protocol.MessageBodyProperty.VersionFlagMask) != 0;

    /// <summary>
    /// 获取手机号字节长度
    /// </summary>
    public int PhoneNumberLength => Is2019Version ? 10 : 6;
}

/// <summary>
/// 消息包封装信息
/// </summary>
public class JT808PackageInfo
{
    /// <summary>
    /// 消息包总数
    /// </summary>
    public ushort TotalPackage { get; set; }

    /// <summary>
    /// 包序号
    /// </summary>
    public ushort PackageIndex { get; set; }
}

/// <summary>
/// JT808消息体
/// </summary>
public class JT808Message
{
    /// <summary>
    /// 消息头
    /// </summary>
    public JT808Header Header { get; set; } = new();

    /// <summary>
    /// 消息体数据
    /// </summary>
    public byte[] Body { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// 校验码
    /// </summary>
    public byte CheckCode { get; set; }
}

/// <summary>
/// 位置信息汇报 (2019版本)
/// </summary>
public class LocationInfo
{
    /// <summary>
    /// 报警标志 (4字节)
    /// </summary>
    public uint AlarmFlag { get; set; }

    /// <summary>
    /// 状态 (4字节)
    /// </summary>
    public uint Status { get; set; }

    /// <summary>
    /// 纬度(以度为单位的纬度值乘以10^6)
    /// </summary>
    public uint Latitude { get; set; }

    /// <summary>
    /// 经度(以度为单位的经度值乘以10^6)
    /// </summary>
    public uint Longitude { get; set; }

    /// <summary>
    /// 高程(海拔高度,单位为米)
    /// </summary>
    public ushort Altitude { get; set; }

    /// <summary>
    /// 速度(1/10km/h)
    /// </summary>
    public ushort Speed { get; set; }

    /// <summary>
    /// 方向(0-359,正北为0,顺时针)
    /// </summary>
    public ushort Direction { get; set; }

    /// <summary>
    /// GPS时间(YY-MM-DD-hh-mm-ss, BCD码)
    /// </summary>
    public DateTime GpsTime { get; set; }

    /// <summary>
    /// 位置附加信息列表
    /// </summary>
    public List<LocationAdditionalInfo> AdditionalInfoList { get; set; } = new();

    /// <summary>
    /// 获取纬度(度)
    /// </summary>
    public double GetLatitude() => Latitude / 1000000.0;

    /// <summary>
    /// 获取经度(度)
    /// </summary>
    public double GetLongitude() => Longitude / 1000000.0;

    /// <summary>
    /// 获取速度(km/h)
    /// </summary>
    public double GetSpeed() => Speed / 10.0;

    /// <summary>
    /// 是否ACC开启
    /// </summary>
    public bool IsAccOn => (Status & 0x01) != 0;

    /// <summary>
    /// 是否已定位
    /// </summary>
    public bool IsPositioned => (Status & 0x02) != 0;

    /// <summary>
    /// 是否南纬
    /// </summary>
    public bool IsSouthLatitude => (Status & 0x04) != 0;

    /// <summary>
    /// 是否西经
    /// </summary>
    public bool IsWestLongitude => (Status & 0x08) != 0;
}

/// <summary>
/// 位置附加信息
/// </summary>
public class LocationAdditionalInfo
{
    /// <summary>
    /// 附加信息ID
    /// </summary>
    public byte Id { get; set; }

    /// <summary>
    /// 附加信息长度
    /// </summary>
    public byte Length { get; set; }

    /// <summary>
    /// 附加信息内容
    /// </summary>
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// 终端注册消息 (2019版本)
/// </summary>
public class TerminalRegisterInfo
{
    /// <summary>
    /// 省域ID (2字节)
    /// </summary>
    public ushort ProvinceId { get; set; }

    /// <summary>
    /// 市县域ID (2字节)
    /// </summary>
    public ushort CityId { get; set; }

    /// <summary>
    /// 制造商ID (11字节, 2019扩展为11字节)
    /// </summary>
    public string ManufacturerId { get; set; } = string.Empty;

    /// <summary>
    /// 终端型号 (30字节, 2019扩展为30字节)
    /// </summary>
    public string TerminalModel { get; set; } = string.Empty;

    /// <summary>
    /// 终端ID (30字节, 2019扩展为30字节)
    /// </summary>
    public string TerminalId { get; set; } = string.Empty;

    /// <summary>
    /// 车牌颜色 (1字节)
    /// 0:未上牌 1:蓝色 2:黄色 3:黑色 4:白色 9:其他
    /// </summary>
    public byte PlateColor { get; set; }

    /// <summary>
    /// 车辆标识(车牌号, 可变长度)
    /// </summary>
    public string PlateNumber { get; set; } = string.Empty;
}

/// <summary>
/// 终端鉴权消息 (2019版本)
/// </summary>
public class TerminalAuthenticationInfo
{
    /// <summary>
    /// 鉴权码长度 (2019新增, 1字节)
    /// </summary>
    public byte AuthCodeLength { get; set; }

    /// <summary>
    /// 鉴权码 (可变长度)
    /// </summary>
    public string AuthCode { get; set; } = string.Empty;

    /// <summary>
    /// IMEI (2019新增, 15字节)
    /// </summary>
    public string IMEI { get; set; } = string.Empty;

    /// <summary>
    /// 软件版本号 (2019新增, 20字节)
    /// </summary>
    public string SoftwareVersion { get; set; } = string.Empty;
}

/// <summary>
/// 终端参数项
/// </summary>
public class TerminalParameter
{
    /// <summary>
    /// 参数ID (4字节, 2019扩展为4字节)
    /// </summary>
    public uint Id { get; set; }

    /// <summary>
    /// 参数长度
    /// </summary>
    public byte Length { get; set; }

    /// <summary>
    /// 参数值
    /// </summary>
    public byte[] Value { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// 终端属性 (2019版本)
/// </summary>
public class TerminalProperties
{
    /// <summary>
    /// 终端类型
    /// </summary>
    public ushort TerminalType { get; set; }

    /// <summary>
    /// 制造商ID (11字节)
    /// </summary>
    public string ManufacturerId { get; set; } = string.Empty;

    /// <summary>
    /// 终端型号 (30字节)
    /// </summary>
    public string TerminalModel { get; set; } = string.Empty;

    /// <summary>
    /// 终端ID (30字节)
    /// </summary>
    public string TerminalId { get; set; } = string.Empty;

    /// <summary>
    /// 终端SIM卡ICCID (20字节)
    /// </summary>
    public string ICCID { get; set; } = string.Empty;

    /// <summary>
    /// 终端硬件版本号长度
    /// </summary>
    public byte HardwareVersionLength { get; set; }

    /// <summary>
    /// 终端硬件版本号
    /// </summary>
    public string HardwareVersion { get; set; } = string.Empty;

    /// <summary>
    /// 终端固件版本号长度
    /// </summary>
    public byte FirmwareVersionLength { get; set; }

    /// <summary>
    /// 终端固件版本号
    /// </summary>
    public string FirmwareVersion { get; set; } = string.Empty;

    /// <summary>
    /// GNSS模块属性
    /// </summary>
    public byte GNSSProperties { get; set; }

    /// <summary>
    /// 通信模块属性
    /// </summary>
    public byte CommunicationProperties { get; set; }
}

/// <summary>
/// 多媒体类型
/// </summary>
public enum MultimediaType : byte
{
    Image = 0,      // 图像
    Audio = 1,      // 音频
    Video = 2       // 视频
}

/// <summary>
/// 多媒体格式编码
/// </summary>
public enum MultimediaFormat : byte
{
    JPEG = 0,       // JPEG
    TIF = 1,        // TIF
    MP3 = 2,        // MP3
    WAV = 3,        // WAV
    WMV = 4         // WMV
}

/// <summary>
/// 多媒体事件项编码
/// </summary>
public enum MultimediaEvent : byte
{
    PlatformCommand = 0,        // 平台下发指令
    TimerAction = 1,            // 定时动作
    RobberyAlarm = 2,           // 抢劫报警触发
    CollisionRolloverAlarm = 3, // 碰撞侧翻报警触发
    OpenDoor = 4,               // 门开拍照 (2019新增)
    CloseDoor = 5               // 门关拍照 (2019新增)
}

/// <summary>
/// 多媒体数据上传 (0x0801)
/// </summary>
public class MultimediaDataUpload
{
    /// <summary>
    /// 多媒体ID (4字节)
    /// </summary>
    public uint MultimediaId { get; set; }

    /// <summary>
    /// 多媒体类型 (1字节)
    /// 0:图像 1:音频 2:视频
    /// </summary>
    public MultimediaType Type { get; set; }

    /// <summary>
    /// 多媒体格式编码 (1字节)
    /// 0:JPEG 1:TIF 2:MP3 3:WAV 4:WMV
    /// </summary>
    public MultimediaFormat Format { get; set; }

    /// <summary>
    /// 事件项编码 (1字节)
    /// </summary>
    public MultimediaEvent Event { get; set; }

    /// <summary>
    /// 通道ID (1字节)
    /// </summary>
    public byte ChannelId { get; set; }

    /// <summary>
    /// 位置信息汇报 (28字节)
    /// </summary>
    public LocationInfo? Location { get; set; }

    /// <summary>
    /// 多媒体数据包
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// 获取文件扩展名 (小写)
    /// </summary>
    public string GetFileExtension()
    {
        return Format switch
        {
            MultimediaFormat.JPEG => ".jpg",
            MultimediaFormat.TIF => ".tif",
            MultimediaFormat.MP3 => ".mp3",
            MultimediaFormat.WAV => ".wav",
            MultimediaFormat.WMV => ".wmv",
            _ => ".bin"
        };
    }

    /// <summary>
    /// 获取文件扩展名 (大写)
    /// </summary>
    public string GetFileExtensionUpper()
    {
        return Format switch
        {
            MultimediaFormat.JPEG => ".JPEG",
            MultimediaFormat.TIF => ".TIF",
            MultimediaFormat.MP3 => ".MP3",
            MultimediaFormat.WAV => ".WAV",
            MultimediaFormat.WMV => ".WMV",
            _ => ".BIN"
        };
    }

    /// <summary>
    /// 获取多媒体类型名称
    /// </summary>
    public string GetTypeName()
    {
        return Type switch
        {
            MultimediaType.Image => "图像",
            MultimediaType.Audio => "音频",
            MultimediaType.Video => "视频",
            _ => "未知"
        };
    }
}
