using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using McBot11.Auth;
using McBot11.Logging;
using McBot11.Minecraft;
using McBot11.OneBot11;

namespace McBot11.Core
{
    /// <summary>
    /// 桥接编排：装配 MC 假人与 OneBot 客户端，实现双向消息路由、
    /// 格式模板渲染、@ 解析、回环指纹过滤、MC 断线自动重连。
    /// </summary>
    public sealed class BridgeService : IDisposable
    {
        private readonly IAppLogger _log;
        private readonly FileTokenCache _tokens = new FileTokenCache();
        private readonly AccountFileStore _accounts;
        private AppConfig _cfg;
        private readonly LoopbackFilter _loopback = new LoopbackFilter(15);
        private readonly CommandRunner _commands;
        private CancellationTokenSource? _joinCmdCts;
        private volatile bool _joinCmdDone;
        private volatile bool _mcWanted;
        private int _reconnecting;

        public MinecraftClient Mc { get; }
        public OneBotClient Qq { get; }

        /// <summary>token 缓存（config\tokens.json）。</summary>
        public FileTokenCache TokenCache => _tokens;

        /// <summary>第三方登录账号池（config\accounts.json）。</summary>
        public AccountFileStore Accounts => _accounts;

        public BridgeService(IAppLogger log, AppConfig cfg)
        {
            _log = log;
            _cfg = cfg;
            _commands = new CommandRunner(log);
            _accounts = new AccountFileStore(ConfigStore.AccountsPath, _tokens, log);
            Mc = new MinecraftClient(log);
            Qq = new OneBotClient(log);

            Mc.ChatReceived += OnMcChat;
            Mc.Disconnected += OnMcDisconnected;
            Mc.PhaseChanged += OnPhaseChanged;
            Mc.DeviceCodePrompt += msg => _log.Info("Auth", msg);
            Qq.GroupMessageReceived += OnGroupMessage;
            Qq.PrivateMessageReceived += OnPrivateMessage;

            // 首次启动确保命令文件模板存在
            CommandRunner.EnsureTemplateFile(CommandRunner.ResolvePath(cfg.CustomCommands.FilePath));

            // 账号池：accounts.json 缺失时从 config.json 的 yggdrasil 单账号平滑迁移
            try
            {
                var y = cfg.Minecraft.Auth.Yggdrasil;
                var migrated = _accounts.MigrateLegacy(y.ServerRoot, y.Email, y.Password,
                    string.IsNullOrWhiteSpace(y.ServerRoot) ? "" : y.ServerRoot.TrimEnd('/') + "|" + y.Email.Trim());
                if (migrated != null) cfg.Minecraft.CurrentAccountId = migrated.Id;
                if (string.IsNullOrEmpty(cfg.Minecraft.CurrentAccountId))
                    cfg.Minecraft.CurrentAccountId = _accounts.List() is { Count: > 0 } list ? list[0].Id : null;
            }
            catch (Exception ex)
            {
                _log.Warn("Accounts", "账号池初始化/迁移失败: " + ex.Message);
            }
        }

        /// <summary>应用（UI 保存后的）新配置，不重建客户端。</summary>
        public void ApplyConfig(AppConfig cfg) => _cfg = cfg;

        public AppConfig Config => _cfg;

        // ==================== MC 连接管理 ====================

        /// <summary>按当前配置构造账号（连接与"测试认证"共用）。</summary>
        public AccountConfig BuildAccount()
        {
            var m = _cfg.Minecraft;
            string type = (m.AuthType ?? "offline").Trim().ToLowerInvariant();
            return type switch
            {
                "mojang" => new AccountConfig
                {
                    Type = "mojang",
                    Username = m.Auth.Mojang.Email,
                    Password = m.Auth.Mojang.Password
                },
                "microsoft" => new AccountConfig
                {
                    Type = "microsoft",
                    Username = m.Username,
                    RefreshToken = string.IsNullOrEmpty(m.Auth.Microsoft.RefreshToken) ? null : m.Auth.Microsoft.RefreshToken
                },
                "yggdrasil" => BuildYggdrasilAccount(m),
                _ => new AccountConfig
                {
                    Type = "offline",
                    Username = string.IsNullOrWhiteSpace(m.Auth.Offline.Username) ? m.Username : m.Auth.Offline.Username
                }
            };
        }

