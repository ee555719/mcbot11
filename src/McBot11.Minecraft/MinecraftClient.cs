using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McBot11.Auth;
using McBot11.Logging;
using McBot11.Protocol;
using McBot11.Protocol.Crypto;
using McBot11.Protocol.IO;
using McBot11.Protocol.Net;
using McBot11.Protocol.Nbt;
using McBot11.Protocol.Text;

namespace McBot11.Minecraft
{
    /// <summary>
    /// 自研 Minecraft 协议客户端：TCP 连接、握手/登录（加密+压缩）、配置阶段、
    /// 游戏阶段状态机，keepalive/传送确认/死亡重生/资源包应答自动处理，
    /// 聊天收发。协议 767~777（1.21.x ~ 26.3）包 ID 表驱动，字段按版本分支。
    /// </summary>
    public sealed class MinecraftClient : IDisposable
    {
        private readonly IAppLogger _log;
        private MinecraftOptions _opt = new MinecraftOptions();
        private TcpClient? _tcp;
        private NetworkStream? _stream = null;
        private FrameReader _frames = new FrameReader();
        private int _threshold = -1;
        private Cfb8Cipher? _cipher;
        private PacketState _state = PacketState.Status;
        private int _protocol;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? _cts;
        private SessionProfile? _profile;
        private string? _joinRoot;
        private int _phase = (int)ClientPhase.Disconnected;
        private bool _disconnectRaised;
        private bool _dead;
        private double _x, _y, _z;
        private float _yaw, _pitch;
        private readonly Dictionary<Guid, string> _playerNames = new Dictionary<Guid, string>();
        private readonly object _namesLock = new object();
        private readonly HashSet<int> _unknownIdsLogged = new HashSet<int>();

        public MinecraftClient(IAppLogger? log = null)
        {
            _log = log ?? NullLogger.Instance;
        }

        /// <summary>收到聊天（读循环线程触发，UI 侧需自行调度线程）。</summary>
        public event Action<ChatEventArgs>? ChatReceived;

        /// <summary>阶段变化（Disconnected/Connecting/Login/Configuration/Play）。</summary>
        public event Action<ClientPhase>? PhaseChanged;

        /// <summary>断开（附原因，含服务器踢出原因）。</summary>
        public event Action<string>? Disconnected;

        /// <summary>Microsoft device code 提示（透传认证器）。</summary>
        public event Action<string>? DeviceCodePrompt;

        public ClientPhase Phase => (ClientPhase)_phase;
        public bool IsConnected => (ClientPhase)_phase is ClientPhase.Login or ClientPhase.Configuration or ClientPhase.Play;
        public string? Username => _profile?.Username;
        public int Protocol => _protocol;

        private void SetPhase(ClientPhase phase)
        {
            Interlocked.Exchange(ref _phase, (int)phase);
            PhaseChanged?.Invoke(phase);
        }

