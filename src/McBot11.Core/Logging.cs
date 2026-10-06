using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using McBot11.Auth;
using McBot11.Logging;

namespace McBot11.Core
{
    /// <summary>
    /// 文件 + UI 双通道日志（自研，按天切分 logs\mcbot-yyyyMMdd.log）。
    /// 线程安全；写文件失败不影响 UI 通道。
    /// </summary>
    public sealed class FileLogger : IAppLogger
    {
        private readonly object _lock = new object();
        private readonly string _dir;
        private string _currentDate = "";
        private StreamWriter? _writer;

        public FileLogger(string? dir = null)
        {
            _dir = dir ?? ConfigStore.LogDir;
            try { Directory.CreateDirectory(_dir); } catch { }
        }

        /// <summary>UI 订阅：级别、来源、正文、完整格式行。</summary>
        public event Action<LogLevel, string, string, string>? Logged;

        // 便捷方法（IAppLogger 默认接口方法只能经接口类型调用，这里提供类级便捷封装）
        public void Debug(string source, string message) => Log(LogLevel.Debug, source, message);
        public void Info(string source, string message) => Log(LogLevel.Info, source, message);
        public void Warn(string source, string message) => Log(LogLevel.Warn, source, message);
        public void Error(string source, string message, Exception? ex = null)
            => Log(LogLevel.Error, source, ex == null ? message : message + " | " + ex);