        /// <summary>
        /// 构造 yggdrasil 账号：优先账号池当前选中账号（密码按需 DPAPI 解密，仅驻留内存），
        /// 账号池为空时回退 config.json 的单账号兼容字段。
        /// </summary>
        private AccountConfig BuildYggdrasilAccount(MinecraftSection m)
        {
            var entry = _accounts.Current(m.CurrentAccountId);
            if (entry != null && !string.IsNullOrWhiteSpace(entry.Email))
            {
                return new AccountConfig
                {
                    Type = "yggdrasil",
                    Username = entry.Email,
                    Password = entry.GetPassword(),
                    YggdrasilRoot = entry.ServerRoot,
                    CacheKeyOverride = entry.Id,
                    SelectedProfileId = entry.SelectedProfileId,
                    SelectedProfileName = entry.SelectedProfileName
                };
            }
            return new AccountConfig
            {
                Type = "yggdrasil",
                Username = m.Auth.Yggdrasil.Email,
                Password = m.Auth.Yggdrasil.Password,
                YggdrasilRoot = m.Auth.Yggdrasil.ServerRoot
            };
        }

        /// <summary>
        /// 按账号池条目构造 yggdrasil AccountConfig（获取角色 / 绑定角色 / 登录共用）。
        /// </summary>
        private static AccountConfig BuildEntryAccount(AccountEntry entry, string? selectedProfileId = null, string? selectedProfileName = null)
            => new()
            {
                Type = "yggdrasil",
                Username = entry.Email,
                Password = entry.GetPassword(),
                YggdrasilRoot = entry.ServerRoot,
                CacheKeyOverride = entry.Id,
                SelectedProfileId = selectedProfileId ?? entry.SelectedProfileId,
                SelectedProfileName = selectedProfileName ?? entry.SelectedProfileName
            };

