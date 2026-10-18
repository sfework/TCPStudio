using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TcpTestServer;

/// <summary>TCP 测试服务端：多客户端接入、自动回显、手动群发/随机推送，用于 TCPStudio 联调</summary>
internal static class Program
{
    private static readonly List<ClientSession> _clients = new();
    private static readonly object _clientsLock = new();

    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // 端口：命令行第一个参数覆盖，默认 8080（与 TCPStudio 连接配置默认端口一致）
        var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8080;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Log($"监听 127.0.0.1:{port}，等待连接…");
        Console.WriteLine("命令：回车+HEX文本=群发（如 AA 55 01）；r=随机推送；q=退出");

        var accepting = AcceptLoopAsync(listener);
        var input = InputLoopAsync();
        await Task.WhenAny(accepting, input);

        listener.Stop();
        lock (_clientsLock)
        {
            foreach (var c in _clients) c.Close();
            _clients.Clear();
        }
        Log("已退出");
    }

    private static async Task AcceptLoopAsync(TcpListener listener)
    {
        while (true)
        {
            var tcp = await listener.AcceptTcpClientAsync();
            var session = new ClientSession(tcp);
            lock (_clientsLock) _clients.Add(session);
            Log($"客户端接入：{session.Remote}（当前 {Count()} 个）");
            _ = SessionLoopAsync(session);
        }
    }

    private static async Task SessionLoopAsync(ClientSession session)
    {
        var buf = new byte[4096];
        try
        {
            var stream = session.Stream;
            while (true)
            {
                var n = await stream.ReadAsync(buf);
                if (n <= 0) break;
                var data = buf.AsSpan(0, n).ToArray();
                Log($"← [{session.Remote}] {Fmt(data)}");
                // 自动回显：收到什么原样回什么，便于验证收发闭环
                await stream.WriteAsync(data);
                await stream.FlushAsync();
                Log($"→ 回显 [{session.Remote}] {Fmt(data)}");
            }
        }
        catch (IOException) { } // 客户端强制断开
        catch (OperationCanceledException) { }
        finally
        {
            lock (_clientsLock) _clients.Remove(session);
            session.Close();
            Log($"客户端断开：{session.Remote}（当前 {Count()} 个）");
        }
    }

    private static async Task InputLoopAsync()
    {
        while (true)
        {
            var line = await Task.Run(Console.ReadLine);
            if (line == null) continue;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.Equals("q", StringComparison.OrdinalIgnoreCase)) return;
            if (line.Equals("r", StringComparison.OrdinalIgnoreCase))
            {
                var rnd = new byte[Random.Shared.Next(4, 12)];
                Random.Shared.NextBytes(rnd);
                Broadcast(rnd, "随机推送");
                continue;
            }
            // 按 HEX 解析群发；解析失败则按 UTF-8 文本发送
            byte[] data;
            try
            {
                data = ParseHex(line);
            }
            catch (Exception e)
            {
                Log($"HEX 解析失败（{e.Message}），按文本发送");
                data = Encoding.UTF8.GetBytes(line);
            }
            Broadcast(data, "群发");
        }
    }

    private static void Broadcast(byte[] data, string tag)
    {
        List<ClientSession> targets;
        lock (_clientsLock) targets = _clients.ToList();
        if (targets.Count == 0) { Log($"无在线客户端，{tag}未发送"); return; }
        foreach (var c in targets)
        {
            try
            {
                c.Stream.Write(data);
                c.Stream.Flush();
            }
            catch (Exception e) { Log($"{tag}失败 {c.Remote}：{e.Message}"); }
        }
        Log($"{tag} {targets.Count} 个客户端：{Fmt(data)}");
    }

    private static int Count()
    {
        lock (_clientsLock) return _clients.Count;
    }

    /// <summary>HEX 文本解析：支持空格/逗号/换行/连字符分隔，也支持紧凑写法（AABBCC）</summary>
    private static byte[] ParseHex(string text)
    {
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length == 0 || hex.Length % 2 != 0) throw new FormatException("非法 HEX");
        return Convert.FromHexString(hex);
    }

    /// <summary>展示格式：HEX | 文本（全为可打印 ASCII 时）</summary>
    private static string Fmt(byte[] data)
    {
        var printable = data.All(b => b is >= 0x20 and < 0x7F);
        return printable ? $"{Convert.ToHexString(data)} | {Encoding.ASCII.GetString(data)}" : Convert.ToHexString(data);
    }

    private static void Log(string msg) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

    /// <summary>单个客户端会话</summary>
    private sealed class ClientSession
    {
        private readonly TcpClient _tcp;
        public NetworkStream Stream { get; }
        public string Remote { get; }

        public ClientSession(TcpClient tcp)
        {
            _tcp = tcp;
            Stream = tcp.GetStream();
            Remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        }

        public void Close()
        {
            try { Stream.Dispose(); } catch { }
            try { _tcp.Close(); } catch { }
        }
    }
}