        public void Log(LogLevel level, string source, string message)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelTag(level)}] [{source}] {message}";
            string ui = $"{DateTime.Now:HH:mm:ss} [{LevelTag(level)}] [{source}] {message}";
            try
            {
                lock (_lock)
                {
                    EnsureWriter();
                    _writer?.WriteLine(line);
                    _writer?.Flush();
                }
            }
            catch
            {
                // 日志文件写入失败不打断业务
            }
            try { Logged?.Invoke(level, source, message, ui); }
            catch { }
        }

        private void EnsureWriter()
        {
            string date = DateTime.Now.ToString("yyyyMMdd");
            if (_writer != null && date == _currentDate) return;
            try { _writer?.Dispose(); } catch { }
            string path = Path.Combine(_dir, $"mcbot-{date}.log");
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
            {
                AutoFlush = false
            };
            _currentDate = date;
        }

        private static string LevelTag(LogLevel level) => level switch
        {
            LogLevel.Debug => "DBG ",
            LogLevel.Info => "INFO",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERR ",
            _ => "INFO"
        };

        public void DisposeWriter()
        {
            lock (_lock)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }

    /// <summary>
    /// config\tokens.json 文件缓存（不含密码）。
    /// accessToken / clientToken / refreshToken 落盘前经 DPAPI(CurrentUser) 加密；
    /// 读取时先解密，失败则按旧版明文直接使用（兼容既有文件，下次保存自动转加密）。
    /// 加锁读写 + 原子替换，避免并发写坏文件。
    /// </summary>
    public sealed class FileTokenCache : ITokenCache
    {
        private sealed class TokenProfile
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")] public string? Id { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
        }

        private sealed class TokenFileEntry
        {
            [System.Text.Json.Serialization.JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("clientToken")] public string? ClientToken { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("selectedProfile")] public TokenProfile? SelectedProfile { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("availableProfiles")] public List<TokenProfile>? AvailableProfiles { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("root")] public string? Root { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("account")] public string? Account { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }

            // 旧版平铺字段（只读兼容，写出时不再产生）
            [System.Text.Json.Serialization.JsonPropertyName("profileId")] public string? LegacyProfileId { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("profileName")] public string? LegacyProfileName { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("savedAt")] public DateTimeOffset? LegacySavedAt { get; set; }
        }

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly string _path;
        private readonly object _lock = new object();
        private Dictionary<string, CachedToken>? _data;

        public FileTokenCache(string? path = null)
        {
            _path = path ?? ConfigStore.TokensPath;
        }

        /// <summary>解密或（旧版明文时）原样返回。</summary>
        private static string? Decode(string? value) => SecureStore.UnprotectToken(value) ?? value;

        private Dictionary<string, CachedToken> LoadAll()
        {
            if (_data != null) return _data;
            _data = new Dictionary<string, CachedToken>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(_path))
                {
                    string json = File.ReadAllText(_path);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, TokenFileEntry>>(json);
                    if (loaded != null)
                        foreach (var kv in loaded)
                        {
                            var en = kv.Value;
                            if (en == null) continue;
                            _data[kv.Key] = new CachedToken
                            {
                                AccessToken = Decode(en.AccessToken) ?? "",
                                ClientToken = Decode(en.ClientToken),
                                RefreshToken = Decode(en.RefreshToken),
                                ProfileId = en.SelectedProfile?.Id ?? en.LegacyProfileId,
                                ProfileName = en.SelectedProfile?.Name ?? en.LegacyProfileName,
                                AvailableProfiles = ToProfiles(en.AvailableProfiles),
                                Root = en.Root,
                                Account = en.Account ?? "",
                                SavedAt = en.UpdatedAt ?? en.LegacySavedAt ?? DateTimeOffset.UtcNow
                            };
                        }
                }
            }
            catch
            {
                // tokens.json 损坏时忽略（将按需重建）
                try
                {
                    if (File.Exists(_path))
                        File.Copy(_path, _path + ".broken", overwrite: true);
                }
                catch { }
                _data.Clear();
            }
            return _data;
        }

        public CachedToken? Load(string key)
        {
            lock (_lock)
                return LoadAll().TryGetValue(key, out var token) ? token : null;
        }

        public void Save(string key, CachedToken token)
        {
            lock (_lock)
            {
                LoadAll()[key] = token;
                Persist();
            }
        }

        public void Remove(string key)
        {
            lock (_lock)
            {
                LoadAll().Remove(key);
                Persist();
            }
        }

        private static List<AuthProfile>? ToProfiles(List<TokenProfile>? list)
        {
            if (list == null || list.Count == 0) return null;
            var result = new List<AuthProfile>(list.Count);
            foreach (var p in list)
                if (p != null && !string.IsNullOrEmpty(p.Id))
                    result.Add(new AuthProfile { Id = p.Id!, Name = p.Name ?? "" });
            return result.Count == 0 ? null : result;
        }

        private static List<TokenProfile>? ToTokenProfiles(List<AuthProfile>? list)
        {
            if (list == null || list.Count == 0) return null;
            var result = new List<TokenProfile>(list.Count);
            foreach (var p in list)
                if (p != null && !string.IsNullOrEmpty(p.Id))
                    result.Add(new TokenProfile { Id = p.Id, Name = p.Name });
            return result.Count == 0 ? null : result;
        }

        private void Persist()
        {
            try
            {
                var file = new Dictionary<string, TokenFileEntry>(StringComparer.Ordinal);
                foreach (var kv in _data)
                {
                    var t = kv.Value;
                    file[kv.Key] = new TokenFileEntry
                    {
                        AccessToken = string.IsNullOrEmpty(t.AccessToken) ? null : SecureStore.ProtectToken(t.AccessToken),
                        ClientToken = string.IsNullOrEmpty(t.ClientToken) ? null : SecureStore.ProtectToken(t.ClientToken),
                        RefreshToken = string.IsNullOrEmpty(t.RefreshToken) ? null : SecureStore.ProtectToken(t.RefreshToken),
                        SelectedProfile = string.IsNullOrEmpty(t.ProfileId) && string.IsNullOrEmpty(t.ProfileName)
                            ? null
                            : new TokenProfile { Id = t.ProfileId, Name = t.ProfileName },
                        AvailableProfiles = ToTokenProfiles(t.AvailableProfiles),
                        Root = t.Root,
                        Account = string.IsNullOrEmpty(t.Account) ? null : t.Account,
                        UpdatedAt = t.SavedAt
                    };
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string json = JsonSerializer.Serialize(file, WriteOptions);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, _path, overwrite: true);
            }
            catch
            {
                // 缓存写失败不阻断登录
            }
        }
    }

    /// <summary>
    /// 回环过滤：QQ→MC 发出的消息记录指纹（规范化后哈希），
    /// 服务器把它广播回来时命中指纹则不再转发回 QQ。窗口默认 15 秒。
    /// </summary>
    public sealed class LoopbackFilter
    {
        private readonly int _windowSeconds;
        private readonly object _lock = new object();
        private readonly Dictionary<string, long> _seen = new Dictionary<string, long>(StringComparer.Ordinal);

        public LoopbackFilter(int windowSeconds = 15)
        {
            _windowSeconds = windowSeconds;
        }

        private static string Normalize(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            bool prevSpace = false;
            foreach (char c in text.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!prevSpace) sb.Append(' ');
                    prevSpace = true;
                }
                else
                {
                    sb.Append(c);
                    prevSpace = false;
                }
            }
            return sb.ToString();
        }

        private static string Fingerprint(string normalized)
        {
            // FNV-1a 64（短小无依赖；碰撞窗口仅 15 秒内同文本判定，无安全影响）
            ulong hash = 14695981039346656037UL;
            foreach (char c in normalized)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16") + ":" + normalized.Length;
        }

        /// <summary>记录一条"已送入 MC"的消息。</summary>
        public void Remember(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string fp = Fingerprint(Normalize(text));
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (_lock)
            {
                Prune(now);
                _seen[fp] = now;
            }
        }

        /// <summary>检查 MC 广播消息是否为刚才 QQ 转入的回环（命中则应丢弃）。</summary>
        public bool ShouldDrop(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string fp = Fingerprint(Normalize(text));
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (_lock)
            {
                Prune(now);
                return _seen.ContainsKey(fp);
            }
        }

        private void Prune(long now)
        {
            if (_seen.Count == 0) return;
            List<string>? stale = null;
            foreach (var kv in _seen)
            {
                if (now - kv.Value > _windowSeconds)
                    (stale ??= new List<string>()).Add(kv.Key);
            }
            if (stale != null)
                foreach (var k in stale)
                    _seen.Remove(k);
        }
    }
}
