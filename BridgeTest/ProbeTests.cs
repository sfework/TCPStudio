using System.Text.Json;

namespace BridgeTest;

/// <summary>桥接兼容性探针：覆盖各类传参形态与返回类型的测试目标类</summary>
public class ProbeService
{
    public enum Level { Low, Mid, High }

    public record Item(string Name, int Count);

    // ---- 无参 ----
    public string Ping() => "pong";
    public List<Item> GetItems() => [new("a", 1), new("b", 2)];

    // ---- 基础类型单参 ----
    public string Echo(string text) => text;
    public int Add(int a) => a + 1;
    public bool IsReady(bool flag) => flag;
    public Level ParseLevel(Level lv) => lv;
    public Guid RoundTripGuid(Guid g) => g;
    public DateTime RoundTripDate(DateTime d) => d;
    public int? NullableInt(int? n) => n;
    public List<string> ListParam(List<string> xs) => xs;
    public Item PocoParam(Item item) => item;
    public Dictionary<string, int> DictParam(Dictionary<string, int> d) => d;
    public string? NullString(string? s) => s;

    // ---- 多参 ----
    public string Join2(string a, int b) => $"{a}-{b}";
    public string JoinDefault(string a, string sep = "|", int times = 1) => string.Join(sep, Enumerable.Repeat(a, times));
    public int SumArr(int a, int b, int c) => a + b + c;

    // ---- 重载 ----
    public string Over(string s) => $"str:{s}";
    public string Over(int n) => $"int:{n}";
    public string Over() => "void";

    // ---- 返回类型 ----
    public bool BoolTrue() => true;
    public bool BoolFalse() => false;
    public int IntResult() => 42;
    public decimal DecimalResult() => 1.25m;
    public Level EnumResult() => Level.High;
    public void VoidResult() { }
    public string? NullResult() => null;
    public DateTime DateResult() => new(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc);

    // ---- 异步 ----
    public async Task<string> AsyncString()
    {
        await Task.Delay(10);
        return "async-ok";
    }
    public async Task<Item> AsyncPoco()
    {
        await Task.Delay(10);
        return new Item("x", 9);
    }
    public async Task AsyncVoid() => await Task.Delay(10);
    public Task<int> AsyncInt() => Task.FromResult(7);

    // ---- 异常 ----
    public void ThrowBiz(string msg) => throw new InvalidOperationException(msg);
    public string ThrowNested() => throw new Exception("nested-msg");

    // ---- 中文/转义 ----
    public string Chinese(string s) => "中文" + s;
    public string Quote(string s) => "\"" + s + "\"";
}

