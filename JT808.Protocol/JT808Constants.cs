namespace JT808.Protocol;

/// <summary>
/// JT808-2019协议常量定义
/// </summary>
public static class JT808Constants
{
    /// <summary>
    /// 标识位 0x7E
    /// </summary>
    public const byte FLAG = 0x7E;

    /// <summary>
    /// 转义字符 0x7D
    /// </summary>
    public const byte ESCAPE = 0x7D;

    /// <summary>
    /// 0x7E 转义后为 0x7D 0x02
    /// </summary>
    public const byte ESCAPE_7E = 0x02;

    /// <summary>
    /// 0x7D 转义后为 0x7D 0x01
    /// </summary>
    public const byte ESCAPE_7D = 0x01;
}

/// <summary>
/// 消息ID定义 (JT/T 808-2019)
/// </summary>
public static class JT808MessageId
{
    // ========== 终端通用消息 ==========
    public const ushort TerminalGeneralResponse = 0x0001;      // 终端通用应答
    public const ushort PlatformGeneralResponse = 0x8001;      // 平台通用应答
    public const ushort TerminalHeartbeat = 0x0002;            // 终端心跳
    public const ushort SupplementaryTransmission = 0x8003;    // 补传分包请求
    public const ushort TerminalRegister = 0x0100;             // 终端注册
    public const ushort TerminalRegisterResponse = 0x8100;     // 终端注册应答
    public const ushort TerminalUnregister = 0x0003;           // 终端注销
    public const ushort TerminalAuthentication = 0x0102;       // 终端鉴权

    // ========== 参数设置 ==========
    public const ushort SetTerminalParameters = 0x8103;        // 设置终端参数
    public const ushort QueryTerminalParameters = 0x8104;      // 查询终端参数
    public const ushort QueryTerminalParametersResponse = 0x0104; // 查询终端参数应答
    public const ushort QueryTerminalProperties = 0x8107;      // 查询终端属性
    public const ushort QueryTerminalPropertiesResponse = 0x0107; // 查询终端属性应答
    public const ushort TerminalUpgrade = 0x8108;              // 下发终端升级包
    public const ushort TerminalUpgradeResponse = 0x0108;      // 终端升级结果通知

    // ========== 位置信息 ==========
    public const ushort LocationReport = 0x0200;               // 位置信息汇报
    public const ushort LocationQueryResponse = 0x0201;        // 位置信息查询应答
    public const ushort LocationQuery = 0x8201;                // 位置信息查询
    public const ushort LocationTrack = 0x8202;                // 临时位置跟踪控制
    public const ushort LocationBatchUpload = 0x0704;          // 定位数据批量上传
    public const ushort BatchUploadResponse = 0x8704;          // 定位数据批量上传应答

    // ========== 信息服务 ==========
    public const ushort TextMessageDownload = 0x8300;          // 文本信息下发
    public const ushort QuestionDownload = 0x8302;             // 提问下发
    public const ushort QuestionResponse = 0x0302;             // 提问应答
    public const ushort InfoMenuSetting = 0x8303;              // 信息点播菜单设置
    public const ushort InfoService = 0x0303;                  // 信息点播/取消

    // ========== 电话服务 ==========
    public const ushort CallbackRequest = 0x8400;              // 电话回拨
    public const ushort SetPhoneBook = 0x8401;                 // 设置电话本

    // ========== 车辆控制 ==========
    public const ushort VehicleControl = 0x8500;               // 车辆控制
    public const ushort VehicleControlResponse = 0x0500;       // 车辆控制应答

    // ========== 事件设置 ==========
    public const ushort SetCircularArea = 0x8600;              // 设置圆形区域
    public const ushort DeleteCircularArea = 0x8601;           // 删除圆形区域
    public const ushort SetRectangularArea = 0x8602;           // 设置矩形区域
    public const ushort DeleteRectangularArea = 0x8603;        // 删除矩形区域
    public const ushort SetPolygonArea = 0x8604;               // 设置多边形区域
    public const ushort DeletePolygonArea = 0x8605;            // 删除多边形区域
    public const ushort SetRoute = 0x8606;                     // 设置路线
    public const ushort DeleteRoute = 0x8607;                  // 删除路线

