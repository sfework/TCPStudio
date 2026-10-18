using System.Text.Json;
using WPF.Web.Common;

namespace BridgeTest;

/// <summary>HostBridge 测试：UseExecutor 替身模拟 ExecuteScriptWithResultAsync 的结果形态</summary>
internal static class HostBridgeTests
{
    private static int _pass, _fail;

    public static (int pass, int fail) Run()
    {
        // 替身：解析脚本里的 action/data，返回 value*2 作为结果 JSON
        HostBridge EchoBridge(Func<string, string?>? resultOf = null)
        {
            var b = new HostBridge();
            b.UseExecutor(script =>
            {
                var (action, data) = ParseCall(script);
                var result = resultOf?.Invoke(action) ?? "null";
                return Task.FromResult((true, result, (string?)null));
            });
            return b;
        }

        T("脚本格式：window.hostRpc.onHostAction(action,data)", () =>
        {
            string? captured = null;
            var b = new HostBridge();
            b.UseExecutor(s => { captured = s; return Task.FromResult((true, "1", (string?)null)); });
            b.CallAsync<int>("act", new { value = 5 }).GetAwaiter().GetResult();
            return captured == $"{HostBridge.HostRpcFunction}(\"act\",{{\"value\":5}})";
        });

        T("CallAsync int 结果", () => EchoBridge(_ => "42").CallAsync<int>("double", null).GetAwaiter().GetResult() == 42);

        T("CallAsync POCO 结果(camelCase)", () =>
        {
            var r = EchoBridge(_ => "{\"name\":\"n1\",\"count\":3}").CallAsync<Result>("get", null).GetAwaiter().GetResult();
            return r?.Name == "n1" && r?.Count == 3;
        });

        T("CallAsync string 结果去引号", () =>
            EchoBridge(_ => "\"hello\"").CallAsync<string>("say", null).GetAwaiter().GetResult() == "hello");

        T("CallAsync null 结果→default", () =>
            EchoBridge(_ => "null").CallAsync<int?>("none", null).GetAwaiter().GetResult() == null);

        T("CallAsync bool 结果", () =>
            EchoBridge(_ => "true").CallAsync<bool>("ask", null).GetAwaiter().GetResult());

        T("CallAsync List 结果", () =>
        {
            var r = EchoBridge(_ => "[1,2,3]").CallAsync<List<int>>("list", null).GetAwaiter().GetResult();
            return r is { Count: 3 };
        });

        T("data 为 null 时脚本传 null", () =>
        {
            string? captured = null;
            var b = new HostBridge();
            b.UseExecutor(s => { captured = s; return Task.FromResult((true, "null", (string?)null)); });
            b.CallAsync<object?>("ping", null).GetAwaiter().GetResult();
            return captured!.EndsWith("(\"ping\",null)");
        });

        T("中文/特殊字符安全嵌入", () =>
        {
            string? captured = null;
            var b = new HostBridge();
            b.UseExecutor(s => { captured = s; return Task.FromResult((true, "null", (string?)null)); });
            b.CallAsync<object?>("a", new { text = "中文\"引号\"\r\n换行" }).GetAwaiter().GetResult();
            // 生成的脚本必须是合法 JS 字面量：反解析验证
            var jsonArg = captured!.Substring(captured!.IndexOf(",") + 1).TrimEnd(')');
            return JsonDocument.Parse(jsonArg).RootElement.GetProperty("text").GetString() == "中文\"引号\"\r\n换行";
        });

        T("前端抛异常 → Succeeded=false 带原因", () =>
        {
            var b = new HostBridge();
            b.UseExecutor(_ => Task.FromResult((false, (string?)null, (string?)"handler 报错")));
            try { b.CallAsync<int>("bad", null).GetAwaiter().GetResult(); return false; }
            catch (Exception e) { return e.Message.Contains("handler 报错"); }
        });

        T("超时 → TimeoutException", () =>
        {
            var b = new HostBridge { DefaultTimeout = TimeSpan.FromMilliseconds(150) };
            b.UseExecutor(_ => Task.Delay(2000).ContinueWith(_ => (true, "null", (string?)null)));
            try { b.CallAsync<int>("slow", null).GetAwaiter().GetResult(); return false; }
            catch (TimeoutException) { return true; }
        });

        T("未 Attach → 抛 InvalidOperationException", () =>
        {
            try { new HostBridge().CallAsync<int>("x", null).GetAwaiter().GetResult(); return false; }
            catch (InvalidOperationException e) { return e.Message.Contains("Attach"); }
        });

        T("CallAsync 无泛型重载：不阻塞且触发调用", () =>
        {
            var calls = new List<string>();
            var b = new HostBridge();
            b.UseExecutor(s => { lock (calls) calls.Add(s); return Task.FromResult((true, "null", (string?)null)); });
            b.CallAsync("notify", new { x = 1 });
            b.CallAsync("notify2", null);
            for (int i = 0; i < 50 && calls.Count < 2; i++) Thread.Sleep(10);
            return calls.Count == 2 && calls[0].Contains("notify") && calls[1].Contains("notify2");
        });

        T("CallAsync 无泛型重载：失败不抛到调用方", () =>
        {
            var b = new HostBridge();
            b.UseExecutor(_ => Task.FromResult((false, (string?)null, (string?)"页面未就绪")));
            b.CallAsync("fire", null); // 不应抛
            Thread.Sleep(50);
            return true;
        });

        T("并发调用各自结果正确", () =>
        {
            var b = new HostBridge();
            b.UseExecutor(s =>
            {
                var (action, data) = ParseCall(s);
                var v = JsonDocument.Parse(data!).RootElement.GetProperty("value").GetInt32();
                return Task.Delay(5 + v).ContinueWith(_ => (true, v.ToString(), (string?)null));
            });
            var tasks = Enumerable.Range(1, 8).Select(i => b.CallAsync<int>($"t{i}", new { value = i })).ToList();
            Task.WaitAll([.. tasks]);
            return tasks.Select((t, i) => t.Result == i + 1).All(x => x);
        });

        return (_pass, _fail);
    }

    /// <summary>从生成的脚本中解析出 action 与 data 两段 JSON</summary>
    private static (string action, string? data) ParseCall(string script)
    {
        var inner = script[HostBridge.HostRpcFunction.Length..].Trim();
        inner = inner[1..^1]; // 去掉外层括号
        var depth = 0; var split = -1; var inStr = false;
        for (int i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (inStr) { if (c == '"' && inner[i - 1] != '\\') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            else if (c == ',' && depth == 0) { split = i; break; }
        }
        var action = JsonDocument.Parse(inner[..split]).RootElement.GetString()!;
        var rest = inner[(split + 1)..].Trim();
        return (action, rest == "null" ? null : rest);
    }

    private class Result
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    private static void T(string name, Func<bool> f)
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