internal static class ProbeTests
{
    private static int _pass, _fail;
    private static readonly JsonSerializerOptions Opt = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static (int pass, int fail) Run()
    {
        var invoker = new WPF.Web.Common.SmartInvoker(new ProbeService());
        object? Call(string m, string? input) => invoker.InvokeCall(m, input);

        // ===== 无参 =====
        T("无参 string 返回", () => (string?)Call("Ping", null) == "pong");
        T("无参 空串入参", () => (string?)Call("Ping", "") == "pong");
        T("无参 POCO 集合→camelCase JSON", () =>
        {
            var json = (string?)Call("GetItems", null);
            var arr = JsonDocument.Parse(json!).RootElement;
            return arr[0].GetProperty("name").GetString() == "a" && arr[0].GetProperty("count").GetInt32() == 1;
        });

        // ===== 基础类型单参：裸文本 / JSON 标量 / 对象包裹 =====
        T("string 裸文本", () => (string?)Call("Echo", "hello") == "hello");
        T("string 带引号 JSON", () => (string?)Call("Echo", "\"hello\"") == "hello");
        T("string 对象同名属性", () => (string?)Call("Echo", "{\"text\":\"v1\"}") == "v1");
        T("string 对象唯一属性(参数名不同)", () => (string?)Call("Echo", "{\"anything\":\"v2\"}") == "v2");
        T("string null→null", () => Call("Echo", null) == null);
        T("string 空串", () => (string?)Call("Echo", "\"\"") == "");
        T("int 裸数字", () => (string?)Call("Add", "5") == "6");
        T("int JSON 数字", () => (string?)Call("Add", "5") == "6");
        T("int 对象包裹", () => (string?)Call("Add", "{\"a\":9}") == "10");
        T("int 非法→缺省0", () => (string?)Call("Add", "abc") == "1");
        T("bool true→JSON 字面量", () => (string?)Call("IsReady", "true") == "true");
        T("bool 返回 true→\"true\"", () => (string?)Call("BoolTrue", null) == "true");
        T("bool 返回 false→\"false\"", () => (string?)Call("BoolFalse", null) == "false");
        T("enum 名称入参", () => (string?)Call("ParseLevel", "\"high\"") == "2");
        T("enum 数字入参", () => (string?)Call("ParseLevel", "0") == "0");
        T("enum 返回→数字文本", () => (string?)Call("EnumResult", null) == "2");
        T("Guid 往返", () =>
        {
            var g = Guid.NewGuid().ToString();
            return (string?)Call("RoundTripGuid", $"\"{g}\"") == g;
        });
        T("DateTime ISO 入参/返回", () =>
        {
            var r = (string?)Call("RoundTripDate", "\"2026-10-18T09:30:00Z\"")!;
            return r.StartsWith("2026-10-18T09:30:00");
        });
        T("int? null→null 结果", () => Call("NullableInt", null) == null);
        T("int? 有值", () => (string?)Call("NullableInt", "3") == "3");
        T("string? null 参数", () => Call("NullString", "null") == null);
        T("List<string> 数组入参", () =>
        {
            var json = (string?)Call("ListParam", "[\"a\",\"b\"]");
            return JsonDocument.Parse(json!).RootElement.GetArrayLength() == 2;
        });
        T("POCO 入参", () =>
        {
            var json = (string?)Call("PocoParam", "{\"name\":\"n1\",\"count\":3}");
            var el = JsonDocument.Parse(json!).RootElement;
            return el.GetProperty("name").GetString() == "n1" && el.GetProperty("count").GetInt32() == 3;
        });
        T("Dictionary 入参", () =>
        {
            var json = (string?)Call("DictParam", "{\"k1\":1,\"k2\":2}");
            return JsonDocument.Parse(json!).RootElement.GetProperty("k1").GetInt32() == 1;
        });
        T("中文不转义", () => (string?)Call("Chinese", "\"x\"") == "中文x");
        T("引号内容原样透传", () => (string?)Call("Quote", "\"a\"") == "\"a\"");

        // ===== 多参 =====
        T("多参 对象按名", () => (string?)Call("Join2", "{\"a\":\"x\",\"b\":2}") == "x-2");
        T("多参 对象名大小写不敏感", () => (string?)Call("Join2", "{\"A\":\"x\",\"B\":2}") == "x-2");
        T("多参 数组按位置", () => (string?)Call("Join2", "[\"y\",3]") == "y-3");
        T("多参 缺失用默认值", () => (string?)Call("JoinDefault", "{\"a\":\"z\"}") == "z");
        T("多参 部分默认值", () => (string?)Call("JoinDefault", "{\"a\":\"z\",\"times\":2}") == "z|z");
        T("多参 3 数字数组", () => (string?)Call("SumArr", "[1,2,3]") == "6");
        T("多参 空输入→全默认", () => (string?)Call("JoinDefault", "") == "");
        T("多参 裸文本→异常提示", () => Throws(() => Call("Join2", "abc"), "个参数"));

        // ===== 重载 =====
        T("重载 string→str", () => (string?)Call("Over", "\"s\"") == "str:s");
        T("重载 int→int", () => (string?)Call("Over", "12") == "int:12");
        T("重载 空→void 版", () => (string?)Call("Over", "") == "void");

        // ===== 返回类型 =====
        T("int 返回", () => (string?)Call("IntResult", null) == "42");
        T("decimal 返回", () => (string?)Call("DecimalResult", null) == "1.25");
        T("void 返回→null", () => Call("VoidResult", null) == null);
        T("null 返回→null", () => Call("NullResult", null) == null);
        T("DateTime 返回 ISO", () => (string?)Call("DateResult", null) == "2026-10-18T09:30:00.0000000Z");

        // ===== 异步 =====
        T("Task<string>", () => (string?)Call("AsyncString", null) == "async-ok");
        T("Task<POCO>→camelCase JSON", () =>
        {
            var json = (string?)Call("AsyncPoco", null);
            return JsonDocument.Parse(json!).RootElement.GetProperty("count").GetInt32() == 9;
        });
        T("Task 无结果→null", () => Call("AsyncVoid", null) == null);
        T("Task<int>", () => (string?)Call("AsyncInt", null) == "7");

        // ===== 异常解包 =====
        T("业务异常信息可见", () => Throws(() => Call("ThrowBiz", "\"业务错\""), "业务错"));
        T("嵌套异常解包", () => Throws(() => Call("ThrowNested", null), "nested-msg"));

        // ===== 方法查找 =====
        T("未知方法→抛异常", () => Throws(() => Call("Nope", null), "未找到"));
        T("方法名大小写不敏感", () => (string?)Call("ping", null) == "pong");

        return (_pass, _fail);
    }

    private static bool Throws(Func<object?> f, string contains)
    {
        try { f(); return false; }
        catch (Exception e) { return e.Message.Contains(contains); }
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
