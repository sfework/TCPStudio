namespace TCPStudio.Services
{
    /// <summary>连接配置（持久化到 Data/connections.json，Status 为运行时状态）</summary>
    public class ConnectionConfig
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 8080;
        public string Encoding { get; set; } = "hex";
        public bool AutoReconnect { get; set; }
        public string Status { get; set; } = "disconnected";
    }

    /// <summary>收发记录（持久化到 Data/records/{connectionId}.json）</summary>
    public class MessageRecord
    {
        public string Id { get; set; } = "";
        public string ConnectionId { get; set; } = "";
        /// <summary>send / recv</summary>
        public string Direction { get; set; } = "recv";
        public DateTime Timestamp { get; set; } = DateTime.Now;
        /// <summary>原始字节 Base64（无损存储）</summary>
        public string RawBase64 { get; set; } = "";
        public string Encoding { get; set; } = "hex";
        /// <summary>按当时编码解码的展示文本</summary>
        public string Text { get; set; } = "";
        public int Length { get; set; }
    }

    /// <summary>预设数据包（持久化到 Data/presets.json）</summary>
    public class Preset
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Content { get; set; } = "";
        public string Encoding { get; set; } = "hex";
        /// <summary>绑定连接；空=全局</summary>
        public string? ConnectionId { get; set; }
        public int Order { get; set; }
    }

    /// <summary>统一操作结果（避免 bool 经桥接变 "True"/"False" 字符串的真值坑）</summary>
    public class ServiceResult
    {
        public bool Ok { get; set; }
        public string Msg { get; set; } = "";
        public static ServiceResult Success(string msg = "") => new() { Ok = true, Msg = msg };
        public static ServiceResult Fail(string msg) => new() { Ok = false, Msg = msg };
    }

    /// <summary>发送请求（桥接单参数约定：所有服务方法只收一个复杂对象或基础类型）</summary>
    public class SendRequest
    {
        public string Id { get; set; } = "";
        public string Data { get; set; } = "";
        public string? Encoding { get; set; }
        /// <summary>行尾：空=无，crlf=\r\n</summary>
        public string? NewLine { get; set; }
    }

    public class RecordQuery
    {
        public string ConnectionId { get; set; } = "";
        public int PageIndex { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }

    public class RecordPage
    {
        public int Total { get; set; }
        public List<MessageRecord> Items { get; set; } = [];
    }

    public class DeleteRecordRequest
    {
        public string ConnectionId { get; set; } = "";
        public string RecordId { get; set; } = "";
    }

    public class SendPresetRequest
    {
        public string PresetId { get; set; } = "";
        public string? ConnectionId { get; set; }
    }

    /// <summary>发送结果（成功时携带记录用于前端回显）</summary>
    public class SendResult
    {
        public bool Ok { get; set; }
        public string Msg { get; set; } = "";
        public MessageRecord? Record { get; set; }
    }
}
