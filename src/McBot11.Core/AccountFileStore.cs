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
    /// config\accounts.json 账号池存取（加锁 + 原子写 + 损坏备份）。
    /// 密码字段由 AccountEntry 内部以 DPAPI 加密后落盘；读写仅在锁内进行。
    /// </summary>
    public sealed class AccountFileStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private readonly string _path;
        private readonly ITokenCache? _tokens;
        private readonly IAppLogger? _log;
        private readonly object _lock = new object();
        private AccountPool? _pool;

        public AccountFileStore(string? path = null, ITokenCache? tokens = null, IAppLogger? log = null)
        {
            _path = path ?? ConfigStore.AccountsPath;
            _tokens = tokens;
            _log = log;
        }

        private AccountPool LoadLocked()
        {
            if (_pool != null) return _pool;
            _pool = new AccountPool();
            try
            {
                if (File.Exists(_path))
                {
                    string json = File.ReadAllText(_path);
                    var loaded = JsonSerializer.Deserialize<AccountPool>(json, Options);
                    if (loaded != null) _pool = loaded;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(_path)) File.Copy(_path, _path + ".broken", overwrite: true);
                }
                catch { }
                _log?.Warn("Accounts", "accounts.json 解析失败（已备份 .broken 并按空账号池重建）: " + ex.Message);
                _pool = new AccountPool();
            }
            _pool.Accounts ??= new List<AccountEntry>();
            return _pool;
        }

        private void PersistLocked()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string json = JsonSerializer.Serialize(_pool, Options);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, _path, overwrite: true);
        }

        /// <summary>账号列表快照（按显示名排序，便于 UI）。</summary>
        public List<AccountEntry> List()
        {
            lock (_lock)
            {
                var list = new List<AccountEntry>(LoadLocked().Accounts);
                list.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
                return list;
            }
        }

        public AccountEntry? Get(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock)
                return LoadLocked().Accounts.Find(a => a.Id == id);
        }

        /// <summary>当前选中账号（优先指定 ID，其次池内 CurrentAccountId）。</summary>
        public AccountEntry? Current(string? preferredId)
        {
            lock (_lock)
            {
                var pool = LoadLocked();
                var byPreferred = GetUnlocked(pool, preferredId);
                if (byPreferred != null) return byPreferred;
                return GetUnlocked(pool, pool.CurrentAccountId);
            }
        }

        private static AccountEntry? GetUnlocked(AccountPool pool, string? id)
            => string.IsNullOrEmpty(id) ? null : pool.Accounts.Find(a => a.Id == id);

        /// <summary>新增或按 ID 覆盖更新。</summary>
        public void Upsert(AccountEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            lock (_lock)
            {
                var pool = LoadLocked();
                int idx = pool.Accounts.FindIndex(a => a.Id == entry.Id);
                if (idx >= 0) pool.Accounts[idx] = entry;
                else pool.Accounts.Add(entry);
                if (string.IsNullOrEmpty(pool.CurrentAccountId)) pool.CurrentAccountId = entry.Id;
                PersistLocked();
            }
        }

        /// <summary>删除账号（同时清除 tokens.json 中对应条目）。</summary>
        public void Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_lock)
            {
                var pool = LoadLocked();
                if (pool.Accounts.RemoveAll(a => a.Id == id) == 0) return;
                if (string.Equals(pool.CurrentAccountId, id, StringComparison.Ordinal))
                    pool.CurrentAccountId = pool.Accounts.Count > 0 ? pool.Accounts[0].Id : null;
                PersistLocked();
            }
            try { _tokens?.Remove(id); }
            catch (Exception ex) { _log?.Warn("Accounts", "删除账号 token 失败: " + ex.Message); }
            _log?.Info("Accounts", "账号已删除: " + id);
        }

        /// <summary>设置当前用于 MC 登录的账号并落盘。</summary>
        public void SetCurrent(string? id)
        {
            lock (_lock)
            {
                var pool = LoadLocked();
                if (!string.IsNullOrEmpty(id) && GetUnlocked(pool, id) == null) return;
                if (pool.CurrentAccountId == id) return;
                pool.CurrentAccountId = id;
                PersistLocked();
            }
            _log?.Info("Accounts", "当前登录账号已切换: " + (id ?? "(无)"));
        }

        /// <summary>登录成功后回写上次登录信息（时间/角色名/UUID）。</summary>
        public void MarkLoginSuccess(string? id, string? profileName, string? profileUuid)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_lock)
            {
                var pool = LoadLocked();
                var acc = GetUnlocked(pool, id);
                if (acc == null) return;
                acc.LastLoginTime = DateTimeOffset.UtcNow;
                if (!string.IsNullOrEmpty(profileName)) acc.LastProfileName = profileName;
                if (!string.IsNullOrEmpty(profileUuid)) acc.LastProfileUuid = profileUuid;
                PersistLocked();
            }
        }

        /// <summary>选定登录角色后回写 accounts.json（selectedProfileId / selectedProfileName）。</summary>
        public void MarkSelectedProfile(string? id, string profileId, string? profileName)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(profileId)) return;
            lock (_lock)
            {
                var pool = LoadLocked();
                var acc = GetUnlocked(pool, id);
                if (acc == null) return;
                acc.SelectedProfileId = profileId;
                acc.SelectedProfileName = profileName;
                PersistLocked();
            }
        }

        /// <summary>清除选定角色（角色失效/凭据变更时强制重新获取）。</summary>
        public void ClearSelectedProfile(string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_lock)
            {
                var pool = LoadLocked();
                var acc = GetUnlocked(pool, id);
                if (acc == null) return;
                if (acc.SelectedProfileId == null && acc.SelectedProfileName == null) return;
                acc.SelectedProfileId = null;
                acc.SelectedProfileName = null;
                PersistLocked();
            }
        }

        /// <summary>
        /// 平滑迁移：accounts.json 不存在但 config.json 中有 yggdrasil 账号时，
        /// 自动生成一条账号记录（密码 DPAPI 加密），并把旧 token 缓存键（root|email）
        /// 迁移到账号 ID 键下。
        /// </summary>
        public AccountEntry? MigrateLegacy(string serverRoot, string email, string plainPassword, string legacyTokenKey)
        {
            lock (_lock)
            {
                if (File.Exists(_path)) return null;
                if (string.IsNullOrWhiteSpace(email)) return null;

                var entry = new AccountEntry
                {
                    ServerRoot = (serverRoot ?? "").Trim(),
                    Email = email.Trim(),
                    DisplayName = email.Trim()
                };
                if (!string.IsNullOrEmpty(plainPassword)) entry.SetPassword(plainPassword);

                var pool = LoadLocked();
                pool.Accounts.Add(entry);
                pool.CurrentAccountId = entry.Id;
                PersistLocked();

                // 旧 token 条目（root|email 键）迁移到账号 ID 键
                if (_tokens != null && !string.IsNullOrEmpty(legacyTokenKey))
                {
                    try
                    {
                        var cached = _tokens.Load(legacyTokenKey);
                        if (cached != null)
                        {
                            _tokens.Save(entry.Id, cached);
                            _tokens.Remove(legacyTokenKey);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn("Accounts", "迁移旧 token 失败: " + ex.Message);
                    }
                }

                _log?.Info("Accounts",
                    $"已从 config.json 迁移 yggdrasil 账号: {entry.Email} ({entry.ServerRoot})，ID={entry.Id}");
                return entry;
            }
        }
    }
}
