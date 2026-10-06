using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace McBot11.Auth
{
    /// <summary>
    /// 认证用 HTTP 工具：单例 HttpClient、禁用自动重定向（POST 301/302 会被框架
    /// 降级为 GET 丢弃请求体，Yggdrasil 根地址常有重定向）→ 手动跟随并原样重发，
    /// 自动解压 gzip/deflate，全异常包装为 AuthException。
    /// </summary>
    internal static class AuthHttp
    {
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("McBot11/1.0");
            return client;
        }

        public static async Task<(int Status, string Body)> RequestAsync(
            HttpMethod method, string url, string? jsonBody, CancellationToken ct)
        {
            string current = url;
            for (int hop = 0; hop <= 5; hop++)
            {
                using var request = new HttpRequestMessage(method, current);
                if (jsonBody != null)
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                HttpResponseMessage response;
                try
                {
                    response = await Client.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    throw new AuthException($"HTTP 请求失败: {current} - {ex.Message}", ex);
                }

                int code = (int)response.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308)
                {
                    string? loc = response.Headers.Location?.ToString();
                    response.Dispose();
                    if (string.IsNullOrEmpty(loc))
                        throw new AuthException($"重定向缺少 Location: {current}");
                    current = new Uri(new Uri(current), loc).ToString();
                    continue;
                }

                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                response.Dispose();
                return (code, body);
            }
            throw new AuthException("重定向次数过多: " + url);
        }

        public static async Task<(int Status, JsonElement Json)> PostJsonAsync(
            string url, object payload, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(payload);
            var (status, body) = await RequestAsync(HttpMethod.Post, url, json, ct).ConfigureAwait(false);
            return (status, Parse(body, url));
        }

        public static async Task<(int Status, JsonElement Json)> GetJsonAsync(string url, CancellationToken ct)
        {
            var (status, body) = await RequestAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            return (status, Parse(body, url));
        }

        public static async Task<(int Status, string Body)> PostRawAsync(string url, object payload, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(payload);
            return await RequestAsync(HttpMethod.Post, url, json, ct).ConfigureAwait(false);
        }

        /// <summary>POST JSON，主路径 404/405 时回退备用路径（兼容不同 Yggdrasil 接口布局）。</summary>
        public static async Task<(int Status, JsonElement Json)> PostJsonWithFallbackAsync(
            string primary, string fallback, object payload, CancellationToken ct)
        {
            var result = await PostJsonAsync(primary, payload, ct).ConfigureAwait(false);
            if ((result.Status == 404 || result.Status == 405) && fallback != primary)
                result = await PostJsonAsync(fallback, payload, ct).ConfigureAwait(false);
            return result;
        }

        /// <summary>POST raw，主路径 404/405 时回退备用路径。</summary>
        public static async Task<(int Status, string Body)> PostRawWithFallbackAsync(
            string primary, string fallback, object payload, CancellationToken ct)
        {
            var result = await PostRawAsync(primary, payload, ct).ConfigureAwait(false);
            if ((result.Status == 404 || result.Status == 405) && fallback != primary)
                result = await PostRawAsync(fallback, payload, ct).ConfigureAwait(false);
            return result;
        }

        private static readonly JsonDocument EmptyDoc = JsonDocument.Parse("{}");

        private static JsonElement Parse(string body, string url)
        {
            if (string.IsNullOrWhiteSpace(body)) return EmptyDoc.RootElement.Clone();
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new AuthException($"响应 JSON 解析失败: {url}", ex);
            }
        }

        /// <summary>从错误响应中提取 errorMessage（若为 JSON）。</summary>
        public static string ExtractError(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "errorMessage", "error_description", "error", "Message" })
                    {
                        if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                            return v.GetString() ?? "";
                    }
                }
            }
            catch (JsonException) { }
            return body.Length > 300 ? body.Substring(0, 300) : body;
        }
    }

    /// <summary>认证过程异常。</summary>
    public sealed class AuthException : Exception
    {
        public AuthException(string message) : base(message) { }
        public AuthException(string message, Exception inner) : base(message, inner) { }
    }
}
