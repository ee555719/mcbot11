using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McBot11.Logging;

namespace McBot11.OneBot11
{
    /// <summary>OneBot API 调用失败异常。</summary>
    public sealed class OneBotApiException : Exception
    {
        public int Retcode { get; }
        public OneBotApiException(string message, int retcode) : base(message) { Retcode = retcode; }
    }

    /// <summary>
    /// 自研 OneBot v11 正向 WebSocket 客户端：
    /// 事件接收（group/private message）、API 调用（echo 配对）、断线指数退避重连、
    /// 可选 access_token 头。不依赖任何现成 OneBot 框架。
    /// </summary>
    public sealed class OneBotClient : IDisposable
    {
        private readonly IAppLogger _log;
        private OneBotOptions _opt = new OneBotOptions();
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private int _state = (int)OneBotState.Disconnected;
        private bool _manualClose;
        private int _reconnectDelayMs = 1000;
        private long _echoSeq;
        private long _selfId;
        private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending =
            new Dictionary<string, TaskCompletionSource<JsonElement>>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        public OneBotClient(IAppLogger? log = null)
        {
            _log = log ?? NullLogger.Instance;
        }

        /// <summary>收到群消息（WS 接收线程触发）。</summary>
        public event Action<GroupMessageArgs>? GroupMessageReceived;

        /// <summary>收到私聊消息。</summary>
        public event Action<PrivateMessageArgs>? PrivateMessageReceived;

        /// <summary>连接状态变化。</summary>
        public event Action<OneBotState>? StateChanged;

        /// <summary>登录的机器人 QQ 号（从事件/登录信息学习，0=未知）。</summary>
        public long SelfId => Interlocked.Read(ref _selfId);

        public OneBotState State => (OneBotState)_state;

        private void SetState(OneBotState s)
        {
            Interlocked.Exchange(ref _state, (int)s);
            try { StateChanged?.Invoke(s); } catch (Exception ex) { _log.Error("OneBot", "状态事件异常", ex); }
        }

        /// <summary>启动（后台连接与自动重连循环）。</summary>
        public void Start(OneBotOptions options)
        {
            _opt = options ?? throw new ArgumentNullException(nameof(options));
            _manualClose = false;
            _reconnectDelayMs = Math.Max(200, options.ReconnectIntervalMs);
            if (_cts == null || _cts.IsCancellationRequested)
                _cts = new CancellationTokenSource();
            _ = Task.Run(ConnectLoopAsync);
        }

        /// <summary>停止并保持不再重连。</summary>
        public void Stop()
        {
            _manualClose = true;
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Dispose(); } catch { }
            _ws = null;
            SetState(OneBotState.Disconnected);
        }

