using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TCPStudio.Services
{
    /// <summary>
    /// JSON 文件存储：程序运行目录\Data\{connections.json, presets.json, records\{connectionId}.json}。
    /// 写入采用"临时文件+替换"防损坏；记录文件按整表重写（调用方防抖）。
    /// </summary>
    public class JsonDataStore
    {
        private static readonly JsonSerializerOptions _options = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        /// <summary>记录逐行存储用紧凑选项</summary>
        private static readonly JsonSerializerOptions _compact = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly string _root;
        private readonly string _recordDir;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public JsonDataStore(string? root = null)
        {
            _root = root ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            _recordDir = Path.Combine(_root, "records");
            Directory.CreateDirectory(_recordDir);
        }

        public List<ConnectionConfig> LoadConnections()
            => Read<List<ConnectionConfig>>(Path.Combine(_root, "connections.json")) ?? [];

        public Task SaveConnectionsAsync(List<ConnectionConfig> list)
            => WriteAsync(Path.Combine(_root, "connections.json"), list);

        public List<Preset> LoadPresets()
            => Read<List<Preset>>(Path.Combine(_root, "presets.json")) ?? [];

        public Task SavePresetsAsync(List<Preset> list)
            => WriteAsync(Path.Combine(_root, "presets.json"), list);

        public List<MessageRecord> LoadRecords(string connectionId)
        {
            var path = RecordPath(connectionId);
            if (!File.Exists(path)) return [];
            try
            {
                var list = new List<MessageRecord>();
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var item = JsonSerializer.Deserialize<MessageRecord>(line, _options);
                    if (item != null) list.Add(item);
                }
                return list;
            }
            catch (Exception)
            {
                return [];
            }
        }

        /// <summary>增量追加记录（每行一条 JSON，不重写全文件）。</summary>
        public async Task AppendRecordsAsync(string connectionId, IEnumerable<MessageRecord> records)
        {
            var batch = records as IList<MessageRecord> ?? records.ToList();
            if (batch.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            foreach (var r in batch)
            {
                sb.AppendLine(JsonSerializer.Serialize(r, _compact));
            }
            await _writeLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(RecordPath(connectionId), sb.ToString());
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>全量重写记录文件（删除单条/清空后保持一致）。</summary>
        public Task RewriteRecordsAsync(string connectionId, List<MessageRecord> list)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in list)
            {
                sb.AppendLine(JsonSerializer.Serialize(r, _compact));
            }
            return WriteTextAsync(RecordPath(connectionId), sb.ToString());
        }

        public void DeleteRecords(string connectionId)
        {
            var path = RecordPath(connectionId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private string RecordPath(string connectionId)
            => Path.Combine(_recordDir, $"{Sanitize(connectionId)}.jsonl");

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private T? Read<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), _options);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task WriteAsync<T>(string path, T data)
            => await WriteTextAsync(path, JsonSerializer.Serialize(data, _options));

        private async Task WriteTextAsync(string path, string content)
        {
            await _writeLock.WaitAsync();
            try
            {
                var tmp = path + ".tmp";
                await File.WriteAllTextAsync(tmp, content);
                if (File.Exists(path))
                {
                    File.Replace(tmp, path, null, true);
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
