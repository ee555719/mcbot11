using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace McBot11.RoleTests;

/// <summary>
/// 迷你 Yggdrasil 模拟服务器（TcpListener，免 URL ACL）：
/// GET  {root}/                 → 元数据
/// POST {root}/authserver/authenticate → accessToken + selectedProfile? + availableProfiles
/// POST {root}/authserver/refresh      → 同上（换发 accessToken）
/// POST {root}/authserver/validate     → 204
/// 密码不符返回 403；可配置角色列表 / 服务端默认角色 / 请求计数。
/// </summary>
public sealed class MockServer : IDisposable
{
    public sealed record Profile(string Id, string Name);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public string ExpectedPassword { get; set; } = "S3cret-P@ss";
    public List<Profile> Profiles { get; set; } = new();
    /// <summary>服务端默认角色（authenticate 响应的 selectedProfile；null = 不返回）。</summary>
    public string? ServerSelectedProfileId { get; set; }
    public string Root { get; }

    public int AuthenticateCount;
    public int RefreshCount;
    public int ValidateCount;
    public string LastIssuedToken = "";

    /// <summary>refresh 请求携带 selectedProfile 的次数（最近一次 refresh 是否携带）。</summary>
    public bool LastRefreshHadSelectedProfile;
    /// <summary>未绑定角色的 token 直接 refresh 被拒 400 的次数（需求 3.2 违规计数，断言应为 0）。</summary>
    public int UnboundRefresh400;
    /// <summary>强制 refresh 返回指定状态码（模拟 5xx 故障；null = 正常处理）。</summary>
    public int? FailRefreshStatus;

    private readonly object _bindLock = new();
    /// <summary>token → 绑定角色（模拟 LittleSkin 服务端侧的令牌-角色绑定）。</summary>
    private readonly Dictionary<string, Profile> _bindings = new();

    public MockServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Root = $"http://127.0.0.1:{port}/api/yggdrasil";
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);

                string? requestLine = await reader.ReadLineAsync();
                if (requestLine == null) return;

                long contentLength = 0;
                while (true)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null || line.Length == 0) break;
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        long.TryParse(line.Substring(15).Trim(), out contentLength);
                }

                char[] bodyBuf = new char[contentLength];
                int read = 0;
                while (read < bodyBuf.Length)
                {
                    int n = await reader.ReadAsync(bodyBuf.AsMemory(read, bodyBuf.Length - read));
                    if (n <= 0) break;
                    read += n;
                }
                string body = new string(bodyBuf, 0, read);

                string[] parts = requestLine.Split(' ');
                string method = parts.Length > 0 ? parts[0] : "";
                string path = parts.Length > 1 ? parts[1] : "";

                var (status, json) = Route(method, path, body);
                await WriteAsync(stream, status, json);
            }
            catch
            {
                // 连接被客户端关闭等，忽略
            }
        }
    }

    private (int Status, string Body) Route(string method, string path, string body)
    {
        if (method == "GET" && path.TrimEnd('/').EndsWith("yggdrasil", StringComparison.Ordinal))
        {
            return (200, JsonSerializer.Serialize(new
            {
                skinDomains = new[] { ".littleskin.cn" },
                signaturePublickey = "",
                meta = new { serverName = "MockSkin", implementationName = "mock", implementationVersion = "1.0" }
            }));
        }

        if (method == "POST" && path.Contains("authenticate", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref AuthenticateCount);
            string password = TryJsonString(body, "password") ?? "";
            if (password != ExpectedPassword)
                return (403, """{"errorMessage":"Forbidden","error":"Forbidden"}""");

            string token = "MOCKTOKEN-" + Guid.NewGuid().ToString("N");
            LastIssuedToken = token;
            // 服务端默认角色存在时：authenticate 即绑定令牌 ↔ 角色
            Profile? selected = null;
            if (ServerSelectedProfileId != null)
                selected = Profiles.FirstOrDefault(x => x.Id == ServerSelectedProfileId);
            if (selected != null)
            {
                lock (_bindLock) _bindings[token] = selected;
            }
            return (200, BuildAuthResponse(token, selected));
        }

        if (method == "POST" && path.Contains("refresh", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref RefreshCount);
            // 需求 3.1：检查 refresh 是否携带 selectedProfile
            var (reqId, reqName) = TryJsonProfile(body);
            LastRefreshHadSelectedProfile = reqId != null;

            if (FailRefreshStatus is int failStatus)
                return (failStatus, $$"""{"errorMessage":"Mock refresh failure (HTTP {{failStatus}})","error":"Error"}""");

            string oldToken = TryJsonString(body, "accessToken") ?? "";
            Profile? bound = null;
            if (reqId != null)
            {
                // 请求指定角色 → 服务端完成绑定（对不存在的角色也按请求回显，便于测试角色删除场景）
                bound = new Profile(reqId, reqName ?? reqId);
            }
            else
            {
                lock (_bindLock)
                {
                    if (!string.IsNullOrEmpty(oldToken) && _bindings.TryGetValue(oldToken, out var existing))
                        bound = existing;
                }
            }

            if (bound == null)
            {
                // LittleSkin 行为：未绑定角色的令牌直接 refresh → 400（需求 3.2 违规路径）
                Interlocked.Increment(ref UnboundRefresh400);
                return (400, """{"errorMessage":"访问令牌未绑定角色，请在刷新时指定角色。The access token has no profile assigned, please select a profile.","error":"Bad Request"}""");
            }

            string newToken = "MOCKTOKEN-" + Guid.NewGuid().ToString("N");
            LastIssuedToken = newToken;
            lock (_bindLock) _bindings[newToken] = bound;
            return (200, BuildAuthResponse(newToken, bound));
        }

        if (method == "POST" && path.Contains("validate", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref ValidateCount);
            return (204, "");
        }

        return (404, """{"errorMessage":"Not Found"}""");
    }

    private string BuildAuthResponse(string accessToken, Profile? selected)
    {
        object? selectedObj = selected == null ? null : new { id = selected.Id, name = selected.Name };
        var available = Profiles.Select(p => new { id = p.Id, name = p.Name }).ToArray();
        return JsonSerializer.Serialize(new
        {
            accessToken,
            clientToken = Guid.NewGuid().ToString("N"),
            selectedProfile = selectedObj,
            availableProfiles = available,
            user = new { id = Guid.NewGuid().ToString("N") }
        });
    }

    private static string? TryJsonString(string body, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析请求体中的 selectedProfile.id / selectedProfile.name。</summary>
    private static (string? Id, string? Name) TryJsonProfile(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.TryGetProperty("selectedProfile", out var sp) && sp.ValueKind == JsonValueKind.Object)
            {
                string? id = sp.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() : null;
                string? name = sp.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() : null;
                return (id, name);
            }
        }
        catch
        {
            // 忽略：按未携带处理
        }
        return (null, null);
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string body)
    {
        string reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "Error"
        };
        byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
        string head =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes);
        if (payload.Length > 0) await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(1000); } catch { }
        _cts.Dispose();
    }
}
