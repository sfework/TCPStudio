using System.Net.Sockets;

namespace TCPStudio.Services
{
    /// <summary>
    /// 单条 TCP 客户端连接：连接/断开/发送/后台接收循环/自动重连。
    /// 状态变化与收到数据通过事件抛出，由上层（TcpService）转发推送与记录。
    /// ConnectAsync 为非阻塞异步连接，结果通过 StatusChanged 事件反馈（避免桥接调用阻塞 UI 线程）。
    /// </summary>
    public class TcpConnection : IDisposable
    {
        public ConnectionConfig Config { get; private set; }
        public string Status { get; private set; } = "disconnected";

        /// <summary>参数：(状态, 错误信息)</summary>
        public event Action<string, string>? StatusChanged;
        /// <summary>参数：收到的原始字节</summary>
        public event Action<byte[]>? DataReceived;

        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private int _reconnectRound;
        private const int ReconnectIntervalMs = 3000;
        private const int ReconnectMaxRounds = 10;

        public TcpConnection(ConnectionConfig config)
        {
            Config = config;
        }

        public void UpdateConfig(ConnectionConfig config) => Config = config;

        /// <summary>异步发起连接（立即返回，最终状态走 StatusChanged 事件）。重复调用安全。</summary>
        public Task ConnectAsync()
        {
            if (Status is "connected" or "connecting") return Task.CompletedTask;
            _reconnectRound = 0;
            return DoConnectAsync(auto: false);
        }

        public void Disconnect()
        {
            _cts?.Cancel();
            _cts = null;
            CloseSocket();
            _reconnectRound = 0;
            SetStatus("disconnected", "");
        }

        /// <summary>发送原始字节；失败返回错误并转入重连判断。</summary>
        public async Task<ServiceResult> SendAsync(byte[] data)
        {
            if (_stream == null || Status != "connected")
            {
                return ServiceResult.Fail("未连接");
            }
            await _sendLock.WaitAsync();
            try
            {
                await _stream.WriteAsync(data);
                await _stream.FlushAsync();
                return ServiceResult.Success();
            }
            catch (Exception e)
            {
                FailAndReconnect($"发送失败：{e.Message}");
                return ServiceResult.Fail($"发送失败：{e.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task DoConnectAsync(bool auto)
        {
            SetStatus("connecting", "");
            var cts = new CancellationTokenSource();
            var client = new TcpClient();
            try
            {
                using (cts.Token.Register(() => { try { client.Close(); } catch { } }))
                {
                    await client.ConnectAsync(Config.Host, Config.Port, cts.Token);
                }
                client.NoDelay = true;
                _client = client;
                _stream = client.GetStream();
                _cts = cts;
                SetStatus("connected", "");
                _ = Task.Run(() => ReadLoopAsync(cts.Token));
            }
            catch (Exception e)
            {
                try { client.Close(); } catch { }
                cts.Dispose();
                var msg = e is OperationCanceledException ? "连接超时" : e.Message;
                SetStatus("error", msg);
                // 手动连接失败不重连；自动重连失败继续下一轮
                if (auto && Status == "error")
                {
                    ScheduleReconnect();
                }
            }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            try
            {
                while (!ct.IsCancellationRequested && _stream != null)
                {
                    int n = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (n <= 0) break;
                    DataReceived?.Invoke(buffer.AsSpan(0, n).ToArray());
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                // 读循环异常：按断开处理
            }
            if (!ct.IsCancellationRequested)
            {
                FailAndReconnect("对端断开");
            }
        }

        /// <summary>异常断开：置 error；开启自动重连则按间隔重试（有限轮数）。</summary>
        private void FailAndReconnect(string msg)
        {
            _cts?.Cancel();
            _cts = null;
            CloseSocket();
            SetStatus("error", msg);
            if (Config.AutoReconnect)
            {
                ScheduleReconnect();
            }
        }

        private void ScheduleReconnect()
        {
            if (!Config.AutoReconnect || _reconnectRound >= ReconnectMaxRounds) return;
            _reconnectRound++;
            _ = Task.Delay(ReconnectIntervalMs).ContinueWith(_ =>
            {
                // 用户已手动改配置/关闭则停止
                if (Config.AutoReconnect && Status == "error")
                {
                    _ = DoConnectAsync(auto: true);
                }
            });
        }

        private void CloseSocket()
        {
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
        }

        private void SetStatus(string status, string msg)
        {
            Status = status;
            Config.Status = status;
            StatusChanged?.Invoke(status, msg);
        }

        public void Dispose()
        {
            _cts?.Cancel();
            CloseSocket();
        }
    }
}