    // ========== 多媒体 (2019新增) ==========
    public const ushort MultimediaDataUpload = 0x0801;         // 多媒体数据上传
    public const ushort MultimediaDataUploadResponse = 0x8800; // 多媒体数据上传应答
    public const ushort CameraShootImmediately = 0x8801;       // 摄像头立即拍摄命令
    public const ushort CameraShootImmediatelyResponse = 0x0805; // 摄像头立即拍摄命令应答
    public const ushort StoredMediaDataSearch = 0x8802;        // 存储多媒体数据检索
    public const ushort StoredMediaDataSearchResponse = 0x0802; // 存储多媒体数据检索应答
    public const ushort StoredMediaDataUpload = 0x8803;        // 存储多媒体数据上传
    public const ushort StoredMediaDataUploadResponse = 0x0803; // 存储多媒体数据上传应答
    public const ushort RecordingStartCommand = 0x8804;        // 录音开始命令
    public const ushort SingleStoredMediaDataSearchUpload = 0x8805; // 单条存储多媒体数据检索上传

    // ========== 数据采集 ==========
    public const ushort DataCollectionUpload = 0x0900;         // 数据上行透传
    public const ushort DataCollectionDownload = 0x8900;       // 数据下行透传

    // ========== 数据压缩上报 (2019新增) ==========
    public const ushort DataCompressionReport = 0x0901;        // 数据压缩上报

    // ========== RSA公钥 (2019新增) ==========
    public const ushort RSAPublicKeyQuery = 0x8A00;            // 平台RSA公钥
    public const ushort TerminalRSAPublicKey = 0x0A00;         // 终端RSA公钥
}

/// <summary>
/// 消息体属性 (2019版本扩展)
/// </summary>
public static class MessageBodyProperty
{
    /// <summary>
    /// 消息体长度掩码 (bit0-9)
    /// </summary>
    public const ushort LengthMask = 0x03FF;

    /// <summary>
    /// 数据加密方式 (bit10-12)
    /// 0: 不加密
    /// 1: RSA加密
    /// </summary>
    public const ushort EncryptionMask = 0x1C00;
    public const int EncryptionShift = 10;

    /// <summary>
    /// 分包标志 (bit13)
    /// </summary>
    public const ushort PackageFlagMask = 0x2000;

    /// <summary>
    /// 版本标识 (bit14, 2019新增)
    /// 0: 2013版本
    /// 1: 2019版本
    /// </summary>
    public const ushort VersionFlagMask = 0x4000;
}

/// <summary>
/// 注册结果
/// </summary>
public enum RegisterResult : byte
{
    Success = 0,                // 成功
    VehicleRegistered = 1,      // 车辆已被注册
    NoVehicleInDatabase = 2,    // 数据库中无该车辆
    TerminalRegistered = 3,     // 终端已被注册
    NoTerminalInDatabase = 4    // 数据库中无该终端
}

/// <summary>
/// 终端通用应答结果
/// </summary>
public enum CommonResult : byte
{
    Success = 0,                // 成功/确认
    Failure = 1,                // 失败
    MessageError = 2,           // 消息有误
    NotSupported = 3,           // 不支持
    AlarmConfirm = 4            // 报警处理确认 (2019新增)
}

/// <summary>
/// 位置附加信息ID (2019版本扩展)
/// </summary>
public static class LocationAdditionalInfoId
{
    public const byte Mileage = 0x01;                  // 里程,DWORD,1/10km
    public const byte FuelQuantity = 0x02;             // 油量,WORD,1/10L
    public const byte Speed = 0x03;                    // 行驶记录功能获取的速度,WORD,1/10km/h
    public const byte AlarmEventId = 0x04;             // 需要人工确认报警事件的ID,WORD
    public const byte TirePressure = 0x05;             // 胎压,多个轮胎数据
    public const byte CarriageTemperature = 0x06;      // 车厢温度,WORD,摄氏度

    // 2019新增
    public const byte OverspeedAlarmInfo = 0x11;       // 超速报警附加信息
    public const byte InOutAreaAlarmInfo = 0x12;       // 进出区域/路线报警附加信息
    public const byte RouteDrivingTimeInfo = 0x13;     // 路段行驶时间不足/过长报警附加信息
    public const byte ExtendedVehicleSignalStatus = 0x25; // 扩展车辆信号状态位
    public const byte IOStatus = 0x2A;                 // IO状态位
    public const byte AnalogQuantity = 0x2B;           // 模拟量,bit0-15
    public const byte WirelessSignalStrength = 0x30;   // 无线通信网络信号强度
    public const byte GNSSPositioningCount = 0x31;     // GNSS定位卫星数

    // 自定义信息区域 0xE0-0xFF
    public const byte CustomInfoStart = 0xE0;
    public const byte CustomInfoEnd = 0xFF;
}