        private async Task ConnectLoopAsync()
        {
            var token = _cts?.Token ?? CancellationToken.None;
            Uri uri;
            try
            {
                uri = new Uri(_opt.Url);
            }
            catch (Exception ex)
            {
                _log.Error("OneBot", "无效的 WebSocket 地址: " + _opt.Url, ex);
                SetState(OneBotState.Disconnected);
                return;
            }

            while (!_manualClose && !token.IsCancellationRequested)
            {
                SetState(OneBotState.Connecting);
                try
                {
                    var ws = new ClientWebSocket();
                    if (!string.IsNullOrEmpty(_opt.AccessToken))
                        ws.Options.SetRequestHeader("Authorization", "Bearer " + _opt.AccessToken);
                    await ws.ConnectAsync(uri, token).ConfigureAwait(false);
                    _ws = ws;
                    _reconnectDelayMs = Math.Max(200, _opt.ReconnectIntervalMs);
                    SetState(OneBotState.Connected);
                    _log.Info("OneBot", "已连接 OneBot: " + _opt.Url);
                    _ = Task.Run(() => QueryLoginInfoAsync(token), CancellationToken.None);
                    await ReceiveLoopAsync(ws, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (!_manualClose)
                        _log.Warn("OneBot", "连接异常: " + ex.Message);
                }

                _ws = null;
                FailAllPending("连接已断开");
                if (_manualClose || token.IsCancellationRequested) break;
                SetState(OneBotState.Disconnected);
                _log.Info("OneBot", $"{_reconnectDelayMs / 1000.0:F1} 秒后重连…");
                try
                {
                    await Task.Delay(_reconnectDelayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, 30_000);
            }
            SetState(OneBotState.Disconnected);
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[16 * 1024];
            using var ms = new MemoryStream();
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.Info("OneBot", "服务器关闭 WebSocket: " + (result.CloseStatusDescription ?? ""));
                        return;
                    }
                    ms.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    try
                    {
                        HandleMessage(json);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("OneBot", "事件处理异常: " + Trunc(json), ex);
                    }
                }
            }
        }

        private void HandleMessage(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            // API 响应（带 echo）
            if (root.TryGetProperty("echo", out var echoEl) &&
                (root.TryGetProperty("retcode", out _) || root.TryGetProperty("status", out _)))
            {
                string echo = echoEl.ToString();
                TaskCompletionSource<JsonElement>? tcs;
                lock (_pending) _pending.TryGetValue(echo, out tcs);
                if (tcs != null)
                {
                    string status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                    if (status == "ok")
                    {
                        tcs.TrySetResult(root.Clone());
                    }
                    else
                    {
                        int retcode = root.TryGetProperty("retcode", out var rc) && rc.TryGetInt32(out int r) ? r : -1;
                        string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" :
                                     root.TryGetProperty("wording", out var wd) ? wd.GetString() ?? "" : "API 调用失败";
                        tcs.TrySetException(new OneBotApiException(msg, retcode));
                    }
                }
                return;
            }

            if (root.TryGetProperty("self_id", out var selfEl) && selfEl.TryGetInt64(out long selfId))
                Interlocked.Exchange(ref _selfId, selfId);

            if (!root.TryGetProperty("post_type", out var pt)) return;
            string postType = pt.GetString() ?? "";
            if (postType != "message") return;

            string messageType = root.TryGetProperty("message_type", out var mt) ? mt.GetString() ?? "" : "";

            var messageEl = root.TryGetProperty("message", out var msgEl) ? msgEl : default;
            string raw = root.TryGetProperty("raw_message", out var rm) ? rm.GetString() ?? "" : "";
            long senderId = root.TryGetProperty("user_id", out var uid) && uid.TryGetInt64(out long u) ? u : 0;
            long self = root.TryGetProperty("self_id", out var sid) && sid.TryGetInt64(out long s) ? s : 0;

            if (messageType == "group")
            {
                long groupId = root.TryGetProperty("group_id", out var gid) && gid.TryGetInt64(out long g) ? g : 0;
                string nickname = "", card = "";
                if (root.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object)
                {
                    if (sender.TryGetProperty("nickname", out var nn)) nickname = nn.GetString() ?? "";
                    if (sender.TryGetProperty("card", out var cd)) card = cd.GetString() ?? "";
                }
                string text;
                IReadOnlyList<long> ats;
                if (messageEl.ValueKind == JsonValueKind.Array || messageEl.ValueKind == JsonValueKind.String)
                {
                    var (t, a) = OneBotMessageParser.ParseMessageArray(messageEl);
                    text = t;
                    ats = a.Count > 0 ? a : OneBotMessageParser.ParseAtFromRaw(raw);
                }
                else
                {
                    text = raw;
                    ats = OneBotMessageParser.ParseAtFromRaw(raw);
                }
                bool atBot = self != 0 && ats.Contains(self);
                GroupMessageReceived?.Invoke(new GroupMessageArgs
                {
                    GroupId = groupId,
                    UserId = senderId,
                    Nickname = nickname,
                    Card = card,
                    Text = text,
                    RawMessage = raw,
                    AtList = ats,
                    AtBot = atBot,
                    SelfId = self
                });
            }
            else if (messageType == "private")
            {
                string nickname = "";
                if (root.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object &&
                    sender.TryGetProperty("nickname", out var nn))
                    nickname = nn.GetString() ?? "";
                string text;
                if (messageEl.ValueKind == JsonValueKind.Array || messageEl.ValueKind == JsonValueKind.String)
                    text = OneBotMessageParser.ParseMessageArray(messageEl).Text;
                else
                    text = raw;
                PrivateMessageReceived?.Invoke(new PrivateMessageArgs
                {
                    UserId = senderId,
                    Nickname = nickname,
                    Text = text,
                    RawMessage = raw,
                    SelfId = self
                });
            }
        }

        /// <summary>调用 OneBot API（按 echo 配对响应）。</summary>
        public async Task<JsonElement> CallApiAsync(string action, object? parameters, CancellationToken ct = default)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open)
                throw new InvalidOperationException("OneBot 未连接");
            string echo = Interlocked.Increment(ref _echoSeq).ToString();
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) _pending[echo] = tcs;
            try
            {
                var payload = JsonSerializer.SerializeToElement(new
                {
                    action,
                    @params = parameters ?? new { },
                    echo
                });
                await SendJsonAsync(payload, ct).ConfigureAwait(false);
                await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_pending) _pending.Remove(echo);
            }
        }

        /// <summary>发送群消息（纯文本）。</summary>
        public Task SendGroupMessageAsync(long groupId, string text, CancellationToken ct = default)
            => CallApiAsync("send_group_msg", new { group_id = groupId, message = text }, ct);

        /// <summary>发送私聊消息（纯文本）。</summary>
        public Task SendPrivateMessageAsync(long userId, string text, CancellationToken ct = default)
            => CallApiAsync("send_private_msg", new { user_id = userId, message = text }, ct);

        private async Task QueryLoginInfoAsync(CancellationToken ct)
        {
            try
            {
                var info = await CallApiAsync("get_login_info", null, ct).ConfigureAwait(false);
                if (info.TryGetProperty("data", out var data) && data.TryGetProperty("user_id", out var uid) &&
                    uid.TryGetInt64(out long self))
                {
                    Interlocked.Exchange(ref _selfId, self);
                    _log.Info("OneBot", "机器人 QQ: " + self);
                }
            }
            catch (Exception ex)
            {
                _log.Debug("OneBot", "get_login_info 失败: " + ex.Message);
            }
        }

        private async Task SendJsonAsync(JsonElement payload, CancellationToken ct)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            var ws = _ws ?? throw new InvalidOperationException("OneBot 未连接");
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ws.State != WebSocketState.Open)
                    throw new InvalidOperationException("OneBot 连接已关闭");
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void FailAllPending(string reason)
        {
            List<TaskCompletionSource<JsonElement>> list;
            lock (_pending)
            {
                list = new List<TaskCompletionSource<JsonElement>>(_pending.Values);
                _pending.Clear();
            }
            foreach (var tcs in list)
                tcs.TrySetException(new OneBotApiException(reason, -1));
        }

        private static string Trunc(string s) => s.Length > 300 ? s.Substring(0, 300) : s;

        public void Dispose()
        {
            Stop();
            _sendLock.Dispose();
        }
    }
}