        /// <summary>连接并进入游戏。protocolVersion=0 时先做 status 查询。</summary>
        public async Task ConnectAsync(MinecraftOptions options, CancellationToken ct = default)
        {
            DisposeConnection();
            _opt = options ?? throw new ArgumentNullException(nameof(options));
            _disconnectRaised = false;
            _dead = false;
            lock (_namesLock) _playerNames.Clear();
            _unknownIdsLogged.Clear();
            SetPhase(ClientPhase.Connecting);

            try
            {
                // 1) 认证
                var auth = AuthenticatorFactory.Create(options.Account, options.TokenCache);
                if (auth is MicrosoftAuthenticator ms)
                    ms.DeviceCodePrompt += msg => DeviceCodePrompt?.Invoke(msg);
                _log.Info("Auth", $"使用 {options.Account.Type} 认证…");
                var profile = await auth.AuthenticateAsync(options.Account, ct).ConfigureAwait(false);
                _profile = profile;
                _joinRoot = AuthenticatorFactory.GetJoinRoot(options.Account);
                _log.Info("Auth", $"认证成功: {profile.Username} ({profile.Uuid})");

                // 2) 协议版本（0 = status 自动查询）
                int proto = options.ProtocolVersion;
                if (proto <= 0)
                {
                    proto = await StatusQuerier.ResolveAsync(options.Host, options.Port, _log, ct).ConfigureAwait(false);
                }
                if (proto < PacketIds.MinProtocol || proto > PacketIds.MaxProtocol)
                    throw new ProtocolException($"不支持的协议版本 {proto}（支持 {PacketIds.MinProtocol}~{PacketIds.MaxProtocol}）");
                _protocol = proto;

                // 3) TCP + 握手 + 登录开始
                _tcp = new TcpClient();
                await _tcp.ConnectAsync(options.Host, options.Port, ct).ConfigureAwait(false);
                _tcp.NoDelay = true;
                _stream = _tcp.GetStream();
                _frames = new FrameReader { CompressionThreshold = -1 };
                _threshold = -1;
                _cipher = null;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                SetPhase(ClientPhase.Login);
                _state = PacketState.Handshake;
                await SendAsync(PacketName.Intention, w =>
                {
                    w.WriteVarInt(_protocol);
                    string host = options.Host.Length > 255 ? options.Host.Substring(0, 255) : options.Host;
                    w.WriteString(host, 255);
                    w.WriteInt16(unchecked((short)options.Port));
                    w.WriteVarInt(2); // next = login
                }, _cts.Token).ConfigureAwait(false);

                _state = PacketState.Login;
                await SendAsync(PacketName.LoginStart, w =>
                {
                    w.WriteString(profile.Username, 16);
                    w.WriteUuid(profile.Uuid);
                }, _cts.Token).ConfigureAwait(false);

                _log.Info("Minecraft", $"已连接 {options.Host}:{options.Port}，协议 {_protocol}，登录中…");
                _ = Task.Run(ReadLoopAsync, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Error("Minecraft", "连接失败", ex);
                HandleDisconnect("连接失败: " + ex.Message);
                throw;
            }
        }

        /// <summary>主动断开。</summary>
        public void Disconnect(string reason = "客户端主动断开")
        {
            try { _cts?.Cancel(); } catch { }
            HandleDisconnect(reason);
        }

        /// <summary>发送聊天消息（仅 Play 阶段；自动截断到 256 字符）。</summary>
        public Task SendChatAsync(string message, CancellationToken ct = default)
        {
            if ((ClientPhase)_phase != ClientPhase.Play)
                throw new InvalidOperationException("未进入游戏阶段，无法发送聊天");
            if (string.IsNullOrWhiteSpace(message)) return Task.CompletedTask;
            if (message.Length > 256) message = message.Substring(0, 256);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return SendAsync(PacketName.Chat, w =>
            {
                w.WriteString(message, 256);
                w.WriteInt64(now);
                w.WriteInt64(0);          // salt
                w.WriteBoolean(false);    // 无签名
                w.WriteVarInt(0);         // message count
                w.WriteBytes(new byte[3]); // acknowledged 位集（空）
                if (_protocol >= 770)
                    w.WriteByte(1);       // checksum：空集 → Arrays.hashCode(空)=0 → 0 → 返回 1
            }, ct);
        }

        /// <summary>发送命令（chat_command，不含前导斜杠，仅 Play 阶段）。</summary>
        public Task SendCommandAsync(string command, CancellationToken ct = default)
        {
            if ((ClientPhase)_phase != ClientPhase.Play)
                throw new InvalidOperationException("未进入游戏阶段，无法发送命令");
            command = command.Trim();
            if (command.StartsWith("/", StringComparison.Ordinal)) command = command.Substring(1);
            if (command.Length == 0) return Task.CompletedTask;
            if (command.Length > 32767) command = command.Substring(0, 32767);
            return SendAsync(PacketName.ChatCommand, w => w.WriteString(command, 32767), ct);
        }

        // ==================== 发送 ====================

        private async Task SendAsync(PacketName name, Action<PacketWriter> body, CancellationToken ct)
        {
            var w = new PacketWriter(64);
            w.WriteVarInt(PacketIds.GetId(_protocol, _state, PacketDirection.ToServer, name));
            body(w);
            byte[] frame = FrameEncoder.Encode(w.ToArray(), _threshold);
            var cipher = _cipher;
            if (cipher != null) cipher.Apply(frame, 0, frame.Length);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var stream = _stream ?? throw new IOException("连接已关闭");
                await stream.WriteAsync(frame, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // ==================== 接收循环 ====================

        private async Task ReadLoopAsync()
        {
            var token = _cts?.Token ?? CancellationToken.None;
            var buf = new byte[32 * 1024];
            try
            {
                var stream = _stream ?? throw new IOException("连接未建立");
                while (!token.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(), token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        HandleDisconnect("服务器关闭连接");
                        return;
                    }
                    var cipher = _cipher;
                    if (cipher != null) cipher.Apply(buf, 0, n);
                    _frames.Append(buf, 0, n);
                    while (_frames.TryTake(out byte[] packet))
                        await DispatchAsync(packet, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Error("Minecraft", "读取循环异常", ex);
                HandleDisconnect("连接错误: " + ex.Message);
            }
        }

        private async Task DispatchAsync(byte[] payload, CancellationToken ct)
        {
            // 包级 payload 解析失败仅丢弃该包并告警，不中断连接：
            // 帧边界由 FrameReader 按长度截取（数据流仍同步），单包错位不应掉线
            try
            {
                var r = new PacketReader(payload);
                int id = r.ReadVarInt();
                if (!PacketIds.TryGetName(_protocol, _state, PacketDirection.ToClient, id, out var name))
                {
                    // 高频未映射包（如 update_time）每连接只记一次，避免刷屏
                    if (_unknownIdsLogged.Add(id))
                        _log.Debug("Minecraft", $"忽略未知包 id={id} state={_state}");
                    return;
                }
                switch (_state)
                {
                    case PacketState.Login:
                        await HandleLoginAsync(name, r, ct).ConfigureAwait(false);
                        break;
                    case PacketState.Configuration:
                        await HandleConfigAsync(name, r, ct).ConfigureAwait(false);
                        break;
                    case PacketState.Play:
                        await HandlePlayAsync(name, r, ct).ConfigureAwait(false);
                        break;
                    default:
                        _log.Debug("Minecraft", $"状态 {_state} 下忽略包 {name}");
                        break;
                }
            }
            catch (ProtocolException ex)
            {
                _log.Warn("Minecraft", $"包解析失败，已丢弃（state={_state}）: {ex.Message}");
            }
        }

        // ==================== Login ====================

        private async Task HandleLoginAsync(PacketName name, PacketReader r, CancellationToken ct)
        {
            switch (name)
            {
                case PacketName.LoginDisconnect:
                {
                    string reason = TextComponent.JsonToPlainText(r.ReadString(32767));
                    HandleDisconnect("服务器拒绝登录: " + reason);
                    return;
                }
                case PacketName.EncryptionReq:
                {
                    string serverId = r.ReadString(32767);
                    byte[] publicKey = r.ReadVarBytes(4096);
                    byte[] verifyToken = r.ReadVarBytes(1024);
                    bool shouldAuth = r.ReadBoolean();
                    await PerformEncryptionAsync(serverId, publicKey, verifyToken, shouldAuth, ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.LoginCompress:
                {
                    int threshold = r.ReadVarInt();
                    _threshold = threshold;
                    _frames.CompressionThreshold = threshold;
                    _log.Info("Minecraft", $"压缩已启用，阈值 {threshold}");
                    return;
                }
                case PacketName.LoginSuccess:
                {
                    Guid uuid = r.ReadUuid();
                    string username = r.ReadString(16);
                    int propCount = r.ReadVarInt();
                    for (int i = 0; i < propCount && i < 64; i++)
                    {
                        r.ReadString(32767);   // name
                        r.ReadString(32767);   // value
                        if (r.ReadBoolean()) r.ReadString(32767); // signature
                    }
                    if (_protocol == 767) r.ReadBoolean(); // strictErrorHandling
                    if (_profile != null)
                    {
                        _profile.Uuid = uuid;
                        _profile.Username = username;
                    }
                    _log.Info("Minecraft", $"登录成功: {username}");
                    await SendAsync(PacketName.LoginAcknowledged, _ => { }, ct).ConfigureAwait(false);
                    _state = PacketState.Configuration;
                    SetPhase(ClientPhase.Configuration);
                    await SendClientInformationAsync(ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.LoginPluginRequest:
                {
                    int messageId = r.ReadVarInt();
                    await SendAsync(PacketName.LoginPluginResponse, w =>
                    {
                        w.WriteVarInt(messageId);
                        w.WriteBoolean(false); // not understood
                    }, ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.CookieRequest:
                {
                    string key = r.ReadString(32767);
                    await SendAsync(PacketName.CookieResponse, w =>
                    {
                        w.WriteString(key, 32767);
                        w.WriteBoolean(false); // 无 cookie 数据
                    }, ct).ConfigureAwait(false);
                    return;
                }
                default:
                    _log.Debug("Minecraft", $"Login 阶段忽略包 {name}");
                    return;
            }
        }

        private async Task PerformEncryptionAsync(string serverId, byte[] publicKey, byte[] verifyToken, bool shouldAuth, CancellationToken ct)
        {
            if (_profile == null || _profile.AuthKind == "offline")
                throw new ProtocolException("服务器要求在线模式加密，但当前为 offline 账号（请改用 microsoft/yggdrasil 账号，或关闭服务器在线模式）");
            if (_joinRoot == null)
                throw new ProtocolException("缺少 session join 根地址，无法完成加密登录");

            byte[] secret = LoginCrypto.GenerateSharedSecret();
            string hash = LoginCrypto.ComputeServerIdHash(serverId, secret, publicKey);
            _log.Info("Minecraft", "服务器要求加密，执行 session join…");
            await SessionJoiner.JoinAsync(_joinRoot, _profile, hash, _profile.AuthKind == "mojang", ct).ConfigureAwait(false);

            byte[] encryptedSecret = LoginCrypto.RsaEncrypt(secret, publicKey);
            byte[] encryptedToken = LoginCrypto.RsaEncrypt(verifyToken, publicKey);
            await SendAsync(PacketName.EncryptionResp, w =>
            {
                w.WriteVarBytes(encryptedSecret);
                w.WriteVarBytes(encryptedToken);
            }, ct).ConfigureAwait(false);

            _cipher = Cfb8Cipher.Create(secret);
            _log.Info("Minecraft", "加密通道已启用");
        }

        // ==================== Configuration ====================

        private async Task HandleConfigAsync(PacketName name, PacketReader r, CancellationToken ct)
        {
            switch (name)
            {
                case PacketName.Disconnect:
                {
                    var reasonNbt = NbtReader.ReadCompound(r);
                    HandleDisconnect("服务器断开: " + TextComponent.ToPlainText(reasonNbt));
                    return;
                }
                case PacketName.FinishConfiguration:
                {
                    await SendAsync(PacketName.FinishConfiguration, _ => { }, ct).ConfigureAwait(false);
                    _state = PacketState.Play;
                    SetPhase(ClientPhase.Play);
                    _log.Info("Minecraft", "已进入游戏阶段");
                    return;
                }
                case PacketName.KeepAlive:
                {
                    long id = r.ReadInt64();
                    await SendAsync(PacketName.KeepAlive, w => w.WriteInt64(id), ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.Ping:
                {
                    int id = r.ReadInt32();
                    await SendAsync(PacketName.Pong, w => w.WriteInt32(id), ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.SelectKnownPacks:
                {
                    // 不声明任何已知包：服务器下发全量注册表数据（最兼容）
                    await SendAsync(PacketName.SelectKnownPacks, w => w.WriteVarInt(0), ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.ResourcePackPush:
                {
                    Guid uuid = r.ReadUuid();
                    await SendResourcePackResponseAsync(uuid, ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.CookieRequest:
                {
                    string key = r.ReadString(32767);
                    await SendAsync(PacketName.CookieResponse, w =>
                    {
                        w.WriteString(key, 32767);
                        w.WriteBoolean(false);
                    }, ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.CustomPayload:
                case PacketName.RegistryData:
                case PacketName.ResetChat:
                case PacketName.ResourcePackPop:
                case PacketName.StoreCookie:
                case PacketName.Transfer:
                case PacketName.FeatureFlags:
                case PacketName.UpdateTags:
                case PacketName.PostEffects:
                case PacketName.ClearDialog:
                case PacketName.ShowDialog:
                case PacketName.CodeOfConduct:
                    return; // 无需应答
                default:
                    _log.Debug("Minecraft", $"Configuration 阶段忽略包 {name}");
                    return;
            }
        }

        private async Task SendClientInformationAsync(CancellationToken ct)
        {
            await SendAsync(PacketName.ClientInformation, w =>
            {
                w.WriteString(_opt.Locale, 16);
                w.WriteByte(unchecked((byte)(sbyte)_opt.ViewDistance)); // i8
                w.WriteVarInt(0);        // chat mode: enabled
                w.WriteBoolean(true);    // chat colors
                w.WriteByte(0x7F);       // skin parts（全部）
                w.WriteVarInt(1);        // main hand: right
                w.WriteBoolean(false);   // text filtering
                w.WriteBoolean(true);    // allow server listings
                if (_protocol >= 770) w.WriteVarInt(0); // particle status: all
            }, ct).ConfigureAwait(false);
        }

        private async Task SendResourcePackResponseAsync(Guid uuid, CancellationToken ct)
        {
            // 1 = successfully_loaded（假人不实际下载资源包，服务器无校验）
            await SendAsync(PacketName.ResourcePack, w =>
            {
                w.WriteUuid(uuid);
                w.WriteVarInt(1);
            }, ct).ConfigureAwait(false);
        }

        // ==================== Play ====================

        private async Task HandlePlayAsync(PacketName name, PacketReader r, CancellationToken ct)
        {
            switch (name)
            {
                case PacketName.KeepAlive:
                {
                    long id = r.ReadInt64();
                    await SendAsync(PacketName.KeepAlive, w => w.WriteInt64(id), ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.Ping:
                {
                    int id = r.ReadInt32();
                    await SendAsync(PacketName.Pong, w => w.WriteInt32(id), ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.KickDisconnect:
                {
                    var reason = NbtReader.ReadCompound(r);
                    HandleDisconnect("被服务器断开: " + TextComponent.ToPlainText(reason));
                    return;
                }
                case PacketName.SystemChat:
                {
                    var content = NbtReader.ReadCompound(r);
                    bool actionBar = r.ReadBoolean();
                    if (!actionBar)
                        RaiseChat(new ChatEventArgs { Kind = ChatKind.System, Text = SystemMessageLocalizer.FromComponent(content) });
                    return;
                }
                case PacketName.PlayerChat:
                {
                    if (_protocol >= 770) r.ReadVarInt(); // globalIndex
                    Guid sender = r.ReadUuid();
                    r.ReadVarInt();                       // index
                    if (r.ReadBoolean()) r.ReadBytes(256); // signature
                    string plain = r.ReadString(32767);
                    string? senderName;
                    lock (_namesLock) _playerNames.TryGetValue(sender, out senderName);
                    RaiseChat(new ChatEventArgs
                    {
                        Kind = ChatKind.Player,
                        Text = plain,
                        SenderUuid = sender,
                        SenderName = senderName
                    });
                    return;
                }
                case PacketName.DisguisedChat:
                {
                    var message = NbtReader.ReadCompound(r);
                    // chat.type.text 形式（with = [玩家名, 消息]）按玩家消息提取真实名字
                    if (message is Dictionary<string, object> dict &&
                        dict.TryGetValue("translate", out var tr) && tr is string dKey &&
                        dKey.StartsWith("chat.type", StringComparison.Ordinal) &&
                        dict.TryGetValue("with", out var withObj) && withObj is List<object> withList &&
                        withList.Count >= 2)
                    {
                        RaiseChat(new ChatEventArgs
                        {
                            Kind = ChatKind.Player,
                            SenderName = TextComponent.ToPlainText(withList[0]),
                            Text = TextComponent.ToPlainText(withList[1])
                        });
                    }
                    else
                    {
                        RaiseChat(new ChatEventArgs { Kind = ChatKind.System, Text = SystemMessageLocalizer.FromComponent(message) });
                    }
                    return;
                }
                case PacketName.PlayerPosition:
                {
                    await HandleTeleportAsync(r, ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.StartConfiguration:
                {
                    await SendAsync(PacketName.ConfigurationAcknowledged, _ => { }, ct).ConfigureAwait(false);
                    _state = PacketState.Configuration;
                    SetPhase(ClientPhase.Configuration);
                    await SendClientInformationAsync(ct).ConfigureAwait(false);
                    _log.Info("Minecraft", "服务器要求重新配置");
                    return;
                }
                case PacketName.SetHealth:
                {
                    float health = r.ReadSingle();
                    r.ReadVarInt();  // food
                    r.ReadSingle();  // saturation
                    if (health <= 0f)
                    {
                        if (!_dead)
                        {
                            _dead = true;
                            await SendAsync(PacketName.ClientCommand, w => w.WriteVarInt(0), ct).ConfigureAwait(false);
                            _log.Info("Minecraft", "已死亡，发送重生请求");
                        }
                    }
                    else
                    {
                        _dead = false;
                    }
                    return;
                }
                case PacketName.ResourcePackPush:
                {
                    Guid uuid = r.ReadUuid();
                    await SendResourcePackResponseAsync(uuid, ct).ConfigureAwait(false);
                    return;
                }
                case PacketName.Login:
                    _dead = false;
                    _log.Info("Minecraft", "已加入服务器");
                    return;
                case PacketName.Respawn:
                    _dead = false;
                    return;
                case PacketName.PlayerInfoUpdate:
                {
                    TryReadPlayerName(r);
                    return;
                }
                case PacketName.PlayerInfoRemove:
                {
                    HandlePlayerInfoRemove(r);
                    return;
                }
                case PacketName.ResourcePackPop:
                case PacketName.DeleteChat:
                case PacketName.EntityEvent:
                case PacketName.GameEvent:
                case PacketName.TabList:
                case PacketName.RemoveEntities:
                    return;
                default:
                    _log.Debug("Minecraft", $"Play 阶段忽略包 {name}");
                    return;
            }
        }

        private async Task HandleTeleportAsync(PacketReader r, CancellationToken ct)
        {
            int teleportId;
            if (_protocol == 767)
            {
                _x = r.ReadDouble();
                _y = r.ReadDouble();
                _z = r.ReadDouble();
                _yaw = r.ReadSingle();
                _pitch = r.ReadSingle();
                r.ReadByte();          // flags: u8
                teleportId = r.ReadVarInt();
            }
            else
            {
                teleportId = r.ReadVarInt();
                _x = r.ReadDouble();
                _y = r.ReadDouble();
                _z = r.ReadDouble();
                r.ReadDouble(); r.ReadDouble(); r.ReadDouble(); // velocity
                _yaw = r.ReadSingle();
                _pitch = r.ReadSingle();
                r.ReadInt32();         // flags: u32（768+）
            }

            await SendAsync(PacketName.AcceptTeleportation, w =>
            {
                w.WriteVarInt(teleportId);
                if (_protocol >= 777)
                {
                    w.WriteDouble(_x);
                    w.WriteDouble(_y);
                    w.WriteDouble(_z);
                    w.WriteSingle(_yaw);
                    w.WriteSingle(_pitch);
                }
            }, ct).ConfigureAwait(false);

            await SendAsync(PacketName.MovePlayerPos, w =>
            {
                w.WriteDouble(_x);
                w.WriteDouble(_y);
                w.WriteDouble(_z);
                w.WriteByte(0x01); // on ground
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// player_info_update：action(u8 bitflags) → count(varint) → 每条 uuid + 按位条件字段。
        /// 位序：0x01 add_player、0x02 initialize_chat、0x04 update_game_mode、0x08 update_listed、
        /// 0x10 update_latency、0x20 update_display_name、0x40 list_order(768)/hat(769+)、0x80 hat(769+)。
        /// 字段物理顺序：uuid → player → chatSession → gamemode → listed → latency → displayName
        /// → listPriority(varint) → showHat(bool)。
        /// 0x40/0x80 的位义在 minecraft-data 与 wiki 两套约定下相反，因此两字段一律按 VarInt
        /// 读取且固定按 0x40→0x80 顺序消费；两种约定下每种位组合的字节消耗完全对齐（值不使用）。
        /// </summary>
        private void TryReadPlayerName(PacketReader r)
        {
            int actions = 0, count = 0, entry = 0, added = 0;
            try
            {
                actions = r.ReadByte();
                count = r.ReadVarInt();
                if (count < 0 || count > 8192)
                    throw new ProtocolException($"条目数非法: {count}");

                int knownMask = _protocol >= 769 ? 0xFF : _protocol >= 768 ? 0x7F : 0x3F;
                if ((actions & ~knownMask) != 0)
                    throw new ProtocolException($"含本协议未知的 action 位: 0x{actions:X2}（支持掩码 0x{knownMask:X2}）");

                for (entry = 0; entry < count; entry++)
                {
                    Guid uuid = r.ReadUuid();

                    // 0x01 Add Player：game_profile = name + properties[]
                    if ((actions & 0x01) != 0)
                    {
                        string name = r.ReadString();
                        int propCount = r.ReadVarInt();
                        if (propCount < 0 || propCount > 64)
                            throw new ProtocolException($"属性数非法: {propCount}");
                        for (int p = 0; p < propCount; p++)
                        {
                            r.ReadString();
                            r.ReadString();
                            if (r.ReadBoolean()) r.ReadString();
                        }
                        lock (_namesLock) _playerNames[uuid] = name;
                        added++;
                    }

                    // 0x02 Initialize Chat：chat_session = option<uuid + publicKey{expireTime, key, sig}>
                    if ((actions & 0x02) != 0 && r.ReadBoolean())
                    {
                        r.ReadUuid();
                        r.ReadInt64();
                        SkipVarLengthed(r, "chat_session.keyBytes");
                        SkipVarLengthed(r, "chat_session.keySignature");
                    }

                    // 0x04 game mode、0x08 listed、0x10 latency 均为 varint
                    if ((actions & 0x04) != 0) r.ReadVarInt();
                    if ((actions & 0x08) != 0) r.ReadVarInt();
                    if ((actions & 0x10) != 0) r.ReadVarInt();

                    // 0x20 display name：option<anonymousNbt>（0x01 + NBT；NbtReader 容错探测类型字节）
                    if ((actions & 0x20) != 0 && r.ReadBoolean())
                        NbtReader.ReadCompound(r);

                    // 0x40 / 0x80 扩展位（见方法注释）
                    if ((actions & 0x40) != 0) r.ReadVarInt();
                    if ((actions & 0x80) != 0) r.ReadVarInt();
                }

                if (added > 0)
                    _log.Debug("Minecraft", $"player_info_update 解析完成 actions=0x{actions:X2} count={count} 新增玩家名 {added} 个");
            }
            catch (Exception ex)
            {
                // 名字映射尽力解析：中断本包剩余条目（包已独立成帧，不影响连接），已入表的保留
                _log.Warn("Minecraft",
                    $"player_info_update 解析中断: {ex.Message} (actions=0x{actions:X2} count={count} entry={entry} 已入表 {added} 个)");
            }
        }

        /// <summary>
        /// player_info_remove：count(varint) + count × uuid，按 uuid 清理玩家名映射。
        /// </summary>
        private void HandlePlayerInfoRemove(PacketReader r)
        {
            int count = 0;
            try
            {
                count = r.ReadVarInt();
                if (count < 0 || count > 8192)
                    throw new ProtocolException($"条目数非法: {count}");
                for (int i = 0; i < count; i++)
                {
                    Guid uuid = r.ReadUuid();
                    lock (_namesLock) _playerNames.Remove(uuid);
                }
                _log.Debug("Minecraft", $"player_info_remove 移除 {count} 个玩家名");
            }
            catch (Exception ex)
            {
                _log.Warn("Minecraft", $"player_info_remove 解析中断: {ex.Message} (count={count})");
            }
        }

        private static void SkipVarLengthed(PacketReader r, string field)
        {
            int len = r.ReadVarInt();
            if (len < 0 || len > 65536) throw new ProtocolException($"{field} 长度非法: {len}");
            r.Skip(len);
        }

        private void RaiseChat(ChatEventArgs args)
        {
            try { ChatReceived?.Invoke(args); }
            catch (Exception ex) { _log.Error("Minecraft", "聊天事件处理异常", ex); }
        }

        // ==================== 断开 ====================

        private void HandleDisconnect(string reason)
        {
            if (_disconnectRaised) return;
            _disconnectRaised = true;
            try { _cts?.Cancel(); } catch { }
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            SetPhase(ClientPhase.Disconnected);
            _log.Warn("Minecraft", "已断开: " + reason);
            try { Disconnected?.Invoke(reason); } catch (Exception ex) { _log.Error("Minecraft", "断开事件处理异常", ex); }
        }

        private void DisposeConnection()
        {
            try { _cts?.Cancel(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _tcp?.Dispose(); } catch { }
            try { _cipher?.Dispose(); } catch { }
            _stream = null;
            _tcp = null;
            _cipher = null;
            _cts = null;
        }

        public void Dispose()
        {
            DisposeConnection();
            _writeLock.Dispose();
        }
    }
}
