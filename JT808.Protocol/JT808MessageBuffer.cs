namespace JT808.Protocol;

/// <summary>
/// JT808 消息缓冲区 — 处理粘包/半包
///
/// 高并发优化版:
///   - 用 byte[] + head/tail 双指针, 替代原 List&lt;byte&gt; + RemoveRange (O(n²) shift)
///   - Append 是 O(count), Extract 是 O(buffer 长度) 单趟扫描
///   - head 过半时 compact, 不会 lpush 增长
///   - 设置最大容量上限, 防止恶意客户端不发结束 FLAG 撑爆内存
/// </summary>
public class JT808MessageBuffer
{
    private const int InitialCapacity = 4096;
    private const int MaxCapacity = 65536;   // 单连接最大缓冲 64KB

    private byte[] _buffer;
    private int _head;   // 有效数据起点 (含)
    private int _tail;   // 有效数据终点 (不含)
    private readonly object _lock = new();

    public JT808MessageBuffer()
    {
        _buffer = new byte[InitialCapacity];
    }

    /// <summary>
    /// 添加接收到的数据 (从 data 起点)
    /// </summary>
    public void Append(byte[] data)
    {
        if (data == null || data.Length == 0) return;
        Append(data, 0, data.Length);
    }

    /// <summary>
    /// 添加接收到的数据 (片段)
    /// </summary>
    public void Append(byte[] data, int offset, int count)
    {
        if (count <= 0) return;
        lock (_lock)
        {
            EnsureSpace(count);
            Buffer.BlockCopy(data, offset, _buffer, _tail, count);
            _tail += count;
        }
    }

    /// <summary>
    /// 确保 _buffer 末尾有 additional 字节空间; 必要时 compact 或扩容
    /// 调用者必须持有 _lock
    /// </summary>
    private void EnsureSpace(int additional)
    {
        // 末尾空间够 → 直接返回
        if (_tail + additional <= _buffer.Length) return;

        int valid = _tail - _head;

        // compact 后能放下 → compact
        if (valid + additional <= _buffer.Length)
        {
            if (valid > 0 && _head > 0)
            {
                Buffer.BlockCopy(_buffer, _head, _buffer, 0, valid);
            }
            _head = 0;
            _tail = valid;
            return;
        }

        // 需要扩容
        int newSize = _buffer.Length;
        while (newSize < valid + additional)
        {
            newSize *= 2;
            if (newSize >= MaxCapacity)
            {
                newSize = MaxCapacity;
                break;
            }
        }

        // 已经达到最大容量仍不够 → 强制清空 (防恶意客户端不发结束 FLAG 撑爆内存)
        if (valid + additional > MaxCapacity)
        {
            _head = 0;
            _tail = 0;
            // 仍然分配最大缓冲, 让本次 Append 的数据可以放进去
            if (_buffer.Length < MaxCapacity)
            {
                _buffer = new byte[MaxCapacity];
            }
            // 调用方 BlockCopy 会把 additional 字节写到 _tail=0 处
            return;
        }

        var newBuffer = new byte[newSize];
        if (valid > 0)
        {
            Buffer.BlockCopy(_buffer, _head, newBuffer, 0, valid);
        }
        _buffer = newBuffer;
        _head = 0;
        _tail = valid;
    }

    /// <summary>
    /// 提取所有完整消息 (含起止 0x7E flag)
    /// </summary>
    public List<byte[]> ExtractMessages()
    {
        var messages = new List<byte[]>();

        lock (_lock)
        {
            while (_head < _tail)
            {
                // 找起始 FLAG
                int start = -1;
                for (int i = _head; i < _tail; i++)
                {
                    if (_buffer[i] == JT808Constants.FLAG)
                    {
                        start = i;
                        break;
                    }
                }
                if (start < 0)
                {
                    // 没找到任何 FLAG → 全是垃圾, 清空
                    _head = _tail = 0;
                    break;
                }

                // 跳过起始之前的垃圾字节
                _head = start;

                // 至少需要 2 字节才能形成一条消息 (start FLAG + end FLAG)
                if (_tail - _head < 2) break;

                // 找结束 FLAG (从 head+1 开始)
                int end = -1;
                for (int i = _head + 1; i < _tail; i++)
                {
                    if (_buffer[i] == JT808Constants.FLAG)
                    {
                        end = i;
                        break;
                    }
                }
                if (end < 0) break; // 等待更多数据

                // 提取消息 (含起止 FLAG)
                int len = end - _head + 1;
                var message = new byte[len];
                Buffer.BlockCopy(_buffer, _head, message, 0, len);
                messages.Add(message);
                _head = end + 1;
            }

            // head 过半时 compact, 防止 buffer 长期占用前半段空闲空间
            if (_head > 0)
            {
                int valid = _tail - _head;
                if (_head >= _buffer.Length / 2 || valid == 0)
                {
                    if (valid > 0)
                    {
                        Buffer.BlockCopy(_buffer, _head, _buffer, 0, valid);
                    }
                    _head = 0;
                    _tail = valid;
                }
            }
        }

        return messages;
    }

    /// <summary>
    /// 清空缓冲区
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _head = _tail = 0;
        }
    }

    /// <summary>
    /// 当前缓冲区有效数据大小
    /// </summary>
    public int BufferSize
    {
        get
        {
            lock (_lock)
            {
                return _tail - _head;
            }
        }
    }
}