        /// <summary>
        /// 获取指定账号的可选角色列表（availableProfiles，需求 3.3 状态机）：
        /// NoToken/TokenUnbound → authenticate（需密码，不直接 refresh）；
        /// TokenBound → 缓存 1 小时内 validate 通过直接用，否则带 selectedProfile refresh；
        /// 返回空列表 = 该账号没有可用角色。
        /// </summary>
        public async Task<System.Collections.Generic.List<AuthProfile>> FetchProfilesAsync(
            AccountEntry entry, bool forceRefresh = false, CancellationToken ct = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var account = BuildEntryAccount(entry);
            var auth = new YggdrasilAuthenticator(entry.ServerRoot, AuthType.Yggdrasil, _tokens);
            return await auth.FetchProfilesAsync(account, forceRefresh, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 用户选定角色后立刻绑定（需求 3.3.3）：调一次带 selectedProfile 的 refresh
        /// 完成服务器侧绑定，并把 selectedProfile 写入 tokens.json（accounts.json 由调用方写）。
        /// </summary>
        public async Task BindSelectedRoleAsync(AccountEntry entry, AuthProfile role, CancellationToken ct = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (role == null || string.IsNullOrEmpty(role.Id))
                throw new AuthException("绑定角色失败：角色不能为空");
            var account = BuildEntryAccount(entry, role.Id, role.Name);
            var auth = new YggdrasilAuthenticator(entry.ServerRoot, AuthType.Yggdrasil, _tokens);
            await auth.BindProfileAsync(account, role, ct).ConfigureAwait(false);
            _log.Info("Accounts", $"角色绑定完成（带 selectedProfile 的 refresh）: {role.Name} ({role.Id})");
        }

        /// <summary>用户选定角色：同步 tokens.json 的 selectedProfile（accounts.json 由调用方写）。</summary>
        public void CacheSelectedRole(string? accountId, AuthProfile role)
        {
            if (string.IsNullOrEmpty(accountId) || role == null || string.IsNullOrEmpty(role.Id)) return;
            try
            {
                var token = _tokens.Load(accountId!);
                if (token == null) return;
                token.ProfileId = role.Id;
                token.ProfileName = role.Name;
                _tokens.Save(accountId!, token);
            }
            catch (Exception ex)
            {
                _log.Debug("Accounts", "同步 tokens.json selectedProfile 失败: " + ex.Message);
            }
        }

        /// <summary>连接 MC 服务器（启用自动重连标志）。</summary>
        public async Task ConnectMinecraftAsync(CancellationToken ct = default)
        {
            _mcWanted = true;
            var m = _cfg.Minecraft;
            var options = new MinecraftOptions
            {
                Host = m.Host,
                Port = m.Port,
                ProtocolVersion = m.ProtocolVersion ?? 0,
                Account = BuildAccount(),
                TokenCache = _tokens
            };
            await Mc.ConnectAsync(options, ct).ConfigureAwait(false);

            // 登录成功 → 回写账号池的上次登录信息（时间/角色名/UUID）
            try
            {
                string? key = options.Account.CacheKeyOverride;
                if (!string.IsNullOrEmpty(key))
                {
                    var cached = _tokens.Load(key);
                    _accounts.MarkLoginSuccess(key,
                        cached?.ProfileName ?? Mc.Username,
                        cached?.ProfileId);
                }
            }
            catch (Exception ex)
            {
                _log.Debug("Accounts", "回写登录信息失败: " + ex.Message);
            }
        }

        /// <summary>断开 MC 并停止自动重连。</summary>
        public void DisconnectMinecraft()
        {
            _mcWanted = false;
            Mc.Disconnect("用户断开");
        }

        private void OnMcDisconnected(string reason)
        {
            _joinCmdDone = false;
            try { _joinCmdCts?.Cancel(); } catch { }
            if (!_mcWanted || !_cfg.Minecraft.AutoReconnect) return;
            if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (_mcWanted && _cfg.Minecraft.AutoReconnect)
                    {
                        int delaySec = Math.Max(1, _cfg.Minecraft.ReconnectIntervalSeconds);
                        _log.Info("Reconnect", $"{delaySec} 秒后重连 MC 服务器…");
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(delaySec)).ConfigureAwait(false);
                        }
                        catch
                        {
                            break;
                        }
                        if (!_mcWanted) break;
                        try
                        {
                            await ConnectMinecraftAsync().ConfigureAwait(false);
                            break;
                        }
                        catch (Exception ex)
                        {
                            _log.Warn("Reconnect", "重连失败: " + ex.Message);
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _reconnecting, 0);
                }
            });
        }

        // ==================== OneBot 管理 ====================

        /// <summary>进入 Play 阶段 → 触发进服自动命令（每连接会话仅一次）。</summary>
        private void OnPhaseChanged(ClientPhase phase)
        {
            try
            {
                if (phase == ClientPhase.Disconnected)
                {
                    _joinCmdDone = false;
                    return;
                }
                if (phase != ClientPhase.Play || _joinCmdDone) return;
                _joinCmdDone = true;

                var cfg = _cfg.CustomCommands;
                if (!cfg.Enabled || !cfg.ExecuteOnJoin) return;

                try { _joinCmdCts?.Cancel(); } catch { }
                _joinCmdCts = new CancellationTokenSource();
                var token = _joinCmdCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _commands.RunAsync(cmd => Mc.SendCommandAsync(cmd), cfg, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _log.Error("Commands", "进服自动命令执行异常", ex);
                    }
                }, token);
            }
            catch (Exception ex)
            {
                _log.Error("Commands", "进服命令触发异常", ex);
            }
        }

        public void StartOneBot()
        {
            Qq.Start(new OneBotOptions
            {
                Url = _cfg.OneBot.WsUrl,
                AccessToken = string.IsNullOrEmpty(_cfg.OneBot.AccessToken) ? null : _cfg.OneBot.AccessToken,
                ReconnectIntervalMs = Math.Max(1, _cfg.OneBot.ReconnectIntervalSeconds) * 1000
            });
        }

        public void StopOneBot() => Qq.Stop();

        // ==================== MC → QQ ====================

        private void OnMcChat(ChatEventArgs e)
        {
            try
            {
                _log.Info("Router", e.Kind == ChatKind.Player
                    ? $"收到 MC 玩家消息 <{e.SenderName ?? "?"}> {Trunc(e.Text)}"
                    : $"收到 MC {e.Kind} 消息 {Trunc(e.Text)}");
                if (!_cfg.Routing.McToQq.Enabled) return;
                if (_loopback.ShouldDrop(e.Text))
                {
                    _log.Debug("Router", "回环过滤（丢弃 QQ 转入的回声）: " + Trunc(e.Text));
                    return;
                }
                bool includeSelf = _cfg.Routing.McToQq.IncludeBotSelfMessage;
                if (!includeSelf && e.Kind == ChatKind.Player &&
                    !string.IsNullOrEmpty(e.SenderName) &&
                    string.Equals(e.SenderName, _cfg.Minecraft.Username, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Debug("Router", "过滤假人自身消息");
                    return;
                }

                string text;
                if (e.Kind == ChatKind.System)
                {
                    // 系统消息模板独立：[MC] <Server> {message}（翻译键已本地化）
                    text = Render(_cfg.Format.SystemMessageTemplate, ("message", e.Text));
                }
                else
                {
                    // 玩家消息：必须有真实玩家名，绝不输出占位符
                    string? playerName = e.SenderName;
                    if (string.IsNullOrWhiteSpace(playerName))
                    {
                        _log.Warn("Router", "玩家名未知，跳过该条玩家消息（见 Minecraft/player_info_update 日志）: " + Trunc(e.Text));
                        return;
                    }
                    text = Render(_cfg.Format.McToQqTemplate,
                        ("mcName", playerName),
                        ("message", e.Text));
                }

                _log.Info("Router", "MC→QQ 渲染内容: " + Trunc(text));

                foreach (long g in _cfg.Routing.McToQq.TargetGroups)
                {
                    _ = SendSafeAsync(() => Qq.SendGroupMessageAsync(g, text), $"群 {g}");
                }
                foreach (long p in _cfg.Routing.McToQq.TargetPrivate)
                {
                    _ = SendSafeAsync(() => Qq.SendPrivateMessageAsync(p, text), $"私聊 {p}");
                }
            }
            catch (Exception ex)
            {
                _log.Error("Router", "MC→QQ 路由异常", ex);
            }
        }

        private async Task SendSafeAsync(Func<Task> send, string target)
        {
            try
            {
                _log.Debug("Router", $"正在转发到 {target}…");
                await send().ConfigureAwait(false);
                _log.Info("Router", $"已转发到 {target}");
            }
            catch (Exception ex)
            {
                _log.Warn("Router", $"转发到 {target} 失败: {ex.Message}");
            }
        }

        // ==================== QQ → MC ====================

        private void OnGroupMessage(GroupMessageArgs e)
        {
            try
            {
                if (!_cfg.Routing.QqToMc.Enabled) return;
                if (e.SelfId != 0 && e.UserId == e.SelfId) return; // 机器人自身消息
                if (_cfg.Routing.QqToMc.RequireAtInGroup && !e.AtBot)
                {
                    _log.Debug("Router", $"群消息未 @ 机器人，忽略 (群 {e.GroupId})");
                    return;
                }

                string rest = StripAtPrefix(e.Text, e.SelfId);

                // 命令分支：@机器人 /指令（独立白名单，优先于普通转发）
                if (rest.StartsWith("/", StringComparison.Ordinal))
                {
                    string qqName = FirstNonEmpty(e.Card, e.Nickname, e.UserId.ToString());
                    HandleRemoteCommand(rest, isGroup: true, e.GroupId, e.UserId, qqName);
                    return;
                }

                if (!_cfg.Routing.QqToMc.AllowGroups.Contains(e.GroupId))
                {
                    _log.Debug("Router", $"群 {e.GroupId} 不在允许列表");
                    return;
                }

                string message = rest;
                string name = FirstNonEmpty(e.Card, e.Nickname, e.UserId.ToString());
                SendToMc(name, message, $"群 {e.GroupId}");
            }
            catch (Exception ex)
            {
                _log.Error("Router", "QQ→MC 群消息路由异常", ex);
            }
        }

        private void OnPrivateMessage(PrivateMessageArgs e)
        {
            try
            {
                if (!_cfg.Routing.QqToMc.Enabled) return;
                if (e.SelfId != 0 && e.UserId == e.SelfId) return;

                string text = e.Text.Trim();

                // 命令分支：/指令（独立白名单，优先于普通转发）
                if (text.StartsWith("/", StringComparison.Ordinal))
                {
                    string qqName = FirstNonEmpty(e.Nickname, e.UserId.ToString());
                    HandleRemoteCommand(text, isGroup: false, 0, e.UserId, qqName);
                    return;
                }

                if (!_cfg.Routing.QqToMc.AllowPrivate.Contains(e.UserId))
                {
                    _log.Debug("Router", $"私聊 {e.UserId} 不在允许列表");
                    return;
                }
                string message = text;
                string name = FirstNonEmpty(e.Nickname, e.UserId.ToString());
                SendToMc(name, message, $"私聊 {e.UserId}");
            }
            catch (Exception ex)
            {
                _log.Error("Router", "QQ→MC 私聊路由异常", ex);
            }
        }

        // ==================== QQ 远程执行命令 ====================

        /// <summary>
        /// QQ 远程命令：独立权限白名单（allowCommandGroups / allowCommandPrivate，
        /// 默认全空 = 无人可执行），chat_command 发送并回复结果，全程审计日志。
        /// </summary>
        private void HandleRemoteCommand(string raw, bool isGroup, long groupId, long userId, string qqName)
        {
            var perm = _cfg.CommandPermissions;
            string source = isGroup ? $"群 {groupId}" : $"私聊 {userId}";
            string command = raw.Trim();
            if (command.StartsWith("/", StringComparison.Ordinal)) command = command.Substring(1).Trim();

            bool allowed = isGroup
                ? perm.AllowCommandGroups.Contains(groupId)
                : perm.AllowCommandPrivate.Contains(userId);

            if (!allowed)
            {
                _log.Warn("Command", $"无权限: {source} 用户 {userId}({qqName}) 尝试执行 /{command}（未在白名单）");
                if (perm.ReplyOnFailure)
                    Reply(isGroup, groupId, userId, "无权限执行 MC 命令（未在 commandPermissions 白名单）");
                return;
            }

            if (command.Length == 0)
            {
                _log.Warn("Command", $"空命令: {source} 用户 {userId}({qqName}) 仅发送了 /");
                if (perm.ReplyOnFailure)
                    Reply(isGroup, groupId, userId, "命令为空，请在 / 后输入命令内容");
                return;
            }

            _log.Info("Command", $"{source} 用户 {userId}({qqName}) 执行命令: /{command}");
            bool replyOk = perm.ReplyOnExecute;
            bool replyFail = perm.ReplyOnFailure;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (Mc.Phase != ClientPhase.Play)
                        throw new InvalidOperationException("MC 未连接（未进入游戏阶段）");
                    await Mc.SendCommandAsync(command).ConfigureAwait(false);
                    _log.Info("Command", $"命令已发送: /{command}");
                    if (replyOk)
                        await ReplyAsync(isGroup, groupId, userId, $"[MC] 已发送命令：/{command}").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn("Command", $"命令发送失败: /{command} | {ex.Message}");
                    if (replyFail)
                        await ReplyAsync(isGroup, groupId, userId, $"命令发送失败：{ex.Message}").ConfigureAwait(false);
                }
            });
        }

        private void Reply(bool isGroup, long groupId, long userId, string text)
        {
            _ = SendSafeAsync(() => isGroup
                ? Qq.SendGroupMessageAsync(groupId, text)
                : Qq.SendPrivateMessageAsync(userId, text),
                isGroup ? $"群 {groupId}" : $"私聊 {userId}");
        }

        private Task ReplyAsync(bool isGroup, long groupId, long userId, string text)
            => SendSafeAsync(() => isGroup
                ? Qq.SendGroupMessageAsync(groupId, text)
                : Qq.SendPrivateMessageAsync(userId, text),
                isGroup ? $"群 {groupId}" : $"私聊 {userId}");

        private void SendToMc(string qqName, string message, string source)
        {
            if (Mc.Phase != ClientPhase.Play)
            {
                _log.Warn("Router", $"MC 未连接，消息被丢弃（{source}）: {Trunc(message)}");
                return;
            }
            // 新格式：({QQ名字}) {消息} —— 不再拼 <机器人名>/<游戏名>，MC 服务器会自动加 <假人名> 前缀
            string text = Render(_cfg.Format.QqToMcTemplate,
                ("botName", _cfg.Minecraft.Username),
                ("qqName", qqName),
                ("message", message));
            if (text.Length > 256) text = text.Substring(0, 256);
            _loopback.Remember(text);
            // 服务器广播回流时会带上 <假人名> 前缀，一并记入指纹避免回声
            _loopback.Remember($"<{_cfg.Minecraft.Username}> {text}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Mc.SendChatAsync(text).ConfigureAwait(false);
                    _log.Info("Router", $"[{source}] → MC: {Trunc(text)}");
                }
                catch (Exception ex)
                {
                    _log.Warn("Router", "发送到 MC 失败: " + ex.Message);
                }
            });
        }

        /// <summary>去掉开头的 @xxx 段（含被 @ 的机器人与后续 @）。</summary>
        private static string StripAtPrefix(string text, long selfId)
        {
            string s = text.Trim();
            while (s.StartsWith("@", StringComparison.Ordinal))
            {
                int space = s.IndexOfAny(new[] { ' ', '\t' });
                if (space < 0)
                {
                    // 只有 @ 无正文
                    return "";
                }
                s = s.Substring(space + 1).TrimStart();
            }
            return s;
        }

        private static string Render(string template, params (string Key, string Value)[] pairs)
        {
            string result = template ?? "";
            foreach (var (key, value) in pairs)
                result = result.Replace("{" + key + "}", value ?? "");
            return result;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            return "";
        }

        private static string Trunc(string s) => s.Length > 120 ? s.Substring(0, 120) + "…" : s;

        public void Dispose()
        {
            _mcWanted = false;
            try { _joinCmdCts?.Cancel(); } catch { }
            Mc.Dispose();
            Qq.Dispose();
        }
    }
}
