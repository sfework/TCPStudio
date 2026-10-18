using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WPF.Web.Common
{
    /// <summary>
    /// WebView2 HostObject 桥接调用器：前端统一走 InvokeCall(方法名, 入参)，入参支持常见调用形态并自动绑定：
    /// 无参（空/null）；基础类型单参（裸文本 abc、JSON 标量 "abc"/123/true/null）；
    /// 对象单参（JSON 对象 -> POCO/集合/嵌套；目标为基础参数时优先同名属性、其次唯一属性）；
    /// 多参（JSON 对象按参数名不区分大小写匹配、支持 C# 默认值；JSON 数组按位置）；同名重载按入参形态自动选择。
    /// 返回：基础类型转文本（bool 为 JSON 字面量 true/false，数字保持原样，DateTime 为 ISO8601）；
    /// 复杂类型序列化为 camelCase JSON 字符串。Task/Task&lt;T&gt; 自动解包等待。
    /// 业务异常从 TargetInvocationException 中解包后抛出，前端可见原始错误信息。
    /// 注意：桥接调用发生在 UI 线程，异步方法内部须 ConfigureAwait(false) 或将阻塞 IO 调度到线程池，避免死锁。
    /// </summary>
    public class SmartInvoker
    {
        private readonly object context;
        // 方法名 -> 重载列表（OrdinalIgnoreCase：兼容前端大小写差异）
        private readonly Dictionary<string, List<MethodModel>> methodDic = new(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        public SmartInvoker(object _context)
        {
            var type = _context.GetType();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (MethodInfo method in methods)
            {
                // 属性访问器（get_/set_）等不暴露给前端
                if (method.IsSpecialName) continue;
                var model = new MethodModel
                {
                    Method = method,
                    Name = method.Name,
                    ReturnType = UnwrapTaskType(method.ReturnType),
                };
                model.Parameters.AddRange(method.GetParameters());
                if (!methodDic.TryGetValue(model.Name, out var list))
                {
                    list = [];
                    methodDic[model.Name] = list;
                }
                list.Add(model);
            }
            context = _context;
        }

        /// <summary>
        /// 调用方法并返回结果。
        /// </summary>
        /// <returns>
        /// 如果是基础类型，返回文本形式；如果是复杂类型，返回 JSON 字符串。
        /// </returns>
        public object? InvokeCall(string methodName, string? jsonInput)
        {
            if (!methodDic.TryGetValue(methodName, out var models) || models.Count == 0)
            {
                throw new Exception($"方法 '{methodName}' 未找到");
            }
            var input = (jsonInput ?? "").Trim();
            var root = ParseInput(input);
            var method = SelectOverload(models, input, root);
            var args = BindArguments(method, input, root);
            object? raw;
            try
            {
                raw = method.Method.Invoke(context, args);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                // 解包反射异常，前端拿到业务错误文案而非 TargetInvocationException
                throw new Exception(tie.InnerException.Message, tie.InnerException);
            }
            raw = UnwrapTask(raw, method);
            return ProcessResult(raw, method.ReturnType);
        }

        /// <summary>尝试按 JSON 解析入参；非 JSON（裸文本）返回 null，绑定阶段按原始字符串处理</summary>
        private static JsonNode? ParseInput(string input)
        {
            if (input.Length == 0) return null;
            var c = input[0];
            if (c is '{' or '[' or '"' or 't' or 'f' or 'n' or (>= '0' and <= '9') or '-')
            {
                try { return JsonNode.Parse(input); } catch (JsonException) { return null; }
            }
            return null;
        }

        /// <summary>多重载时按入参形态选择：空参→0 个参数；数组→长度相等；对象→参数名全匹配且数量足够；否则取第一个</summary>
        private static MethodModel SelectOverload(List<MethodModel> models, string input, JsonNode? root)
        {
            if (models.Count == 1) return models[0];
            foreach (var m in models)
            {
                var n = m.Parameters.Count;
                if (n == 0 && input.Length == 0) return m;
                if (n == 1 && root is JsonValue sc && AcceptsScalar(sc, m.Parameters[0].ParameterType)) return m;
                if (root is JsonArray arr && n == arr.Count) return m;
                if (root is JsonObject obj && obj.Count > 0 && n >= obj.Count
                    && obj.Select(kv => kv.Key).All(k => m.Parameters.Any(x =>
                        string.Equals(x.Name, k, StringComparison.OrdinalIgnoreCase))))
                {
                    return m;
                }
            }
            return models[0];
        }

        /// <summary>标量入参能否绑定到目标基础类型（重载选择用）：string 要带引号，其余类型收未加引号的数字/布尔字面量</summary>
        private static bool AcceptsScalar(JsonValue v, Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            var isJsonString = v.ToJsonString()[0] == '"';
            if (t == typeof(string)) return isJsonString;
            if (isJsonString) return false;
            if (t == typeof(bool))
            {
                var s = v.ToJsonString();
                return s == "true" || s == "false";
            }
            return (t.IsPrimitive && t != typeof(char) && t != typeof(bool))
                || t == typeof(decimal) || t.IsEnum
                || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateTimeOffset)
                || t == typeof(TimeSpan) || t == typeof(DateOnly) || t == typeof(TimeOnly);
        }

        private static object?[] BindArguments(MethodModel method, string input, JsonNode? root)
        {
            var ps = method.Parameters;
            if (ps.Count == 0) return [];
            if (ps.Count == 1) return [BindSingle(ps[0], input, root)];
            // 多参数：数组按位置；对象按参数名（缺失用默认值/类型缺省）；空输入全部默认值
            if (root is JsonArray pos && pos.Count == ps.Count)
            {
                var byPos = new object?[ps.Count];
                for (int i = 0; i < ps.Count; i++) byPos[i] = ToArg(pos[i], ps[i].ParameterType);
                return byPos;
            }
            if (root is JsonObject obj)
            {
                var byName = new object?[ps.Count];
                for (int i = 0; i < ps.Count; i++)
                {
                    var v = GetPropIgnoreCase(obj, ps[i].Name ?? "");
                    if (v != null || HasProp(obj, ps[i].Name ?? ""))
                    {
                        byName[i] = ToArg(v, ps[i].ParameterType);
                    }
                    else if (ps[i].HasDefaultValue)
                    {
                        byName[i] = ps[i].DefaultValue;
                    }
                    else
                    {
                        byName[i] = DefaultOf(ps[i].ParameterType);
                    }
                }
                return byName;
            }
            if (input.Length == 0)
            {
                return [.. ps.Select(p => p.HasDefaultValue ? p.DefaultValue : DefaultOf(p.ParameterType))];
            }
            throw new Exception($"方法 '{method.Name}' 有 {ps.Count} 个参数，前端需传 JSON 对象（按参数名）或数组（按位置）");
        }

        /// <summary>单参数绑定：兼容裸文本、JSON 标量、对象包单值（{ id: 'x' }）、POCO/集合</summary>
        private static object? BindSingle(ParameterInfo param, string input, JsonNode? root)
        {
            var t = param.ParameterType;
            switch (root)
            {
                case null: // 空输入、非 JSON 裸文本，或 JSON null 字面量
                    if (input.Equals("null", StringComparison.OrdinalIgnoreCase)) return DefaultOf(t);
                    return ConvertTo(input, t);
                case JsonObject obj:
                    if (!IsPrimitive(t)) return obj.Deserialize(t, _jsonOptions);
                    // 基础类型参数 + 对象入参：优先同名属性，其次唯一属性
                    var named = GetPropIgnoreCase(obj, param.Name ?? "");
                    if (named != null || HasProp(obj, param.Name ?? "")) return ToArg(named, t);
                    if (obj.Count == 1) return ToArg(obj.First().Value, t);
                    throw new Exception($"无法从对象入参绑定参数 '{param.Name}'：请改传基础类型值，或对象内含同名属性");
                case JsonArray arr:
                    if (!IsPrimitive(t)) return arr.Deserialize(t, _jsonOptions);
                    if (arr.Count == 1) return ToArg(arr[0], t);
                    throw new Exception($"参数 '{param.Name}' 为 {t.Name}，不接受数组入参");
                default: // JsonValue 标量：字符串自动去引号，数字/布尔按字面转换
                    return ToArg(root, t);
            }
        }

        /// <summary>JSON 节点 -> 参数值：基础类型取原始文本统一转换，复杂类型按 JSON 反序列化</summary>
        private static object? ToArg(JsonNode? node, Type type)
        {
            if (node is null) return DefaultOf(type);
            if (node is JsonValue jv)
            {
                // JSON 字符串标量取去引号原文；数字/布尔用字面文本
                if (jv.TryGetValue<string>(out var s))
                {
                    return type == typeof(string) || !IsPrimitive(type) ? s : ConvertTo(s, type);
                }
                if (IsPrimitive(type)) return ConvertTo(jv.ToJsonString(), type);
            }
            if (IsPrimitive(type)) return ConvertTo(node.ToString(), type);
            return node.Deserialize(type, _jsonOptions);
        }

        private static JsonNode? GetPropIgnoreCase(JsonObject obj, string name)
        {
            foreach (var p in obj)
            {
                if (string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            }
            return null;
        }

        private static bool HasProp(JsonObject obj, string name) =>
            obj.Any(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase));

        private static object? DefaultOf(Type t) =>
            t.IsValueType && Nullable.GetUnderlyingType(t) == null ? Activator.CreateInstance(t) : null;

        /// <summary>Task / Task&lt;T&gt; 解包为同步结果（见类注释的 UI 线程死锁约束）</summary>
        private static Type UnwrapTaskType(Type returnType)
        {
            if (returnType == typeof(Task)) return typeof(void);
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                return returnType.GetGenericArguments()[0];
            }
            return returnType;
        }

        private static object? UnwrapTask(object? raw, MethodModel method)
        {
            if (raw is not Task task) return raw;
            task.GetAwaiter().GetResult();
            if (method.Method.ReturnType.IsGenericType)
            {
                return method.Method.ReturnType.GetProperty("Result")!.GetValue(task);
            }
            return null;
        }

        private static object? ProcessResult(object? result, Type type)
        {
            if (result == null) return null;
            if (IsPrimitive(type))
            {
                return ConvertPrimitiveResult(result, type);
            }
            return JsonSerializer.Serialize(result, _jsonOptions);
        }

        private static object? ConvertTo(string? input, Type type)
        {
            if (IsPrimitive(type))
            {
                return ConvertPrimitive(input, type);
            }
            return ConvertFromString(input, type);
        }

        private static bool IsPrimitive(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>) && Nullable.GetUnderlyingType(type) != null)
            {
                return IsPrimitive(Nullable.GetUnderlyingType(type)!);
            }
            return type.IsPrimitive
                || type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(Guid)
                || type == typeof(DateTime)
                || type == typeof(DateTimeOffset)
                || type == typeof(TimeSpan)
                || type == typeof(DateOnly)
                || type == typeof(TimeOnly)
                || type.IsEnum;
        }

        private static object? ConvertPrimitive(string? input, Type type)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return DefaultOf(type);
            }
            try
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    var underlyingType = Nullable.GetUnderlyingType(type)!;
                    return ConvertPrimitive(input, underlyingType);
                }
                if (type == typeof(string))
                {
                    // 整段是带引号的 JSON 字符串时去掉引号（兼容前端 JSON.stringify 后的标量）
                    if (input.Length >= 2 && input[0] == '"' && input[^1] == '"')
                    {
                        try { return JsonNode.Parse(input)?.GetValue<string>(); } catch (JsonException) { }
                    }
                    return input;
                }
                if (type.IsEnum)
                {
                    return Enum.Parse(type, input, ignoreCase: true);
                }
                if (type == typeof(DateOnly))
                {
                    return DateOnly.Parse(input, CultureInfo.InvariantCulture);
                }
                if (type == typeof(TimeOnly))
                {
                    return TimeOnly.Parse(input, CultureInfo.InvariantCulture);
                }
                if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
                {
                    // 走 JSON 语义解析：保留 Z/+08:00 的 Kind 信息，避免 TypeDescriptor 转本地时间造成往返失真
                    var doc = JsonDocument.Parse(input.StartsWith('"') ? input : $"\"{input}\"");
                    return doc.RootElement.Deserialize(type, _jsonOptions);
                }
                var converter = TypeDescriptor.GetConverter(type);
                if (converter.CanConvertFrom(typeof(string)))
                {
                    return converter.ConvertFromInvariantString(input);
                }
                return Convert.ChangeType(input, type, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // 转换失败回落类型缺省值（避免向值类型参数传 null 造成 Invoke 二次异常）
                return DefaultOf(type);
            }
        }

        private static object? ConvertPrimitiveResult(object input, Type type)
        {
            try
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    var underlyingType = Nullable.GetUnderlyingType(type)!;
                    return ConvertPrimitiveResult(input, underlyingType);
                }
                if (type == typeof(bool))
                {
                    // JSON 字面量：前端 convertToResult 直接还原为 true/false，规避 "True" 真值坑
                    return ((bool)input) ? "true" : "false";
                }
                if (type.IsEnum)
                {
                    return Convert.ToInt32(input).ToString(CultureInfo.InvariantCulture);
                }
                if (type == typeof(DateTime))
                {
                    return ((DateTime)input).ToString("O", CultureInfo.InvariantCulture); // 使用 ISO 8601 格式
                }
                if (type == typeof(DateTimeOffset))
                {
                    return ((DateTimeOffset)input).ToString("O", CultureInfo.InvariantCulture);
                }
                if (type == typeof(DateOnly))
                {
                    return ((DateOnly)input).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                if (type == typeof(TimeOnly))
                {
                    return ((TimeOnly)input).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                }
                if (type == typeof(TimeSpan))
                {
                    return ((TimeSpan)input).ToString("c", CultureInfo.InvariantCulture);
                }
                return Convert.ToString(input, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static object? ConvertFromString(string? input, Type targetType)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return DefaultOf(targetType);
            }

            try
            {
                return JsonSerializer.Deserialize(input, targetType, _jsonOptions);
            }
            catch (JsonException)
            {
                return DefaultOf(targetType);
            }
        }

        public class MethodModel
        {
            public MethodInfo Method { get; set; } = null!;
            public List<ParameterInfo> Parameters { get; set; } = [];
            public string Name { get; set; } = null!;
            /// <summary>Task&lt;T&gt; 已解包为 T；Task 为 void</summary>
            public Type ReturnType { get; set; } = null!;
        }
    }
}
