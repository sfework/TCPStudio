namespace TCPStudio.Services
{
    /// <summary>
    /// TCP 服务门面：经 SmartInvoker 暴露给前端（HostObject "Tcp"）。
    /// 约定：每个方法只接收一个参数（基础类型或强类型 POCO），规避桥接多参数缺陷；
    /// 入参反序列化与返回值序列化由 SmartInvoker 统一完成（camelCase），业务代码不再手写 JSON。
    /// 事件通过 Attach 注入的 push 委托转发到 WebView2 PostWebMessageAsJson。
    /// </summary>
    public class TcpService
    {
        private readonly JsonDataStore _store;
        private readonly List<ConnectionConfig> _connections;
        private readonly List<Preset> _presets;
        private readonly Dictionary<string, TcpConnection> _clients = [];
        /// <summary>每连接内存记录（含未落盘部分），上限保护</summary>
        private readonly Dictionary<string, List<MessageRecord>> _records = [];
        /// <summary>待增量追加的新记录</summary>
        private readonly Dictionary<string, List<MessageRecord>> _appendQueue = [];
        /// <summary>需要全量重写记录文件的连接（删除/清空后）</summary>
        private readonly HashSet<string> _rewriteQueue = [];
        private readonly object _queueLock = new();
        private Action<string, object?>? _push;
        private System.Timers.Timer? _flushTimer;
        private const int MemoryRecordLimit = 2000;

        public TcpService(JsonDataStore store)
        {
            _store = store;
            _connections = store.LoadConnections();
            // 重启后无活动 socket，持久化的连接状态不可信，统一重置
            foreach (var c in _connections) c.Status = "disconnected";
            _presets = store.LoadPresets();
            // 定时防抖落盘变更记录
            _flushTimer = new System.Timers.Timer(1000) { AutoReset = true };
            _flushTimer.Elapsed += (_, _) => FlushRecords();
            _flushTimer.Start();
        }

        /// <summary>由宿主在 WebView2 就绪后注入推送出口（action + data，信封序列化由宿主 HostBridge 完成）。</summary>
        public void Attach(Action<string, object?> push)
        {
            _push = push;
        }

        /// <summary>窗口关闭时释放全部连接与落盘定时器。</summary>
        public void Shutdown()
        {
            _flushTimer?.Stop();
            _flushTimer?.Dispose();
            FlushRecords();
            Thread.Sleep(200);
            foreach (var cli in _clients.Values)
            {
                cli.Dispose();
            }
            _clients.Clear();
        }

        #region 连接管理

        public List<ConnectionConfig> GetConnections()
        {
            foreach (var c in _connections)
            {
                if (_clients.TryGetValue(c.Id, out var cli)) c.Status = cli.Status;
            }
            return _connections;
        }

        public ConnectionConfig AddConnection(ConnectionConfig config)
        {
            config ??= new ConnectionConfig();
            config.Id = string.IsNullOrWhiteSpace(config.Id) ? Guid.NewGuid().ToString("N") : config.Id;
            config.Status = "disconnected";
            _connections.Add(config);
            _ = _store.SaveConnectionsAsync(_connections);
            return config;
        }

        public ServiceResult UpdateConnection(ConnectionConfig config)
        {
            config ??= new ConnectionConfig();
            var old = _connections.FirstOrDefault(x => x.Id == config.Id);
            if (old == null) return ServiceResult.Fail("连接不存在");
            old.Name = config.Name;
            old.Host = config.Host;
            old.Port = config.Port;
            old.Encoding = config.Encoding;
            old.AutoReconnect = config.AutoReconnect;
            if (_clients.TryGetValue(old.Id, out var cli)) cli.UpdateConfig(old);
            _ = _store.SaveConnectionsAsync(_connections);
            return ServiceResult.Success();
        }

        public ServiceResult RemoveConnection(string id)
        {
            var old = _connections.FirstOrDefault(x => x.Id == id);
            if (old == null) return ServiceResult.Fail("连接不存在");
            if (_clients.TryGetValue(id, out var cli))
            {
                cli.Dispose();
                _clients.Remove(id);
            }
            _connections.Remove(old);
            _records.Remove(id);
            lock (_queueLock)
            {
                _appendQueue.Remove(id);
                _rewriteQueue.Remove(id);
            }
            _store.DeleteRecords(id);
            _ = _store.SaveConnectionsAsync(_connections);
            return ServiceResult.Success();
        }

        public ServiceResult Connect(string id)
        {
            var config = _connections.FirstOrDefault(x => x.Id == id);
            if (config == null) return ServiceResult.Fail("连接不存在");
            if (!_clients.TryGetValue(id, out var cli))
            {
                cli = CreateClient(config);
                _clients[id] = cli;
            }
            if (cli.Status == "connected") return ServiceResult.Success("已处于连接状态");
            // 非阻塞：立即返回 connecting，最终 connected/error 由状态事件推送
            _ = cli.ConnectAsync();
            return new ServiceResult { Ok = true, Msg = "connecting" };
        }

        public ServiceResult Disconnect(string id)
        {
            if (_clients.TryGetValue(id, out var cli)) cli.Disconnect();
            else
            {
                var config = _connections.FirstOrDefault(x => x.Id == id);
                if (config != null) config.Status = "disconnected";
            }
            return ServiceResult.Success();
        }

        #endregion

        #region 收发

        public SendResult Send(SendRequest req)
        {
            req ??= new SendRequest();
            var config = _connections.FirstOrDefault(x => x.Id == req.Id);
            if (config == null) return new SendResult { Ok = false, Msg = "连接不存在" };
            if (!_clients.TryGetValue(req.Id, out var cli) || cli.Status != "connected")
            {
                return new SendResult { Ok = false, Msg = "未连接" };
            }
            byte[] data;
            try
            {
                data = PayloadCodec.Encode(req.Data, req.Encoding ?? config.Encoding, req.NewLine);
            }
            catch (Exception e)
            {
                return new SendResult { Ok = false, Msg = $"编码错误：{e.Message}" };
            }
            // 桥接调用发生在 UI 线程，网络写入调度到线程池避免死锁
            var result = Task.Run(() => cli.SendAsync(data)).GetAwaiter().GetResult();
            if (result.Ok)
            {
                var rec = AddRecord(req.Id, "send", data, req.Encoding ?? config.Encoding);
                return new SendResult { Ok = true, Msg = "", Record = rec };
            }
            return new SendResult { Ok = false, Msg = result.Msg };
        }

        #endregion

        #region 记录

        public RecordPage GetRecords(RecordQuery req)
        {
            req ??= new RecordQuery();
            var list = GetRecordList(req.ConnectionId);
            var total = list.Count;
            var items = list.OrderByDescending(x => x.Timestamp)
                           .Skip((Math.Max(1, req.PageIndex) - 1) * req.PageSize)
                           .Take(req.PageSize)
                           .ToList();
            return new RecordPage { Total = total, Items = items };
        }

        public ServiceResult DeleteRecord(DeleteRecordRequest req)
        {
            req ??= new DeleteRecordRequest();
            var list = GetRecordList(req.ConnectionId);
            var item = list.FirstOrDefault(x => x.Id == req.RecordId);
            if (item == null) return ServiceResult.Fail("记录不存在");
            list.Remove(item);
            lock (_queueLock) { _rewriteQueue.Add(req.ConnectionId); }
            return ServiceResult.Success();
        }

        public ServiceResult ClearRecords(string connectionId)
        {
            GetRecordList(connectionId).Clear();
            lock (_queueLock)
            {
                _appendQueue.Remove(connectionId);
                _rewriteQueue.Add(connectionId);
            }
            return ServiceResult.Success();
        }

        #endregion

        #region 预设

        public List<Preset> GetPresets()
            => _presets;

        public Preset SavePreset(Preset preset)
        {
            preset ??= new Preset();
            preset.Id = string.IsNullOrWhiteSpace(preset.Id) ? Guid.NewGuid().ToString("N") : preset.Id;
            var old = _presets.FirstOrDefault(x => x.Id == preset.Id);
            if (old == null) _presets.Add(preset);
            else _presets[_presets.IndexOf(old)] = preset;
            _ = _store.SavePresetsAsync(_presets);
            return preset;
        }

        public ServiceResult RemovePreset(string id)
        {
            _presets.RemoveAll(x => x.Id == id);
            _ = _store.SavePresetsAsync(_presets);
            return ServiceResult.Success();
        }

        public SendResult SendPreset(SendPresetRequest req)
        {
            req ??= new SendPresetRequest();
            var preset = _presets.FirstOrDefault(x => x.Id == req.PresetId);
            if (preset == null) return new SendResult { Ok = false, Msg = "预设不存在" };
            var connId = string.IsNullOrWhiteSpace(req.ConnectionId) ? preset.ConnectionId : req.ConnectionId;
            if (string.IsNullOrWhiteSpace(connId)) return new SendResult { Ok = false, Msg = "未指定连接" };
            // 不传 Encoding：发送编码跟随目标连接自身配置
            return Send(new SendRequest
            {
                Id = connId!,
                Data = preset.Content,
            });
        }

        #endregion

        #region 内部

        private TcpConnection CreateClient(ConnectionConfig config)
        {
            var cli = new TcpConnection(config);
            cli.StatusChanged += (status, msg) => PushEvent("connection.status", new
            {
                connectionId = config.Id,
                status,
                msg,
            });
            cli.DataReceived += data =>
            {
                var rec = AddRecord(config.Id, "recv", data, config.Encoding);
                PushEvent("data.received", new { connectionId = config.Id, record = rec });
            };
            return cli;
        }

        private MessageRecord AddRecord(string connectionId, string direction, byte[] data, string encoding)
        {
            var rec = new MessageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConnectionId = connectionId,
                Direction = direction,
                Timestamp = DateTime.Now,
                RawBase64 = Convert.ToBase64String(data),
                Encoding = encoding,
                Text = SafeDecode(data, encoding),
                Length = data.Length,
            };
            var list = GetRecordList(connectionId);
            list.Add(rec);
            if (list.Count > MemoryRecordLimit)
            {
                // 仅裁剪内存缓存窗口；磁盘为追加式 JSONL，历史不受影响
                list.RemoveRange(0, list.Count - MemoryRecordLimit);
            }
            lock (_queueLock)
            {
                if (!_appendQueue.TryGetValue(connectionId, out var queue))
                {
                    queue = [];
                    _appendQueue[connectionId] = queue;
                }
                queue.Add(rec);
            }
            return rec;
        }

        private List<MessageRecord> GetRecordList(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return [];
            if (!_records.TryGetValue(connectionId, out var list))
            {
                list = _store.LoadRecords(connectionId);
                _records[connectionId] = list;
            }
            return list;
        }

        private void FlushRecords()
        {
            List<(string, List<MessageRecord>)> appends;
            List<string> rewrites;
            lock (_queueLock)
            {
                appends = _appendQueue.Select(kv => (kv.Key, kv.Value)).Where(x => x.Item2.Count > 0).ToList();
                foreach (var kv in appends) _appendQueue[kv.Item1] = [];
                rewrites = _rewriteQueue.ToList();
                _rewriteQueue.Clear();
            }
            foreach (var (connId, batch) in appends)
            {
                // 重写队列优先：同一连接本轮既有删除又有新增时，全量重写已含新增
                if (rewrites.Contains(connId)) continue;
                _ = _store.AppendRecordsAsync(connId, batch);
            }
            foreach (var connId in rewrites)
            {
                if (_records.TryGetValue(connId, out var list))
                {
                    _ = _store.RewriteRecordsAsync(connId, list);
                }
            }
        }

        private static string SafeDecode(byte[] data, string encoding)
        {
            try
            {
                return PayloadCodec.DecodeText(data, encoding);
            }
            catch (Exception)
            {
                return PayloadCodec.ToHexDisplay(data);
            }
        }

        private void PushEvent(string action, object? data)
        {
            _push?.Invoke(action, data);
        }

        #endregion
    }
}
