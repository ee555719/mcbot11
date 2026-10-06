using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McBot11.Auth;
using McBot11.Core;
using McBot11.Logging;
using McBot11.Minecraft;
using McBot11.OneBot11;

namespace McBot11.App
{
    /// <summary>日志行（UI 绑定，Color 为十六进制画刷字符串）。</summary>
    public sealed class LogEntry
    {
        public string Text { get; init; } = "";
        public string Color { get; init; } = "#FFCCCCCC";
    }

    /// <summary>协议版本下拉项。</summary>
    public sealed class ProtocolChoice
    {
        public string Label { get; init; } = "";
        public int? Value { get; init; }
        public override string ToString() => Label;
    }

    /// <summary>第三方账号下拉项（账号池）。</summary>
    public sealed class AccountChoice
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public override string ToString() => Label;
    }

    /// <summary>
    /// 主窗口视图模型：配置编辑（MC/OneBot/路由/模板）、命令（保存/重启/连接/
    /// 测试认证）、日志展示（颜色区分）。
    /// </summary>
    public sealed partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly FileLogger _logger;
        private readonly BridgeService _bridge;

        public ObservableCollection<LogEntry> Logs { get; } = new();
        public ObservableCollection<ProtocolChoice> ProtocolOptions { get; } = new();
        /// <summary>第三方账号池（下拉框）。</summary>
        public ObservableCollection<AccountChoice> Accounts { get; } = new();
        /// <summary>当前账号的可选角色（availableProfiles，多角色选择下拉框）。</summary>
        public ObservableCollection<AuthProfile> Roles { get; } = new();

        private bool _loadingAccounts;
        private bool _loadingRoles;
        private (string Name, string Uuid)? _lastVerifiedProfile;

        /// <summary>日志过滤级别选项（UI 过滤，不影响文件全量日志）。</summary>
        public string[] LogLevels { get; } = { "全部", "Debug", "Info", "Warn", "Error" };

        [ObservableProperty] private string logLevelFilter = "全部";

        /// <summary>登录方式下拉项。</summary>
        public string[] AuthTypes { get; } = { "offline", "mojang", "microsoft", "yggdrasil" };

        // ============ MC ============
        [ObservableProperty] private string mcHost = "127.0.0.1";
        [ObservableProperty] private string mcPort = "25565";
        [ObservableProperty] private string mcUsername = "MyBot";
        [ObservableProperty] private ProtocolChoice? selectedProtocol;
        [ObservableProperty] private string mcAuthType = "offline";
        [ObservableProperty] private string offlineUsername = "";
        [ObservableProperty] private string mojangEmail = "";
        [ObservableProperty] private string mojangPassword = "";
        [ObservableProperty] private string yggRoot = "";
        [ObservableProperty] private string yggEmail = "";
        [ObservableProperty] private string yggPassword = "";
        [ObservableProperty] private bool autoReconnect = true;
        [ObservableProperty] private string reconnectInterval = "10";
        [ObservableProperty] private string mcStatus = "未连接";
        [ObservableProperty] private bool busy;
        /// <summary>当前选中的第三方账号（决定 config.json 的 currentAccountId）。</summary>
        [ObservableProperty] private AccountChoice? selectedAccount;
        /// <summary>当前账号选定的登录角色（写入 accounts.json selectedProfileId）。</summary>
        [ObservableProperty] private AuthProfile? selectedRole;

        partial void OnSelectedAccountChanged(AccountChoice? value)
        {
            if (!_loadingAccounts) RefreshRolesFromCache();
            if (_loadingAccounts) return;
            try
            {
                _bridge.Accounts.SetCurrent(value?.Id);
                var cfg = _bridge.Config;
                cfg.Minecraft.CurrentAccountId = value?.Id;
                ConfigStore.Save(cfg);
                _logger.Info("Accounts", "当前登录账号已选择: " + (value?.Label ?? "(无)") + " → config.json");
            }
            catch (Exception ex)
            {
                _logger.Error("Accounts", "保存当前账号选择失败", ex);
            }
        }

        partial void OnSelectedRoleChanged(AuthProfile? value)
        {
            if (_loadingRoles || value == null) return;
            var entry = CurrentAccountEntry();
            if (entry == null) return;
            if (string.Equals((entry.SelectedProfileId ?? "").Replace("-", ""),
                    value.Id.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                _bridge.Accounts.MarkSelectedProfile(entry.Id, value.Id, value.Name);
                _logger.Info("Accounts", $"登录角色已选择: {value.Name} ({value.Id}) → accounts.json");
                // 需求 3.3.3：选定后立刻调一次带 selectedProfile 的 refresh 完成服务器绑定（后台执行）
                _ = BindRoleInBackgroundAsync(entry, value);
            }
            catch (Exception ex)
            {
                _logger.Error("Accounts", "保存所选角色失败", ex);
            }
        }

        /// <summary>下拉框切换角色后的后台绑定（refresh 携带 selectedProfile；失败回退本地同步）。</summary>
        private async Task BindRoleInBackgroundAsync(AccountEntry entry, AuthProfile role)
        {
            try
            {
                await _bridge.BindSelectedRoleAsync(entry, role).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Warn("Auth", "角色绑定 refresh 失败: " + FriendlyAuthError(ex.Message));
                _bridge.CacheSelectedRole(entry.Id, role);
            }
        }

        /// <summary>切换账号时用 tokens.json 缓存刷新角色下拉框（离线展示，不发请求）。</summary>
        private void RefreshRolesFromCache()
        {
            _loadingRoles = true;
            try
            {
                Roles.Clear();
                var entry = CurrentAccountEntry();
                if (entry == null)
                {
                    SelectedRole = null;
                    return;
                }
                var cached = _bridge.TokenCache.Load(entry.Id);
                foreach (var p in cached?.AvailableProfiles ?? new List<AuthProfile>())
                    Roles.Add(p);
                SelectedRole = entry.SelectedProfileId == null
                    ? null
                    : Roles.FirstOrDefault(r => SameProfileId(r.Id, entry.SelectedProfileId));
            }
            catch
            {
                // 角色缓存读取失败不影响主流程
            }
            finally
            {
                _loadingRoles = false;
            }
        }

        private static bool SameProfileId(string? a, string? b)
            => string.Equals((a ?? "").Replace("-", ""), (b ?? "").Replace("-", ""), StringComparison.OrdinalIgnoreCase);

        partial void OnMcAuthTypeChanged(string value)
        {
            OnPropertyChanged(nameof(ShowOffline));
            OnPropertyChanged(nameof(ShowMojang));
            OnPropertyChanged(nameof(ShowMicrosoft));
            OnPropertyChanged(nameof(ShowYggdrasil));
        }

        public bool ShowOffline => McAuthType == "offline";
        public bool ShowMojang => McAuthType == "mojang";
        public bool ShowMicrosoft => McAuthType == "microsoft";
        public bool ShowYggdrasil => McAuthType == "yggdrasil";

        // ============ OneBot ============
        [ObservableProperty] private string wsUrl = "ws://127.0.0.1:3001";
        [ObservableProperty] private string accessToken = "";
        [ObservableProperty] private string oneBotStatus = "未连接";

        // ============ 路由 ============
        [ObservableProperty] private bool mcToQqEnabled = true;
        [ObservableProperty] private string targetGroups = "";
        [ObservableProperty] private string targetPrivate = "";
        [ObservableProperty] private bool includeBotSelfMessage;
        [ObservableProperty] private bool qqToMcEnabled = true;
        [ObservableProperty] private string allowGroups = "";
        [ObservableProperty] private string allowPrivate = "";
        [ObservableProperty] private bool requireAtInGroup = true;

        // ============ 模板 ============
        [ObservableProperty] private string qqToMcTemplate = "({qqName}) {message}";
        [ObservableProperty] private string mcToQqTemplate = "[MC] <{mcName}> {message}";
        [ObservableProperty] private string systemMessageTemplate = "[MC] <Server> {message}";

        // ============ 命令执行 ============
        [ObservableProperty] private bool customCommandsEnabled = true;
        [ObservableProperty] private bool executeOnJoin = true;
        [ObservableProperty] private string commandsFilePath = "config/commands.txt";
        [ObservableProperty] private string commandInterval = "1000";
        [ObservableProperty] private string allowCommandGroups = "";
        [ObservableProperty] private string allowCommandPrivate = "";
        [ObservableProperty] private bool replyOnExecute = true;
        [ObservableProperty] private bool replyOnFailure = true;

        public MainViewModel()
        {
            _logger = new FileLogger();
            var cfg = ConfigStore.Load();
            _bridge = new BridgeService(_logger, cfg);

            ProtocolOptions.Add(new ProtocolChoice { Label = "自动（status 查询）", Value = null });
            ProtocolOptions.Add(new ProtocolChoice { Label = "1.21 / 1.21.1 (767)", Value = 767 });
            ProtocolOptions.Add(new ProtocolChoice { Label = "1.21.2 / 1.21.3 (768)", Value = 768 });
            ProtocolOptions.Add(new ProtocolChoice { Label = "1.21.4 (769)", Value = 769 });
            ProtocolOptions.Add(new ProtocolChoice { Label = "1.21.5 (770)", Value = 770 });
            ProtocolOptions.Add(new ProtocolChoice { Label = "1.21.6 (771)", Value = 771 });
            ProtocolOptions.Add(new ProtocolChoice { Label = "1.21.7 / 1.21.8 (772)", Value = 772 });
            ProtocolOptions.Add(new ProtocolChoice { Label = "1.21.9 / 1.21.10 (773)", Value = 773 });
            ProtocolOptions.Add(new ProtocolChoice { Label = "1.21.11 (774)", Value = 774 });
            ProtocolOptions.Add(new ProtocolChoice { Label = "26.1.x (775)", Value = 775 });
            ProtocolOptions.Add(new ProtocolChoice { Label = "26.2 (776)", Value = 776 });
            ProtocolOptions.Add(new ProtocolChoice { Label = "26.3 (777)", Value = 777 });
            SelectedProtocol = ProtocolOptions[0];

            _logger.Logged += OnLogged;
            _bridge.Mc.PhaseChanged += p => Application.Current?.Dispatcher.BeginInvoke(() =>
                McStatus = p switch
                {
                    ClientPhase.Disconnected => "未连接",
                    ClientPhase.Connecting => "连接中…",
                    ClientPhase.Login => "登录中…",
                    ClientPhase.Configuration => "配置阶段",
                    ClientPhase.Play => "已连接 (Play)",
                    _ => p.ToString()
                });
            _bridge.Mc.Disconnected += reason => Application.Current?.Dispatcher.BeginInvoke(() =>
                McStatus = "已断开: " + reason);
            _bridge.Qq.StateChanged += s => Application.Current?.Dispatcher.BeginInvoke(() =>
                OneBotStatus = s switch
                {
                    OneBotState.Disconnected => "未连接",
                    OneBotState.Connecting => "连接中…",
                    OneBotState.Connected => "已连接",
                    _ => s.ToString()
                });
            _bridge.Mc.DeviceCodePrompt += msg => Application.Current?.Dispatcher.BeginInvoke(() =>
                _logger.Info("Auth", msg));

            LoadFrom(cfg);
            _logger.Info("App", "McBot11 启动完成，配置: " + ConfigStore.ConfigPath);
        }

        /// <summary>按当前过滤级别决定是否显示到 UI（文件日志始终全量）。</summary>
        private bool PassFilter(LogLevel level) => LogLevelFilter switch
        {
            "Debug" => true,
            "Info" => level >= LogLevel.Info,
            "Warn" => level >= LogLevel.Warn,
            "Error" => level >= LogLevel.Error,
            _ => true
        };

        partial void OnLogLevelFilterChanged(string value)
        {
            // 切换级别后按新级别重建当前列表（文件中已有全量日志）
            // 简化处理：清空后由后续日志按新级别填充
            Logs.Clear();
        }

        private void OnLogged(LogLevel level, string source, string message, string ui)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!PassFilter(level)) return;
                string color = level switch
                {
                    LogLevel.Debug => "#FF808080",
                    LogLevel.Info => "#FFCCCCCC",
                    LogLevel.Warn => "#FFFFCC66",
                    LogLevel.Error => "#FFFF6666",
                    _ => "#FFCCCCCC"
                };
                Logs.Add(new LogEntry { Text = ui, Color = color });
                while (Logs.Count > 500) Logs.RemoveAt(0);
            });
        }

        // ============ 配置 ↔ UI ============

        private void LoadFrom(AppConfig cfg)
        {
            McHost = cfg.Minecraft.Host;
            McPort = cfg.Minecraft.Port.ToString();
            McUsername = cfg.Minecraft.Username;
            SelectedProtocol = ProtocolOptions.FirstOrDefault(p => p.Value == cfg.Minecraft.ProtocolVersion) ?? ProtocolOptions[0];
            McAuthType = string.IsNullOrWhiteSpace(cfg.Minecraft.AuthType) ? "offline" : cfg.Minecraft.AuthType;
            OfflineUsername = cfg.Minecraft.Auth.Offline.Username;
            MojangEmail = cfg.Minecraft.Auth.Mojang.Email;
            MojangPassword = cfg.Minecraft.Auth.Mojang.Password;
            YggRoot = cfg.Minecraft.Auth.Yggdrasil.ServerRoot;
            YggEmail = cfg.Minecraft.Auth.Yggdrasil.Email;
            YggPassword = cfg.Minecraft.Auth.Yggdrasil.Password;
            AutoReconnect = cfg.Minecraft.AutoReconnect;
            ReconnectInterval = cfg.Minecraft.ReconnectIntervalSeconds.ToString();
            WsUrl = cfg.OneBot.WsUrl;
            AccessToken = cfg.OneBot.AccessToken;
            McToQqEnabled = cfg.Routing.McToQq.Enabled;
            TargetGroups = JoinIds(cfg.Routing.McToQq.TargetGroups);
            TargetPrivate = JoinIds(cfg.Routing.McToQq.TargetPrivate);
            IncludeBotSelfMessage = cfg.Routing.McToQq.IncludeBotSelfMessage;
            QqToMcEnabled = cfg.Routing.QqToMc.Enabled;
            AllowGroups = JoinIds(cfg.Routing.QqToMc.AllowGroups);
            AllowPrivate = JoinIds(cfg.Routing.QqToMc.AllowPrivate);
            RequireAtInGroup = cfg.Routing.QqToMc.RequireAtInGroup;
            QqToMcTemplate = cfg.Format.QqToMcTemplate;
            McToQqTemplate = cfg.Format.McToQqTemplate;
            SystemMessageTemplate = cfg.Format.SystemMessageTemplate;
            CustomCommandsEnabled = cfg.CustomCommands.Enabled;
            ExecuteOnJoin = cfg.CustomCommands.ExecuteOnJoin;
            CommandsFilePath = cfg.CustomCommands.FilePath;
            CommandInterval = cfg.CustomCommands.CommandExecuteIntervalMs.ToString();
            AllowCommandGroups = JoinIds(cfg.CommandPermissions.AllowCommandGroups);
            AllowCommandPrivate = JoinIds(cfg.CommandPermissions.AllowCommandPrivate);
            ReplyOnExecute = cfg.CommandPermissions.ReplyOnExecute;
            ReplyOnFailure = cfg.CommandPermissions.ReplyOnFailure;
            RefreshAccounts(cfg.Minecraft.CurrentAccountId);
            // 启动时把实际选中的账号（含迁移产生的）回写到 config.json
            cfg.Minecraft.CurrentAccountId = SelectedAccount?.Id;
            try { ConfigStore.Save(cfg); } catch (Exception ex) { _logger.Warn("Accounts", "回写 currentAccountId 失败: " + ex.Message); }
        }

        /// <summary>按账号池重建下拉框并选中指定账号。</summary>
        private void RefreshAccounts(string? selectId)
        {
            _loadingAccounts = true;
            try
            {
                Accounts.Clear();
                var list = _bridge.Accounts.List();
                foreach (var a in list)
                    Accounts.Add(new AccountChoice { Id = a.Id, Label = a.Label });
                SelectedAccount = Accounts.FirstOrDefault(a => a.Id == selectId)
                                  ?? Accounts.FirstOrDefault()
                                  ?? null;
                OnPropertyChanged(nameof(SelectedAccount));
            }
            finally
            {
                _loadingAccounts = false;
            }
            // 切换账号 → 角色下拉框同步刷新为该账号的角色列表（离线读缓存）
            RefreshRolesFromCache();
        }

        private AppConfig BuildConfig()
        {
            var cfg = _bridge.Config;
            cfg.Minecraft.Host = McHost.Trim();
            cfg.Minecraft.Port = int.TryParse(McPort.Trim(), out int port) && port is > 0 and <= 65535 ? port : 25565;
            cfg.Minecraft.Username = McUsername.Trim();
            cfg.Minecraft.ProtocolVersion = SelectedProtocol?.Value;
            cfg.Minecraft.AuthType = McAuthType;
            cfg.Minecraft.CurrentAccountId = SelectedAccount?.Id ?? cfg.Minecraft.CurrentAccountId;
            cfg.Minecraft.Auth.Offline.Username = OfflineUsername.Trim();
            cfg.Minecraft.Auth.Mojang.Email = MojangEmail.Trim();
            cfg.Minecraft.Auth.Mojang.Password = MojangPassword;
            cfg.Minecraft.Auth.Yggdrasil.ServerRoot = YggRoot.Trim();
            cfg.Minecraft.Auth.Yggdrasil.Email = YggEmail.Trim();
            cfg.Minecraft.Auth.Yggdrasil.Password = YggPassword;
            cfg.Minecraft.AutoReconnect = AutoReconnect;
            cfg.Minecraft.ReconnectIntervalSeconds = int.TryParse(ReconnectInterval.Trim(), out int sec) && sec > 0 ? sec : 10;
            cfg.OneBot.WsUrl = WsUrl.Trim();
            cfg.OneBot.AccessToken = AccessToken;
            cfg.Routing.McToQq.Enabled = McToQqEnabled;
            cfg.Routing.McToQq.TargetGroups = ParseIds(TargetGroups);
            cfg.Routing.McToQq.TargetPrivate = ParseIds(TargetPrivate);
            cfg.Routing.McToQq.IncludeBotSelfMessage = IncludeBotSelfMessage;
            cfg.Routing.QqToMc.Enabled = QqToMcEnabled;
            cfg.Routing.QqToMc.AllowGroups = ParseIds(AllowGroups);
            cfg.Routing.QqToMc.AllowPrivate = ParseIds(AllowPrivate);
            cfg.Routing.QqToMc.RequireAtInGroup = RequireAtInGroup;
            cfg.Format.QqToMcTemplate = string.IsNullOrWhiteSpace(QqToMcTemplate) ? FormatSection.DefaultQqToMcTemplate : QqToMcTemplate;
            cfg.Format.McToQqTemplate = string.IsNullOrWhiteSpace(McToQqTemplate) ? "[MC] <{mcName}> {message}" : McToQqTemplate;
            cfg.Format.SystemMessageTemplate = string.IsNullOrWhiteSpace(SystemMessageTemplate) ? "[MC] <Server> {message}" : SystemMessageTemplate;
            cfg.CustomCommands.Enabled = CustomCommandsEnabled;
            cfg.CustomCommands.ExecuteOnJoin = ExecuteOnJoin;
            cfg.CustomCommands.FilePath = string.IsNullOrWhiteSpace(CommandsFilePath) ? "config/commands.txt" : CommandsFilePath.Trim();
            cfg.CustomCommands.CommandExecuteIntervalMs = int.TryParse(CommandInterval.Trim(), out int ci) && ci >= 50 ? ci : 1000;
            cfg.CommandPermissions.AllowCommandGroups = ParseIds(AllowCommandGroups);
            cfg.CommandPermissions.AllowCommandPrivate = ParseIds(AllowCommandPrivate);
            cfg.CommandPermissions.ReplyOnExecute = ReplyOnExecute;
            cfg.CommandPermissions.ReplyOnFailure = ReplyOnFailure;
            return cfg;
        }

        private static string JoinIds(IEnumerable<long> ids) => string.Join(", ", ids);
        private static List<long> ParseIds(string text) =>
            (text ?? "").Split(new[] { ',', '，', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => long.TryParse(t.Trim(), out long id) ? id : -1)
                .Where(id => id > 0).Distinct().ToList();

        // ============ 命令 ============

        [RelayCommand]
        private void SaveConfig()
        {
            try
            {
                var cfg = BuildConfig();
                ConfigStore.Save(cfg);
                _bridge.ApplyConfig(cfg);
                _logger.Info("Config", "配置已保存: " + ConfigStore.ConfigPath);
            }
            catch (Exception ex)
            {
                _logger.Error("Config", "保存配置失败", ex);
            }
        }

        [RelayCommand]
        private void RestartBridge()
        {
            SaveConfig();
            try
            {
                _bridge.StopOneBot();
                _bridge.DisconnectMinecraft();
                _logger.Info("App", "桥接已停止，正在按新配置重启…");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    try { _bridge.StartOneBot(); } catch (Exception ex) { _logger.Error("OneBot", "启动失败", ex); }
                    try { await _bridge.ConnectMinecraftAsync(); }
                    catch (Exception ex) { _logger.Error("Minecraft", "连接失败: " + ex.Message); }
                });
            }
            catch (Exception ex)
            {
                _logger.Error("App", "重启失败", ex);
            }
        }

        [RelayCommand]
        private async Task ConnectMcAsync()
        {
            if (Busy) return;
            Busy = true;
            SaveConfig();
            try
            {
                try
                {
                    await ConnectOnceAsync().ConfigureAwait(true);
                }
                catch (Exception ex) when (IsMissingPasswordError(ex))
                {
                    _logger.Warn("Auth", "当前账号未保存密码，请在弹窗中输入密码后继续登录");
                    if (PromptPasswordForSelected())
                        await ConnectOnceAsync().ConfigureAwait(true);
                    else
                        _logger.Warn("Auth", "未输入密码，已取消连接");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Minecraft", "连接失败: " + FriendlyAuthError(ex.Message));
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>
        /// 连接一次：yggdrasil 账号先做角色选择（唯一角色自动选中 / 多角色弹窗 / 无角色中止），
        /// 连接中若所选角色已失效则重新获取角色列表并再次弹窗。
        /// </summary>
        private async Task ConnectOnceAsync()
        {
            AccountEntry? entry = McAuthType == "yggdrasil" ? CurrentAccountEntry() : null;
            if (entry == null)
            {
                await _bridge.ConnectMinecraftAsync().ConfigureAwait(true);
                return;
            }

            if (!await EnsureSelectedRoleAsync(entry).ConfigureAwait(true)) return;

            try
            {
                await _bridge.ConnectMinecraftAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (IsRoleInvalidError(ex))
            {
                _logger.Warn("Auth", "登录角色已失效（可能已在皮肤站删除），正在重新获取角色列表…");
                ClearSelectedRole(entry);
                if (!await EnsureSelectedRoleAsync(entry, force: true).ConfigureAwait(true)) return;
                await _bridge.ConnectMinecraftAsync().ConfigureAwait(true);
            }
        }

        /// <summary>
        /// 确保当前账号已有可用角色：缓存 1 小时内直接读，否则 refresh/密码登录；
        /// 0 个角色 → 报错中止；1 个 → 自动选中；多个 → 弹窗单选（取消 = 中止登录）。
        /// 返回 false 表示中止。
        /// </summary>
        private async Task<bool> EnsureSelectedRoleAsync(AccountEntry entry, bool force = false)
        {
            List<AuthProfile> roles;
            try
            {
                roles = await _bridge.FetchProfilesAsync(entry, forceRefresh: true).ConfigureAwait(true);
            }
            catch (Exception ex) when (IsMissingPasswordError(ex) || ex.Message.Contains("密码", StringComparison.Ordinal))
            {
                _logger.Warn("Auth", "获取角色列表需要密码，正在请求输入…");
                if (!PromptPasswordForSelected())
                {
                    _logger.Warn("Auth", "未输入密码，已取消登录");
                    return false;
                }
                entry = CurrentAccountEntry() ?? entry;
                try
                {
                    roles = await _bridge.FetchProfilesAsync(entry, forceRefresh: true).ConfigureAwait(true);
                }
                catch (Exception ex2)
                {
                    _logger.Error("Auth", "获取角色列表失败: " + FriendlyAuthError(ex2.Message));
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Auth", "获取角色列表失败: " + FriendlyAuthError(ex.Message));
                return false;
            }

            if (roles.Count == 0)
            {
                _logger.Error("Auth", "该账号没有可用角色，请先到皮肤站创建角色");
                FillRoles(entry, roles);
                return false;
            }

            if (!force && entry.SelectedProfileId != null &&
                roles.Any(r => SameProfileId(r.Id, entry.SelectedProfileId)))
            {
                FillRoles(entry, roles);
                return true;
            }

            if (roles.Count == 1)
            {
                await ApplyRoleSelectionAsync(entry, roles[0]).ConfigureAwait(true);
                _logger.Info("Auth", $"唯一角色自动选中: {roles[0].Name} ({roles[0].Id})");
                return true;
            }

            var pick = RoleSelectDialog.Show(
                Application.Current?.MainWindow,
                $"账号 {entry.Label} 有 {roles.Count} 个角色，请选择登录使用的角色",
                roles,
                force ? null : entry.SelectedProfileId);
            if (pick == null)
            {
                FillRoles(entry, roles);
                _logger.Warn("Auth", "用户取消了角色选择");
                return false;
            }
            await ApplyRoleSelectionAsync(entry, pick).ConfigureAwait(true);
            return true;
        }

        /// <summary>
        /// 把选定角色写入 accounts.json 并刷新下拉框，
        /// 随后立刻调一次带 selectedProfile 的 refresh 完成服务器绑定（需求 3.3.3）。
        /// 绑定失败仅告警（登录时会再次尝试绑定），不阻断选择。
        /// </summary>
        private async Task ApplyRoleSelectionAsync(AccountEntry entry, AuthProfile role)
        {
            _bridge.Accounts.MarkSelectedProfile(entry.Id, role.Id, role.Name);
            _loadingRoles = true;
            try
            {
                if (Roles.All(r => !SameProfileId(r.Id, role.Id)))
                    Roles.Add(role);
                SelectedRole = Roles.FirstOrDefault(r => SameProfileId(r.Id, role.Id));
            }
            finally
            {
                _loadingRoles = false;
            }
            try
            {
                await _bridge.BindSelectedRoleAsync(entry, role).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Warn("Auth", "角色绑定 refresh 失败: " + FriendlyAuthError(ex.Message));
                _bridge.CacheSelectedRole(entry.Id, role);
            }
        }

        /// <summary>角色已失效：清空 accounts.json 的选定角色与 tokens.json 缓存，强制重新获取。</summary>
        private void ClearSelectedRole(AccountEntry entry)
        {
            try
            {
                _bridge.Accounts.ClearSelectedProfile(entry.Id);
                _bridge.TokenCache.Remove(entry.Id);
                RefreshRolesFromCache();
            }
            catch (Exception ex)
            {
                _logger.Warn("Accounts", "清除失效角色缓存失败: " + ex.Message);
            }
        }

        /// <summary>用给定列表填充角色下拉框并同步选中项（不触发保存）。</summary>
        private void FillRoles(AccountEntry entry, List<AuthProfile> roles)
        {
            _loadingRoles = true;
            try
            {
                Roles.Clear();
                foreach (var r in roles) Roles.Add(r);
                SelectedRole = entry.SelectedProfileId == null
                    ? null
                    : Roles.FirstOrDefault(r => SameProfileId(r.Id, entry.SelectedProfileId));
            }
            finally
            {
                _loadingRoles = false;
            }
        }

        private static bool IsRoleInvalidError(Exception ex)
            => ex.Message.Contains("已选角色已失效", StringComparison.Ordinal);

        [RelayCommand]
        private void DisconnectMc()
        {
            _bridge.DisconnectMinecraft();
        }

        [RelayCommand]
        private void StartOneBot()
        {
            SaveConfig();
            try { _bridge.StartOneBot(); }
            catch (Exception ex) { _logger.Error("OneBot", "启动失败: " + ex.Message); }
        }

        [RelayCommand]
        private void StopOneBot()
        {
            _bridge.StopOneBot();
        }

        /// <summary>按当前认证类型执行登录测试（offline/mojang/yggdrasil/microsoft 全流程）。</summary>
        [RelayCommand]
        private async Task TestAuthAsync()
        {
            if (Busy) return;
            Busy = true;
            try
            {
                SaveConfig();
                var account = _bridge.BuildAccount();
                string type = (account.Type ?? "").Trim().ToLowerInvariant();
                _logger.Info("Auth", $"开始认证测试 ({type})…");

                if (type == "yggdrasil" && !string.IsNullOrWhiteSpace(account.YggdrasilRoot))
                {
                    try
                    {
                        var meta = await YggdrasilMetadataFetcher.GetAsync(account.YggdrasilRoot!);
                        string name = string.IsNullOrEmpty(meta.ServerName) ? "(未提供)" : meta.ServerName;
                        _logger.Info("Auth", $"Yggdrasil 元数据: 服务器名={name}, skinDomains={meta.SkinDomains.Length}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Auth", "Yggdrasil 元数据获取失败: " + ex.Message);
                    }
                }

                if (type == "microsoft")
                {
                    var ms = new MicrosoftAuthenticator(null, _bridge.TokenCache);
                    ms.DeviceCodePrompt += msg =>
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            _logger.Info("Auth", "【微软设备码】" + msg);
                            MessageBox.Show(msg, "Microsoft 设备码登录", MessageBoxButton.OK, MessageBoxImage.Information);
                        });
                    var profile = await ms.AuthenticateAsync(account);
                    _logger.Info("Auth", $"认证成功: {profile.Username} ({profile.Uuid}) [msa]");
                    return;
                }

                var auth = AuthenticatorFactory.Create(account, _bridge.TokenCache);
                var result = await auth.AuthenticateAsync(account);
                _logger.Info("Auth", $"认证成功: {result.Username} ({result.Uuid}) [{result.AuthKind}]");
            }
            catch (Exception ex)
            {
                _logger.Error("Auth", "认证测试失败: " + FriendlyAuthError(ex.Message));
            }
            finally
            {
                Busy = false;
            }
        }

        // ============ 第三方账号池 ============

        private AccountEntry? CurrentAccountEntry()
            => SelectedAccount == null ? null : _bridge.Accounts.Get(SelectedAccount.Id);

        /// <summary>
        /// 「获取角色」（需求 3.3 状态机）：NoToken/TokenUnbound → authenticate（未保存密码则弹窗提示
        /// 「请输入密码以获取角色列表」）；TokenBound → validate/refresh；
        /// 0 个 → 提示创建角色；1 个 → 自动选中并 refresh 绑定；多个且无有效选择 → 弹窗单选（取消则仅填充下拉框）。
        /// </summary>
        [RelayCommand]
        private async Task FetchRolesAsync()
        {
            if (Busy) return;
            var entry = CurrentAccountEntry();
            if (entry == null)
            {
                _logger.Warn("Accounts", "请先选择账号");
                return;
            }
            Busy = true;
            try
            {
                _logger.Info("Auth", $"获取角色列表: {entry.Label} …");
                List<AuthProfile> roles;
                try
                {
                    roles = await _bridge.FetchProfilesAsync(entry, forceRefresh: true).ConfigureAwait(true);
                }
                catch (Exception ex) when (IsMissingPasswordError(ex))
                {
                    _logger.Warn("Auth", "获取角色列表需要密码，请在弹窗中输入密码…");
                    if (!PromptPasswordForSelected())
                    {
                        _logger.Warn("Auth", "未输入密码，已取消获取角色列表");
                        return;
                    }
                    entry = CurrentAccountEntry() ?? entry;
                    roles = await _bridge.FetchProfilesAsync(entry, forceRefresh: true).ConfigureAwait(true);
                }
                if (roles.Count == 0)
                {
                    FillRoles(entry, roles);
                    _logger.Warn("Auth", "该账号没有可用角色，请先到皮肤站创建角色");
                    return;
                }
                if (entry.SelectedProfileId != null && roles.Any(r => SameProfileId(r.Id, entry.SelectedProfileId)))
                {
                    FillRoles(entry, roles);
                    _logger.Info("Auth", $"角色列表已刷新: {roles.Count} 个（已选 {entry.SelectedProfileName}）");
                    return;
                }
                if (roles.Count == 1)
                {
                    await ApplyRoleSelectionAsync(entry, roles[0]).ConfigureAwait(true);
                    _logger.Info("Auth", $"角色列表已获取，唯一角色自动选中: {roles[0].Name}");
                    return;
                }
                // 多角色且无有效已选 → 弹窗让用户选择（需求 3.3.2），选完立刻 refresh 绑定
                var pick = RoleSelectDialog.Show(
                    Application.Current?.MainWindow,
                    $"账号 {entry.Label} 有 {roles.Count} 个角色，请选择登录使用的角色",
                    roles,
                    entry.SelectedProfileId);
                FillRoles(entry, roles);
                if (pick == null)
                {
                    _logger.Warn("Auth", "用户取消了角色选择（可在「登录角色」下拉框选择）");
                    return;
                }
                await ApplyRoleSelectionAsync(entry, pick).ConfigureAwait(true);
                _logger.Info("Auth", $"角色列表已获取并完成绑定: {roles.Count} 个，已选 {pick.Name}");
            }
            catch (Exception ex)
            {
                _logger.Error("Auth", "获取角色列表失败: " + FriendlyAuthError(ex.Message));
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>账号添加/编辑保存后的角色解析（单角色自动选中，多角色弹窗，无角色提示）。</summary>
        private async Task ResolveRolesAfterSaveAsync(AccountEntry entry)
        {
            try
            {
                var roles = await _bridge.FetchProfilesAsync(entry).ConfigureAwait(true);
                if (roles.Count == 0)
                {
                    FillRoles(entry, roles);
                    _logger.Warn("Accounts", "该账号没有可用角色，请先到皮肤站创建角色");
                    return;
                }
                if (entry.SelectedProfileId != null && roles.Any(r => SameProfileId(r.Id, entry.SelectedProfileId)))
                {
                    FillRoles(entry, roles);
                    return;
                }
                if (roles.Count == 1)
                {
                    await ApplyRoleSelectionAsync(entry, roles[0]).ConfigureAwait(true);
                    _logger.Info("Accounts", $"唯一角色自动选中: {roles[0].Name} ({roles[0].Id})");
                    return;
                }
                var pick = RoleSelectDialog.Show(
                    Application.Current?.MainWindow,
                    $"账号 {entry.Label} 有 {roles.Count} 个角色，请选择登录角色",
                    roles,
                    entry.SelectedProfileId);
                if (pick == null)
                {
                    FillRoles(entry, roles);
                    _logger.Warn("Accounts", "用户取消了角色选择（可在主界面「登录角色」下拉框重新选择）");
                    return;
                }
                await ApplyRoleSelectionAsync(entry, pick).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Warn("Accounts", "获取角色列表失败: " + FriendlyAuthError(ex.Message));
            }
        }

        /// <summary>「获取角色列表」按钮（账号编辑/添加对话框）：返回 availableProfiles。</summary>
        private async Task<List<AuthProfile>?> FetchRolesForDialogAsync(AccountInput input)
        {
            string? password = input.Password;
            if (string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(input.AccountId))
                password = _bridge.Accounts.Get(input.AccountId)?.GetPassword();
            var account = new AccountConfig
            {
                Type = "yggdrasil",
                Username = input.Email,
                Password = password,
                YggdrasilRoot = input.ServerRoot,
                CacheKeyOverride = string.IsNullOrEmpty(input.AccountId) ? null : input.AccountId,
                SelectedProfileId = input.SelectedProfileId,
                SelectedProfileName = input.SelectedProfileName
            };
            var auth = new YggdrasilAuthenticator(account.YggdrasilRoot, AuthType.Yggdrasil, _bridge.TokenCache);
            return await auth.FetchProfilesAsync(account, forceRefresh: true).ConfigureAwait(true);
        }

        /// <summary>验证账号输入（严格 authenticate，绕过 token 缓存）；返回 null = 通过。</summary>
        private async Task<string?> VerifyAccountInputAsync(AccountInput input)
        {
            string id = input.AccountId ?? Guid.NewGuid().ToString();
            input.AccountId = id;
            _lastVerifiedProfile = null;
            try
            {
                var account = new AccountConfig
                {
                    Type = "yggdrasil",
                    Username = input.Email,
                    Password = input.Password,
                    YggdrasilRoot = input.ServerRoot,
                    CacheKeyOverride = id
                };
                var auth = new YggdrasilAuthenticator(input.ServerRoot, AuthType.Yggdrasil, _bridge.TokenCache);
                var profile = await auth.AuthenticateWithPasswordAsync(account).ConfigureAwait(true);
                _lastVerifiedProfile = (profile.Username, profile.Uuid.ToString("N"));
                _logger.Info("Auth", $"账号验证通过: {profile.Username} ({profile.Uuid})");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn("Auth", "账号验证失败: " + ex.Message);
                return FriendlyAuthError(ex.Message);
            }
        }

        /// <summary>
        /// 把认证异常翻译为可读中文（不含任何敏感信息）。顺序即优先级（需求 3.4 错误映射）：
        /// 密码缺失 → 未绑定角色 → 5xx 故障 → 401/403 密码错误 → 404 → 网络不可达 → 角色类。
        /// </summary>
        private static string FriendlyAuthError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "未知错误";
            // 1) 未保存密码 → 引导弹窗输入（需求 §四「密码未保存」）
            if (message.Contains("未配置密码", StringComparison.Ordinal) ||
                message.Contains("请输入密码", StringComparison.Ordinal) ||
                message.Contains("请重新输入密码", StringComparison.Ordinal))
                return "账号未保存密码，请先输入密码（" + message + "）";
            // 2) 400 + no profile assigned → 中文对照并触发选择角色流程（需求 3.4）
            if (message.Contains("未绑定角色", StringComparison.Ordinal) ||
                message.Contains("no profile assigned", StringComparison.OrdinalIgnoreCase))
                return "该账号未绑定角色，请先选择角色后再登录（" + message + "）";
            // 3) 5xx → 认证服务器故障（需求 3.4，Auth 层已带该文案）
            if (message.Contains("认证服务器故障", StringComparison.Ordinal))
                return message;
            // 4) 401/403 → 邮箱或密码错误
            if (message.Contains("403", StringComparison.Ordinal) ||
                message.Contains("401", StringComparison.Ordinal) ||
                message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("密码错误", StringComparison.Ordinal) ||
                message.Contains("password", StringComparison.OrdinalIgnoreCase))
                return "邮箱或密码错误（" + message + "）";
            // 5) 404 → 接口地址错误
            if (message.Contains("404", StringComparison.Ordinal))
                return "认证服务器接口不存在，请检查根地址（" + message + "）";
            // 6) 网络故障（断网 / 拒绝连接 / DNS 失败）→ 认证服务器不可达（需求 §四「网络故障」）
            if (message.Contains("HTTP 请求失败", StringComparison.Ordinal) ||
                message.Contains("不可达", StringComparison.Ordinal) ||
                message.Contains("SocketException", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection refused", StringComparison.OrdinalIgnoreCase))
                return "认证服务器不可达（" + message + "）";
            // 7) 已知角色类文案原样透传（供连接流程按关键字识别并重新选角色）
            if (message.Contains("已选角色已失效", StringComparison.Ordinal) ||
                message.Contains("该账号没有可用角色", StringComparison.Ordinal))
                return message;
            // 8) 其余角色相关兜底
            if (message.Contains("selectedProfile", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("角色", StringComparison.Ordinal))
                return "角色不存在或未选择角色（" + message + "）";
            return message;
        }

        [RelayCommand]
        private async Task AddAccountAsync()
        {
            if (Busy) return;
            Busy = true;
            try
            {
                var input = new AccountInput
                {
                    ServerRoot = string.IsNullOrWhiteSpace(YggRoot) ? "https://littleskin.cn/api/yggdrasil" : YggRoot.Trim()
                };
                var result = AccountDialog.Show(Application.Current?.MainWindow, input, VerifyAccountInputAsync, FetchRolesForDialogAsync);
                if (result == null) return;

                var entry = new AccountEntry
                {
                    Id = string.IsNullOrEmpty(result.AccountId) ? Guid.NewGuid().ToString() : result.AccountId!,
                    ServerRoot = result.ServerRoot,
                    Email = result.Email,
                    DisplayName = string.IsNullOrWhiteSpace(result.DisplayName) ? result.Email : result.DisplayName.Trim()
                };
                if (result.SavePassword) entry.SetPassword(result.Password);
                if (!string.IsNullOrEmpty(result.SelectedProfileId))
                {
                    entry.SelectedProfileId = result.SelectedProfileId;
                    entry.SelectedProfileName = result.SelectedProfileName;
                }
                if (_lastVerifiedProfile is { } profile)
                {
                    entry.LastProfileName = profile.Name;
                    entry.LastProfileUuid = profile.Uuid;
                    entry.LastLoginTime = DateTimeOffset.UtcNow;
                    _lastVerifiedProfile = null;
                }
                _bridge.Accounts.Upsert(entry);
                SelectAccount(entry.Id);
                _logger.Info("Accounts", $"账号已添加并选中: {entry.Label}");
                await ResolveRolesAfterSaveAsync(entry).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error("Accounts", "添加账号失败", ex);
            }
            finally
            {
                Busy = false;
            }
        }

        [RelayCommand]
        private async Task EditAccountAsync()
        {
            if (Busy) return;
            var current = CurrentAccountEntry();
            if (current == null)
            {
                _logger.Warn("Accounts", "请先在下拉框选择要编辑的账号");
                return;
            }
            string origRoot = current.ServerRoot;
            string origPassword = current.Password;
            Busy = true;
            try
            {
                var input = new AccountInput
                {
                    IsEdit = true,
                    AccountId = current.Id,
                    ServerRoot = current.ServerRoot,
                    Email = current.Email,
                    DisplayName = current.DisplayName,
                    SavePassword = !string.IsNullOrEmpty(current.Password),
                    SelectedProfileId = current.SelectedProfileId,
                    SelectedProfileName = current.SelectedProfileName
                };
                var result = AccountDialog.Show(Application.Current?.MainWindow, input, VerifyAccountInputAsync, FetchRolesForDialogAsync);
                if (result == null) return;

                current.ServerRoot = result.ServerRoot;
                current.Email = result.Email;
                current.DisplayName = string.IsNullOrWhiteSpace(result.DisplayName) ? result.Email : result.DisplayName.Trim();
                bool credsChanged = !string.Equals(origRoot, current.ServerRoot, StringComparison.Ordinal);
                if (!result.SavePassword)
                {
                    if (current.Password.Length > 0) credsChanged = true;
                    current.Password = "";
                }
                else if (!string.IsNullOrEmpty(result.Password))
                {
                    string origPlain = McBot11.Auth.SecureStore.Unprotect(origPassword) ?? "";
                    if (!string.Equals(origPlain, result.Password, StringComparison.Ordinal))
                        credsChanged = true;
                    current.SetPassword(result.Password);
                }
                if (!string.IsNullOrEmpty(result.SelectedProfileId))
                {
                    current.SelectedProfileId = result.SelectedProfileId;
                    current.SelectedProfileName = result.SelectedProfileName;
                }
                if (_lastVerifiedProfile is { } profile)
                {
                    current.LastProfileName = profile.Name;
                    current.LastProfileUuid = profile.Uuid;
                    current.LastLoginTime = DateTimeOffset.UtcNow;
                    _lastVerifiedProfile = null;
                }
                _bridge.Accounts.Upsert(current);

                if (credsChanged)
                {
                    // 2.4.4：密码 / serverRoot 变更 → 清空角色缓存，强制重新获取
                    _bridge.TokenCache.Remove(current.Id);
                    _bridge.Accounts.ClearSelectedProfile(current.Id);
                    current.SelectedProfileId = null;
                    current.SelectedProfileName = null;
                    _logger.Info("Accounts", "账号凭据已变更，角色缓存已清除（将强制重新获取角色）");
                }

                SelectAccount(current.Id);
                _logger.Info("Accounts", "账号已更新: " + current.Label);
                await ResolveRolesAfterSaveAsync(current).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error("Accounts", "编辑账号失败", ex);
            }
            finally
            {
                Busy = false;
            }
        }

        [RelayCommand]
        private void DeleteAccount()
        {
            if (Busy) return;
            var current = CurrentAccountEntry();
            if (current == null)
            {
                _logger.Warn("Accounts", "请先在下拉框选择要删除的账号");
                return;
            }
            var rc = MessageBox.Show(
                $"确认删除账号 {current.Label}？\n（config\\accounts.json 与 config\\tokens.json 中对应条目一并清除）",
                "删除账号", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (rc != MessageBoxResult.Yes) return;

            try
            {
                _bridge.Accounts.Remove(current.Id);
                RefreshAccounts(_bridge.Accounts.List().FirstOrDefault()?.Id);
                var cfg = _bridge.Config;
                cfg.Minecraft.CurrentAccountId = SelectedAccount?.Id;
                ConfigStore.Save(cfg);
                _logger.Info("Accounts", "已删除账号: " + current.Label);
            }
            catch (Exception ex)
            {
                _logger.Error("Accounts", "删除账号失败", ex);
            }
        }

        [RelayCommand]
        private async Task TestAccountAsync()
        {
            if (Busy) return;
            var current = CurrentAccountEntry();
            if (current == null)
            {
                _logger.Warn("Accounts", "请先在下拉框选择要测试的账号");
                return;
            }
            Busy = true;
            try
            {
                _logger.Info("Auth", $"测试账号 {current.Label} …");
                if (!string.IsNullOrWhiteSpace(current.ServerRoot))
                {
                    try
                    {
                        var meta = await YggdrasilMetadataFetcher.GetAsync(current.ServerRoot);
                        string name = string.IsNullOrEmpty(meta.ServerName) ? "(未提供)" : meta.ServerName;
                        _logger.Info("Auth", $"Yggdrasil 元数据: 服务器名={name}, skinDomains={meta.SkinDomains.Length}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Auth", "Yggdrasil 元数据获取失败: " + ex.Message);
                    }
                }

                var account = new AccountConfig
                {
                    Type = "yggdrasil",
                    Username = current.Email,
                    Password = current.GetPassword(),
                    YggdrasilRoot = current.ServerRoot,
                    CacheKeyOverride = current.Id,
                    SelectedProfileId = current.SelectedProfileId,
                    SelectedProfileName = current.SelectedProfileName
                };
                var auth = AuthenticatorFactory.Create(account, _bridge.TokenCache);
                var profile = await auth.AuthenticateAsync(account);
                _logger.Info("Auth", $"账号测试通过: {profile.Username} ({profile.Uuid}) [{profile.AuthKind}]");
                if (profile.AvailableProfiles.Count > 0)
                    _logger.Info("Auth", $"availableProfiles: {profile.AvailableProfiles.Count} 个角色（{string.Join("、", profile.AvailableProfiles.Select(p => p.Name))}）");
                var cached = _bridge.TokenCache.Load(current.Id);
                _bridge.Accounts.MarkLoginSuccess(current.Id, profile.Username, cached?.ProfileId ?? profile.Uuid.ToString("N"));
                RefreshAccounts(current.Id);
                try
                {
                    var roles = await _bridge.FetchProfilesAsync(current);
                    FillRoles(current, roles);
                }
                catch { /* 角色列表刷新失败不影响测试结论 */ }
            }
            catch (Exception ex)
            {
                _logger.Error("Auth", "账号测试失败: " + FriendlyAuthError(ex.Message));
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>token 失效且未保存密码时，弹窗补录密码（验证通过后保存）。</summary>
        private bool PromptPasswordForSelected()
        {
            var current = CurrentAccountEntry();
            if (current == null) return false;
            var input = new AccountInput
            {
                IsEdit = true,
                AccountId = current.Id,
                ServerRoot = current.ServerRoot,
                Email = current.Email,
                DisplayName = current.DisplayName,
                SavePassword = true,
                RequirePassword = true
            };
            var result = AccountDialog.Show(Application.Current?.MainWindow, input, VerifyAccountInputAsync);
            if (result == null) return false;
            if (!string.IsNullOrEmpty(result.Password)) current.SetPassword(result.Password);
            _bridge.Accounts.Upsert(current);
            SelectAccount(current.Id);
            _logger.Info("Accounts", "密码已保存（DPAPI 加密）: " + current.Label);
            return true;
        }

        /// <summary>选中账号并同步 config.json 的 currentAccountId。</summary>
        private void SelectAccount(string? id)
        {
            RefreshAccounts(id);
            try
            {
                _bridge.Accounts.SetCurrent(id);
                var cfg = _bridge.Config;
                cfg.Minecraft.CurrentAccountId = id;
                ConfigStore.Save(cfg);
            }
            catch (Exception ex)
            {
                _logger.Error("Accounts", "保存当前账号失败", ex);
            }
        }

        private static bool IsMissingPasswordError(Exception ex)
            => ex.Message.Contains("未配置密码", StringComparison.Ordinal) ||
               ex.Message.Contains("请输入密码", StringComparison.Ordinal) ||
               ex.Message.Contains("请重新输入密码", StringComparison.Ordinal);

        [RelayCommand]
        private void ClearLogs() => Logs.Clear();

        public void Dispose()
        {
            try { _bridge.Dispose(); } catch { }
            try { _logger.DisposeWriter(); } catch { }
        }
    }
}
