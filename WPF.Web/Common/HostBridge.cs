using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace WPF.Web.Common
{
    /// <summary>
    /// 后端 → 前端调用中转服务（宿主桥），统一走 ExecuteScriptWithResultAsync：
    /// 前端在全局 window.hostRpc.onHostAction(action, data) 提供订阅总入口，返回值（含 Promise）即本次调用结果。
    /// 唯一对外入口 CallAsync：无泛型重载为单向通知（不等结果）；泛型重载 CallAsync&lt;T&gt; 为请求-响应，带超时兑底。
    /// 约束：CallAsync&lt;T&gt; 不得在 UI 线程上 await——脚本求值的完成需要 UI 消息循环运转，同线程等待必死锁；
    /// 后台线程调用无此限制（内部自动经 Dispatcher 发起）。
    /// </summary>
    public class HostBridge
    {
        /// <summary>脚本执行器签名：返回 (是否成功, 结果 JSON, 异常信息)。Attach 后为真实实现，测试可注入替身。</summary>
        public delegate Task<(bool Succeeded, string? ResultJson, string? Error)> ScriptExecutor(string script);

        private ScriptExecutor? _executor;
        private Dispatcher? _dispatcher;

        /// <summary>默认等待前端返回的超时。</summary>
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>前端约定的订阅总入口全局函数名。</summary>
        public const string HostRpcFunction = "window.hostRpc.onHostAction";

        private static readonly JsonSerializerOptions _json = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>WebView2 就绪后由 ShellWindowBase 调用，绑定脚本执行通道与 UI 调度器。</summary>
        public void Attach(CoreWebView2 core, Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _executor = script => ExecuteAsync(core, script);
        }

        /// <summary>替换脚本执行通道（测试场景）。</summary>
        public void UseExecutor(ScriptExecutor executor) => _executor = executor;

        /// <summary>
        /// 单向通知前端执行 action（不等待结果）：请求-响应请用泛型重载 <see cref="CallAsync{T}"/>。
        /// 失败（未 Attach/超时/前端异常）不抛给调用方，仅记调试输出。
        /// </summary>
        public void CallAsync(string action, object? data)
        {
            _ = CallAsync<object?>(action, data, timeout: TimeSpan.FromSeconds(10))
                .ContinueWith(t => System.Diagnostics.Debug.WriteLine($"HostBridge '{action}' 未成功：{t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// 调用前端 window.hostRpc.onHostAction(action, data) 并等待返回结果，反序列化为 T。
        /// 超时 / 前端未实现入口 / handler 抛异常均抛出 Exception。
        /// </summary>
        public async Task<T?> CallAsync<T>(string action, object? data, TimeSpan? timeout = null)
        {
            var executor = _executor ?? throw new InvalidOperationException("HostBridge 尚未 Attach WebView2");
            // 参数以严格 JSON 字面量嵌入调用表达式，无注入面
            var script = $"{HostRpcFunction}({JsonSerializer.Serialize(action, _json)},{JsonSerializer.Serialize(data, _json)})";
            // 经 Dispatcher 发起（不阻塞本线程），保证任意线程可调；InvokeAsync 返回嵌套 Task 需 Unwrap
            Task<(bool Succeeded, string? ResultJson, string? Error)> invoke;
            if (_dispatcher != null)
            {
                var outer = _dispatcher.InvokeAsync(() => executor(script));
                invoke = outer.Task.Unwrap();
            }
            else
            {
                invoke = executor(script);
            }
            var limit = timeout ?? DefaultTimeout;
            var done = await Task.WhenAny(invoke, Task.Delay(limit));
            if (done != invoke) throw new TimeoutException($"前端未在时限内响应 action '{action}'");
            var (succeeded, resultJson, error) = await invoke;
            if (!succeeded) throw new Exception($"前端执行 '{action}' 失败：{error ?? "未提供原因"}");
            return DeserializeResult<T>(resultJson);
        }

        private static async Task<(bool, string?, string?)> ExecuteAsync(CoreWebView2 core, string script)
        {
            var r = await core.ExecuteScriptWithResultAsync(script);
            if (r.Succeeded) return (true, r.ResultAsJson, null);
            return (false, null, r.Exception?.Message ?? "脚本执行失败");
        }

        private static T? DeserializeResult<T>(string? resultJson)
        {
            if (string.IsNullOrEmpty(resultJson)) return default;
            if (typeof(T) == typeof(string))
            {
                using var doc = JsonDocument.Parse(resultJson);
                return doc.RootElement.ValueKind == JsonValueKind.String ? (T?)(object?)doc.RootElement.GetString() : default;
            }
            return JsonSerializer.Deserialize<T>(resultJson, _json);
        }
    }
}
