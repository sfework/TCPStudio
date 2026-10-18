using System.Text.Json;
using TCPStudio.Services;
using WPF.Web.Common;

namespace BridgeTest;

/// <summary>
/// 桥接链路测试：模拟前端 InvProxy 的行为——
/// JS 对象 -> JSON.stringify(camelCase) -> SmartInvoker.InvokeCall(方法名, json) -> 强类型接参/返回 POCO 序列化。
/// 不启动 WebView2，直接 new SmartInvoker(TcpService) 验证接参与返回契约。
/// </summary>
internal static class Program
{
    private static int _pass, _fail;

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // 隔离数据目录，不污染真实 Data
        var root = Path.Combine(Path.GetTempPath(), "tcpstudio_bridgetest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var service = new TcpService(new JsonDataStore(root));
            var invoker = new SmartInvoker(service);

            // 1. 无参方法：返回集合 JSON
            Check("GetConnections 空列表", () =>
            {
                var json = Call(invoker, "GetConnections");
                var arr = JsonDocument.Parse(json).RootElement;
                return arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() == 0;
            });

            // 2. 对象入参 + 返回 POCO（AddConnection：前端 camelCase -> ConnectionConfig）
            string connId = "";
            Check("AddConnection 强类型接参", () =>
            {
                var req = new { id = "", name = "测试A", host = "127.0.0.1", port = 18081, encoding = "hex", autoReconnect = false };
                var json = Call(invoker, "AddConnection", req);
                var el = JsonDocument.Parse(json).RootElement;
                connId = el.GetProperty("id").GetString() ?? "";
                return el.GetProperty("name").GetString() == "测试A"
                    && el.GetProperty("status").GetString() == "disconnected"
                    && !string.IsNullOrEmpty(connId);
            });

            // 3. 嵌套返回：GetConnections 应含 camelCase 字段
            Check("GetConnections 返回含新连接(camelCase)", () =>
            {
                var json = Call(invoker, "GetConnections");
                using var doc = JsonDocument.Parse(json);
                var first = doc.RootElement[0];
                return first.GetProperty("host").GetString() == "127.0.0.1"
                    && first.GetProperty("port").GetInt32() == 18081;
            });

            // 4. UpdateConnection：ServiceResult 返回
            Check("UpdateConnection 成功", () =>
            {
                var req = new { id = connId, name = "测试B", host = "127.0.0.1", port = 18081, encoding = "utf8", autoReconnect = false };
                var el = JsonDocument.Parse(Call(invoker, "UpdateConnection", req)).RootElement;
                return el.GetProperty("ok").GetBoolean();
            });
            Check("UpdateConnection 不存在→ok=false", () =>
            {
                var req = new { id = "no-such-id", name = "x", host = "h", port = 1, encoding = "hex", autoReconnect = false };
                var el = JsonDocument.Parse(Call(invoker, "UpdateConnection", req)).RootElement;
                return !el.GetProperty("ok").GetBoolean() && el.GetProperty("msg").GetString() == "连接不存在";
            });

            // 5. Connect 未连接目标：应返回 ok=true msg=connecting（非阻塞），随后推送到 error 不影响本测试
            Check("Connect 非阻塞返回 connecting", () =>
            {
                var el = JsonDocument.Parse(Call(invoker, "Connect", connId)).RootElement;
                return el.GetProperty("ok").GetBoolean() && el.GetProperty("msg").GetString() == "connecting";
            });

            // 6. Disconnect：基础类型 string 入参
            Check("Disconnect 基础类型入参", () =>
            {
                var el = JsonDocument.Parse(Call(invoker, "Disconnect", connId)).RootElement;
                return el.GetProperty("ok").GetBoolean();
            });

            // 7. Send 未连接：SendRequest 接参 + SendResult 返回
            Check("Send 未连接→ok=false", () =>
            {
                var req = new { id = connId, data = "AA55" };
                var el = JsonDocument.Parse(Call(invoker, "Send", req)).RootElement;
                return !el.GetProperty("ok").GetBoolean() && el.GetProperty("msg").GetString() == "未连接";
            });

            // 8. 真实 TCP 回环：起监听再 Send，验证 SendResult.record 嵌套序列化
            var (host, port) = StartEchoServer();
            var liveId = AddAndConnect(invoker, host, port);
            Check("Send 成功→record 字段(camelCase)", () =>
            {
                var req = new { id = liveId, data = "AD" };
                var el = JsonDocument.Parse(Call(invoker, "Send", req)).RootElement;
                if (!el.GetProperty("ok").GetBoolean()) return false;
                var rec = el.GetProperty("record");
                return rec.GetProperty("direction").GetString() == "send"
                    && rec.GetProperty("rawBase64").GetString() == "rQ=="
                    && rec.GetProperty("text").GetString() == "AD"
                    && rec.GetProperty("timestamp").GetString()!.Contains("T");
            });

            // 9. 预设：SavePreset 对象入参/返回、SendPreset 走连接编码
            string presetId = "";
            Check("SavePreset 新建（无 id 自动生成）", () =>
            {
                var p = new { name = "心跳", content = "AA 55 01 00", connectionId = liveId };
                var el = JsonDocument.Parse(Call(invoker, "SavePreset", p)).RootElement;
                presetId = el.GetProperty("id").GetString() ?? "";
                return !string.IsNullOrEmpty(presetId) && el.GetProperty("name").GetString() == "心跳";
            });
            Check("SendPreset 成功", () =>
            {
                var el = JsonDocument.Parse(Call(invoker, "SendPreset", new { presetId, connectionId = (string?)null })).RootElement;
                return el.GetProperty("ok").GetBoolean() && el.GetProperty("record").GetProperty("text").GetString() == "AA 55 01 00";
            });
            Check("GetPresets 列表", () =>
            {
                var arr = JsonDocument.Parse(Call(invoker, "GetPresets")).RootElement;
                return arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() == 1;
            });
            Check("RemovePreset", () =>
            {
                var el = JsonDocument.Parse(Call(invoker, "RemovePreset", presetId)).RootElement;
                return el.GetProperty("ok").GetBoolean();
            });

            // 10. 记录查询：RecordQuery 接参 + 分页返回
            Check("GetRecords 分页返回", () =>
            {
                var el = JsonDocument.Parse(Call(invoker, "GetRecords", new { connectionId = liveId, pageIndex = 1, pageSize = 10 })).RootElement;
                return el.GetProperty("total").GetInt32() >= 2 && el.GetProperty("items").GetArrayLength() >= 2;
            });

            // 11. 方法不存在：应抛异常（修复过的 KeyNotFound 路径）
            Check("未知方法→抛异常", () =>
            {
                try { invoker.InvokeCall("NoSuchMethod", "{}"); return false; }
                catch (Exception e) { return e.Message.Contains("NoSuchMethod"); }
            });

            // 12. 错误入参 JSON：ConvertFromString 失败返回 null，方法内 ??= 兜底不崩
            Check("非法入参→不抛崩溃", () =>
            {
                try
                {
                    var el = JsonDocument.Parse(CallRaw(invoker, "Send", "not-json")).RootElement;
                    return !el.GetProperty("ok").GetBoolean();
                }
                catch { return false; }
            });

            service.Shutdown();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("—— SmartInvoker 通用传参/返回类型兼容性探针 ——");
        var (p2, f2) = ProbeTests.Run();
        _pass += p2;
        _fail += f2;

        Console.WriteLine();
        Console.WriteLine("—— HostBridge 后端调前端请求-响应链路 ——");
        var (p3, f3) = HostBridgeTests.Run();
        _pass += p3;
        _fail += f3;

        Console.WriteLine();
        Console.WriteLine($"结果：通过 {_pass}，失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>模拟前端：方法名 + 单参数（对象会被 JSON.stringify；基础类型直接 toString 传）</summary>
    private static string Call(SmartInvoker invoker, string method, object? arg = null)
    {
        var input = arg switch
        {
            null => method == "Connect" || method == "Disconnect" || method == "RemovePreset" ? "" : "{}",
            string s => s,
            _ => JsonSerializer.Serialize(arg, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        };
        var result = invoker.InvokeCall(method, input);
        // 桥接契约：返回值应为 JSON 字符串；兜底序列化用 camelCase 贴近真实链路
        return result as string ?? JsonSerializer.Serialize(result, JsonOpts);
    }

    private static string CallRaw(SmartInvoker invoker, string method, string raw)
    {
        var result = invoker.InvokeCall(method, raw);
        return result as string ?? JsonSerializer.Serialize(result, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static (string host, int port) StartEchoServer()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                System.Net.Sockets.TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var buf = new byte[1024];
                        try
                        {
                            while (true)
                            {
                                int n = await s.ReadAsync(buf);
                                if (n <= 0) break;
                                await s.WriteAsync(buf.AsMemory(0, n));
                            }
                        }
                        catch { }
                    }
                });
            }
        });
        return ("127.0.0.1", port);
    }

    private static string AddAndConnect(SmartInvoker invoker, string host, int port)
    {
        var json = Call(invoker, "AddConnection", new { id = "", name = "live", host, port, encoding = "hex", autoReconnect = false });
        var id = JsonDocument.Parse(json).RootElement.GetProperty("id").GetString()!;
        Call(invoker, "Connect", id);
        // 等待异步连接完成（回环地址通常 <100ms）
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(40);
            var arr = JsonDocument.Parse(Call(invoker, "GetConnections")).RootElement;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.GetProperty("id").GetString() == id && el.GetProperty("status").GetString() == "connected") return id;
            }
        }
        throw new Exception("连接超时，未进入 connected 状态");
    }

    private static void Check(string name, Func<bool> f)
    {
        try
        {
            if (f()) { _pass++; Console.WriteLine($"✅ {name}"); }
            else { _fail++; Console.WriteLine($"❌ {name}：断言失败"); }
        }
        catch (Exception e)
        {
            _fail++;
            Console.WriteLine($"❌ {name}：{e.GetType().Name} {e.Message}");
        }
    }
}
