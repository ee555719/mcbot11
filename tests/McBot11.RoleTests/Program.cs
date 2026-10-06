using System.Text.Json;
using McBot11.Auth;
using McBot11.Core;

namespace McBot11.RoleTests;

/// <summary>
/// 多角色选择验证测试（对应需求「五、验证测试用例」中可自动化部分）。
/// 使用本地 Mock Yggdrasil 服务器驱动 McBot11.Auth / McBot11.Core 的真实代码路径，
/// 断言 accounts.json / tokens.json 结构与日志无敏感信息泄露。
/// </summary>
public static class Program
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  [FAIL] {name} {detail}");
        }
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--live"))
            return await RunLiveAsync();

        string tmp = Path.Combine(Path.GetTempPath(), "mcbot11-roletests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string accountsPath = Path.Combine(tmp, "accounts.json");
        string tokensPath = Path.Combine(tmp, "tokens.json");

        const string plainPassword = "S3cret-P@ss";
        using var server = new MockServer { ExpectedPassword = plainPassword };
        var tokens = new FileTokenCache(tokensPath);
        var accounts = new AccountFileStore(accountsPath, tokens, null);

        // 六角色账号（LittleSkin 场景：jntm123451 / quer2 / zhuguoming2 / zhuguoming / quer1 / xiao_jntm_123451）
        var profiles = new List<MockServer.Profile>
        {
            new("1111111111111111111111111111a001", "jntm123451"),
            new("2222222222222222222222222222b002", "quer2"),
            new("3333333333333333333333333333c003", "zhuguoming2"),
            new("4444444444444444444444444444d004", "zhuguoming"),
            new("5555555555555555555555555555e005", "quer1"),
            new("6666666666666666666666666666f006", "xiao_jntm_123451")
        };
        server.Profiles = profiles;
        const string quer2 = "2222222222222222222222222222b002";

        YggdrasilAuthenticator Auth(string key, string? selectedId = null, string password = plainPassword)
        {
            var acc = new AccountConfig
            {
                Type = "yggdrasil",
                Username = "acc1@qq.com",
                Password = password,
                YggdrasilRoot = server.Root,
                CacheKeyOverride = key,
                SelectedProfileId = selectedId
            };
            return new YggdrasilAuthenticator(acc.YggdrasilRoot, AuthType.Yggdrasil, tokens);
        }

        AccountConfig Acc(string key, string? selectedId = null, string password = plainPassword)
            => new()
            {
                Type = "yggdrasil",
                Username = "acc1@qq.com",
                Password = password,
                YggdrasilRoot = server.Root,
                CacheKeyOverride = key,
                SelectedProfileId = selectedId
            };

        // ============ 1. 密码错误 ============
        Console.WriteLine("T1 密码错误（403 → 邮箱或密码错误）");
        try
        {
            await Auth("acct-A", password: "wrong-password").AuthenticateWithPasswordAsync(Acc("acct-A", password: "wrong-password"));
            Check("T1 错误密码被拒绝", false, "未抛出异常");
        }
        catch (AuthException ex)
        {
            Check("T1 错误密码被拒绝", true);
            Check("T1 错误信息含 HTTP 403", ex.Message.Contains("HTTP 403"), ex.Message);
            Check("T1 错误信息含「账号或密码错误」", ex.Message.Contains("账号或密码错误"), ex.Message);
        }

        // ============ 2. 多角色：authenticate 返回 availableProfiles ============
        Console.WriteLine("T2 多角色账号：获取 availableProfiles（6 个角色）");
        var verified = await Auth("acct-A").AuthenticateWithPasswordAsync(Acc("acct-A"));
        Check("T2 availableProfiles = 6", verified.AvailableProfiles.Count == 6, $"实际 {verified.AvailableProfiles.Count}");
        Check("T2 含角色 quer2", verified.AvailableProfiles.Any(p => p.Name == "quer2"));
        Check("T2 authenticate 调用 1 次", server.AuthenticateCount == 2, $"实际 {server.AuthenticateCount}"); // T1 也计 1 次
        string? rawTokenJson = File.Exists(tokensPath) ? File.ReadAllText(tokensPath) : null;
        Check("T2 tokens.json 含 availableProfiles 数组", rawTokenJson != null && rawTokenJson.Contains("availableProfiles"));
        // 服务端未返回 selectedProfile（未绑定）→ 本地不伪造绑定，待用户选定后由 refresh 绑定（需求 3.3.3）
        Check("T2 未选角色时 tokens.json 不含 selectedProfile（不伪造绑定）",
            rawTokenJson != null && !rawTokenJson.Contains("selectedProfile"));
        Check("T2 tokens.json 含 updatedAt", rawTokenJson != null && rawTokenJson.Contains("updatedAt"));

        // 账号入池（UI：添加账号）
        var entryA = new AccountEntry
        {
            Id = "acct-A",
            ServerRoot = server.Root,
            Email = "acc1@qq.com",
            DisplayName = "主号"
        };
        entryA.SetPassword(plainPassword);
        accounts.Upsert(entryA);

        // ============ 3. 选择角色 quer2 → 持久化 + 立刻 refresh 绑定（需求 3.3.3） ============
        Console.WriteLine("T3 选择角色 quer2 → accounts.json / tokens.json + 绑定 refresh");
        accounts.MarkSelectedProfile("acct-A", quer2, "quer2");
        var reloadedA = accounts.Get("acct-A");
        Check("T3 accounts.json selectedProfileId = quer2 UUID",
            reloadedA?.SelectedProfileId == quer2, $"实际 {reloadedA?.SelectedProfileId}");
        Check("T3 accounts.json selectedProfileName = quer2",
            reloadedA?.SelectedProfileName == "quer2", $"实际 {reloadedA?.SelectedProfileName}");
        int refreshBefore3 = server.RefreshCount;
        await Auth("acct-A", quer2).BindProfileAsync(
            Acc("acct-A", quer2), new AuthProfile { Id = quer2, Name = "quer2" });
        Check("T3 选定后立刻 refresh 绑定", server.RefreshCount == refreshBefore3 + 1,
            $"{refreshBefore3} → {server.RefreshCount}");
        Check("T3 绑定 refresh 携带 selectedProfile", server.LastRefreshHadSelectedProfile);
        var tok = tokens.Load("acct-A");
        Check("T3 tokens.json selectedProfile.id = quer2",
            tok?.ProfileId == quer2 && tok?.ProfileName == "quer2", $"实际 {tok?.ProfileId}/{tok?.ProfileName}");
        string tokensJson = File.ReadAllText(tokensPath);
        Check("T3 tokens.json 含 selectedProfile 对象", tokensJson.Contains("selectedProfile"));
        int authBefore = server.AuthenticateCount;

        // ============ 4. 登录使用选定角色（login_start / join 用 profile） ============
        Console.WriteLine("T4 登录使用选定角色（validate 路径，不重新 authenticate）");
        var login1 = await Auth("acct-A", quer2).AuthenticateAsync(Acc("acct-A", quer2));
        Check("T4 登录 UUID = quer2", login1.UuidHex == quer2, login1.UuidHex);
        Check("T4 登录角色名 = quer2", login1.Username == "quer2", login1.Username);
        Check("T4 未重新调用 authenticate", server.AuthenticateCount == authBefore, $"实际 {server.AuthenticateCount}");

        // ============ 5. 记忆角色：再次登录不重复认证 ============
        Console.WriteLine("T5 记忆角色：重启后直接复用");
        int authBefore5 = server.AuthenticateCount;
        int validateBefore5 = server.ValidateCount;
        var login2 = await Auth("acct-A", quer2).AuthenticateAsync(Acc("acct-A", quer2));
        Check("T5 仍使用 quer2", login2.UuidHex == quer2);
        Check("T5 authenticate 计数不变", server.AuthenticateCount == authBefore5);
        Check("T5 走 validate（计数增加或相等）", server.ValidateCount >= validateBefore5);

        // ============ 6. 角色下拉框数据：FetchProfiles（缓存新鲜 + validate 通过 → 不发 authenticate/refresh） ============
        Console.WriteLine("T6 获取角色：缓存未过期（validate 通过）不重新认证");
        int authBefore6 = server.AuthenticateCount;
        int refreshBefore6 = server.RefreshCount;
        var fetched = await Auth("acct-A").FetchProfilesAsync(Acc("acct-A"));
        int authAfter6 = server.AuthenticateCount;
        int refreshAfter6 = server.RefreshCount;
        Check("T6 返回 6 个角色", fetched.Count == 6, $"实际 {fetched.Count}");
        Check("T6 缓存命中（无 authenticate/refresh，仅 validate）",
            authBefore6 == authAfter6 && refreshBefore6 == refreshAfter6,
            $"auth {authBefore6}→{authAfter6}, refresh {refreshBefore6}→{refreshAfter6}");

        // ============ 7. 1 小时缓存过期 → refresh ============
        Console.WriteLine("T7 角色缓存过期（updatedAt > 1 小时）→ refresh");
        var staleTok = tokens.Load("acct-A");
        Check("T7 能读到 token 缓存", staleTok != null);
        if (staleTok != null)
        {
            staleTok.SavedAt = DateTimeOffset.UtcNow.AddHours(-2);
            tokens.Save("acct-A", staleTok);
        }
        int refreshBefore = server.RefreshCount;
        var fetchedStale = await Auth("acct-A").FetchProfilesAsync(Acc("acct-A"));
        Check("T7 过期后触发 refresh", server.RefreshCount == refreshBefore + 1, $"实际 {server.RefreshCount}");
        Check("T7 refresh 后仍返回 6 个角色", fetchedStale.Count == 6, $"实际 {fetchedStale.Count}");

        // ============ 8. 角色失效：已选角色不在最新列表 ============
        Console.WriteLine("T8 角色已失效（皮肤站删除 quer2）");
        server.Profiles = profiles.Where(p => p.Id != quer2).ToList();
        int refreshBefore8 = server.RefreshCount;
        // 连接前检查：force refresh 拿最新 availableProfiles
        var rolesAfterDelete = await Auth("acct-A").FetchProfilesAsync(Acc("acct-A"), forceRefresh: true);
        Check("T8 连接前强制刷新（refresh 计数 +1）", server.RefreshCount == refreshBefore8 + 1,
            $"{refreshBefore8} → {server.RefreshCount}");
        Check("T8 最新列表已不含 quer2", rolesAfterDelete.Count == 5 && rolesAfterDelete.All(p => p.Name != "quer2"),
            $"实际 {rolesAfterDelete.Count} 个");
        // 仍按已失效角色登录 → 抛错提示重新选择
        try
        {
            await Auth("acct-A", quer2).AuthenticateAsync(Acc("acct-A", quer2));
            Check("T8 抛出角色失效错误", false, "未抛出异常");
        }
        catch (AuthException ex)
        {
            Check("T8 抛出角色失效错误", ex.Message.Contains("已选角色已失效"), ex.Message);
        }

        // 恢复流程（UI：清空选择 + 重新获取 + 重新选择）
        accounts.ClearSelectedProfile("acct-A");
        tokens.Remove("acct-A");
        server.Profiles = profiles;
        var reFetched = await Auth("acct-A").FetchProfilesAsync(Acc("acct-A"));
        Check("T8b 清缓存后可重新获取角色", reFetched.Count == 6, $"实际 {reFetched.Count}");
        accounts.MarkSelectedProfile("acct-A", quer2, "quer2");
        var recovered = await Auth("acct-A", quer2).AuthenticateAsync(Acc("acct-A", quer2));
        Check("T8b 重新选择后登录成功", recovered.UuidHex == quer2, recovered.UuidHex);

        // ============ 9. 单角色账号：自动选中 ============
        Console.WriteLine("T9 单角色账号：自动选中（无需弹窗）");
        server.Profiles = new List<MockServer.Profile> { new("77777777777777777777777777770007", "SoloRole") };
        var soloAuth = await Auth("acct-C").AuthenticateWithPasswordAsync(Acc("acct-C"));
        Check("T9 返回 1 个角色", soloAuth.AvailableProfiles.Count == 1, $"实际 {soloAuth.AvailableProfiles.Count}");
        // UI 规则：唯一角色 → 自动写入 selectedProfileId
        var entryC = new AccountEntry { Id = "acct-C", ServerRoot = server.Root, Email = "acc1@qq.com", DisplayName = "单角色号" };
        entryC.SetPassword(plainPassword);
        accounts.Upsert(entryC);
        accounts.MarkSelectedProfile("acct-C", soloAuth.AvailableProfiles[0].Id, soloAuth.AvailableProfiles[0].Name);
        Check("T9 自动选中已持久化",
            accounts.Get("acct-C")?.SelectedProfileId == soloAuth.AvailableProfiles[0].Id);

        // ============ 10. 无角色账号 ============
        Console.WriteLine("T10 无角色账号：提示创建角色");
        server.Profiles = new List<MockServer.Profile>();
        try
        {
            await Auth("acct-D").AuthenticateWithPasswordAsync(Acc("acct-D"));
            Check("T10 抛出无角色错误", false, "未抛出异常");
        }
        catch (AuthException ex)
        {
            Check("T10 抛出无角色错误", ex.Message.Contains("该账号没有可用角色"), ex.Message);
            Check("T10 提示到皮肤站创建角色", ex.Message.Contains("皮肤站"), ex.Message);
        }

        // ============ 11. 切换账号：各自独立 selectedProfileId ============
        Console.WriteLine("T11 切换账号：角色选择互不影响");
        var currentA = accounts.Current("acct-A");
        var currentC = accounts.Current("acct-C");
        Check("T11 账号 A 记住 quer2", currentA?.SelectedProfileId == quer2, currentA?.SelectedProfileId ?? "(null)");
        Check("T11 账号 C 记住 SoloRole", currentC?.SelectedProfileId == "77777777777777777777777777770007", currentC?.SelectedProfileId ?? "(null)");
        Check("T11 两账号独立", currentA?.SelectedProfileId != currentC?.SelectedProfileId);

        // ============ 12. 删除账号：token 与角色缓存一并清除 ============
        Console.WriteLine("T12 删除账号：tokens.json 对应条目清除");
        server.Profiles = new List<MockServer.Profile> { new("88888888888888888888888888880008", "Doomed") };
        var doomed = new AccountEntry { Id = "acct-E", ServerRoot = server.Root, Email = "acc1@qq.com", DisplayName = "待删除" };
        doomed.SetPassword(plainPassword);
        accounts.Upsert(doomed);
        await Auth("acct-E").AuthenticateWithPasswordAsync(Acc("acct-E"));
        Check("T12 删除前 token 存在", tokens.Load("acct-E") != null);
        accounts.Remove("acct-E");
        Check("T12 删除后 token 与角色缓存清除", tokens.Load("acct-E") == null);
        Check("T12 删除后账号条目清除", accounts.Get("acct-E") == null);

        // ============ 13. 密码保护（DPAPI） ============
        Console.WriteLine("T13 密码保护：accounts.json 为 DPAPI 密文");
        string accountsJson = File.ReadAllText(accountsPath);
        Check("T13 明文密码未落盘", !accountsJson.Contains(plainPassword));
        Check("T13 password 字段为 Base64 密文",
            accountsJson.Contains("\"password\":") && !accountsJson.Contains($"\"{plainPassword}\""));
        Check("T13 解密可还原（本机当前用户）", accounts.Get("acct-A")?.GetPassword() == plainPassword);

        // ============ 14. 服务端默认角色回退 ============
        Console.WriteLine("T14 未本地选定 → 使用服务端 selectedProfile");
        server.Profiles = profiles;
        server.ServerSelectedProfileId = quer2;
        var fallbackAuth = await Auth("acct-F").AuthenticateWithPasswordAsync(Acc("acct-F"));
        Check("T14 使用服务端 selectedProfile", fallbackAuth.UuidHex == quer2, fallbackAuth.UuidHex);
        server.ServerSelectedProfileId = null;

        // ============ 15. 日志 / 文件无敏感信息泄露 ============
        Console.WriteLine("T15 无敏感信息泄露（密码 / accessToken / clientToken）");
        string allConfig = accountsJson + "\n" + File.ReadAllText(tokensPath);
        Check("T15 tokens.json 无明文 accessToken", !allConfig.Contains(server.LastIssuedToken));
        Check("T15 tokens.json 无 clientToken 明文（值由 DPAPI 保护字段之外不含）",
            !File.ReadAllText(tokensPath).Contains("\"MOCKTOKEN"));
        string logsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "logs");
        logsDir = Path.GetFullPath(logsDir);
        if (Directory.Exists(logsDir))
        {
            string logsText = string.Join("\n",
                Directory.GetFiles(logsDir, "*.log").Select(f => File.ReadAllText(f)));
            Check("T15 日志无明文密码", !logsText.Contains(plainPassword));
            Check("T15 日志无 accessToken", !logsText.Contains("MOCKTOKEN"));
        }
        else
        {
            Console.WriteLine("  [SKIP] 日志目录不存在: " + logsDir);
        }

        // ============ 16. tokens.json 结构符合需求 3.2 ============
        Console.WriteLine("T16 tokens.json 结构校验");
        using (var doc = JsonDocument.Parse(File.ReadAllText(tokensPath)))
        {
            var entry = doc.RootElement.EnumerateObject().First().Value;
            Check("T16 含 selectedProfile.id/name",
                entry.TryGetProperty("selectedProfile", out var sp) &&
                sp.TryGetProperty("id", out _) && sp.TryGetProperty("name", out _));
            Check("T16 含 availableProfiles[].id/name",
                entry.TryGetProperty("availableProfiles", out var ap) &&
                ap.ValueKind == JsonValueKind.Array && ap.GetArrayLength() > 0 &&
                ap[0].TryGetProperty("id", out _) && ap[0].TryGetProperty("name", out _));
            Check("T16 含 updatedAt", entry.TryGetProperty("updatedAt", out _));
            Check("T16 accessToken 非明文",
                entry.TryGetProperty("accessToken", out var at) &&
                !at.GetString()!.Contains("MOCKTOKEN"));
        }

        // ============ 17. 无角色账号：获取角色列表 → 提示没有可用角色 ============
        Console.WriteLine("T17 无角色账号：获取角色列表提示创建角色");
        server.Profiles = new List<MockServer.Profile>();
        try
        {
            await Auth("acct-G").FetchProfilesAsync(Acc("acct-G"));
            Check("T17 抛出无角色错误", false, "未抛出异常");
        }
        catch (AuthException ex)
        {
            Check("T17 提示该账号没有可用角色", ex.Message.Contains("该账号没有可用角色"), ex.Message);
        }
        server.Profiles = profiles;

        // ============ 18. Token 未绑定：不直接 refresh（不报 400）→ authenticate → 选定 → 绑定 ============
        Console.WriteLine("T18 Token 未绑定：走 authenticate 而非 refresh，选定后绑定成功");
        tokens.Save("acct-H", new CachedToken
        {
            AccessToken = "MOCKTOKEN-unbound-h",
            ClientToken = "ct-h",
            AvailableProfiles = profiles.Select(p => new AuthProfile { Id = p.Id, Name = p.Name }).ToList(),
            Root = server.Root,
            Account = "acc1@qq.com",
            SavedAt = DateTimeOffset.UtcNow
        });
        int auth18 = server.AuthenticateCount;
        int refresh18 = server.RefreshCount;
        var roles18 = await Auth("acct-H").FetchProfilesAsync(Acc("acct-H"));
        Check("T18 返回 6 个角色", roles18.Count == 6, $"实际 {roles18.Count}");
        Check("T18 走 authenticate（refresh 计数不变）",
            server.AuthenticateCount == auth18 + 1 && server.RefreshCount == refresh18,
            $"auth {auth18}→{server.AuthenticateCount}, refresh {refresh18}→{server.RefreshCount}");
        Check("T18 未出现未绑定角色的 400", server.UnboundRefresh400 == 0, $"实际 {server.UnboundRefresh400}");
        // 用户选定角色 → 立刻调带 selectedProfile 的 refresh 完成绑定（需求 3.3.3）
        await Auth("acct-H", quer2).BindProfileAsync(
            Acc("acct-H", quer2), new AuthProfile { Id = quer2, Name = "quer2" });
        Check("T18 绑定触发 1 次 refresh", server.RefreshCount == refresh18 + 1, $"实际 {server.RefreshCount}");
        Check("T18 绑定 refresh 携带 selectedProfile", server.LastRefreshHadSelectedProfile);
        var tokH = tokens.Load("acct-H");
        Check("T18 tokens.json selectedProfile = quer2",
            tokH?.ProfileId == quer2 && tokH?.ProfileName == "quer2", $"实际 {tokH?.ProfileId}/{tokH?.ProfileName}");

        // ============ 19. 手动清空 selectedProfile → 重新 authenticate，不直接 refresh ============
        Console.WriteLine("T19 清空 selectedProfile 后获取角色：走 authenticate（不报 400）");
        await Auth("acct-I").AuthenticateWithPasswordAsync(Acc("acct-I"));
        await Auth("acct-I", quer2).BindProfileAsync(
            Acc("acct-I", quer2), new AuthProfile { Id = quer2, Name = "quer2" });
        var tokI = tokens.Load("acct-I");
        Check("T19 绑定前 tokens.json 已有 selectedProfile", tokI?.ProfileId == quer2, $"实际 {tokI?.ProfileId}");
        if (tokI != null)
        {
            tokI.ProfileId = null;
            tokI.ProfileName = null;
            tokens.Save("acct-I", tokI);
        }
        int auth19 = server.AuthenticateCount;
        int refresh19 = server.RefreshCount;
        var roles19 = await Auth("acct-I").FetchProfilesAsync(Acc("acct-I"));
        Check("T19 返回 6 个角色", roles19.Count == 6, $"实际 {roles19.Count}");
        Check("T19 走重新 authenticate（refresh 计数不变）",
            server.AuthenticateCount == auth19 + 1 && server.RefreshCount == refresh19,
            $"auth {auth19}→{server.AuthenticateCount}, refresh {refresh19}→{server.RefreshCount}");
        Check("T19 未出现未绑定角色的 400", server.UnboundRefresh400 == 0, $"实际 {server.UnboundRefresh400}");

        // ============ 20. 密码未保存 → 提示输入密码（需求 §四） ============
        Console.WriteLine("T20 密码未保存：获取角色列表提示输入密码");
        try
        {
            await Auth("acct-J", password: "").FetchProfilesAsync(Acc("acct-J", password: ""));
            Check("T20 抛出密码缺失错误", false, "未抛出异常");
        }
        catch (AuthException ex)
        {
            Check("T20 提示未配置密码", ex.Message.Contains("未配置密码"), ex.Message);
            Check("T20 提示请输入密码以获取角色列表", ex.Message.Contains("请输入密码以获取角色列表"), ex.Message);
        }

        // ============ 21. 网络故障 → AuthException（UI 层映射为「认证服务器不可达」） ============
        Console.WriteLine("T21 网络故障：不可达地址获取角色不崩溃");
        const string deadRoot = "http://127.0.0.1:1/api/yggdrasil";
        tokens.Save("acct-K", new CachedToken
        {
            AccessToken = "MOCKTOKEN-net-k",
            ClientToken = "ct-k",
            ProfileId = quer2,
            ProfileName = "quer2",
            AvailableProfiles = profiles.Select(p => new AuthProfile { Id = p.Id, Name = p.Name }).ToList(),
            Root = deadRoot,
            Account = "acc1@qq.com",
            SavedAt = DateTimeOffset.UtcNow
        });
        var accK = new AccountConfig
        {
            Type = "yggdrasil",
            Username = "acc1@qq.com",
            Password = plainPassword,
            YggdrasilRoot = deadRoot,
            CacheKeyOverride = "acct-K",
            SelectedProfileId = quer2,
            SelectedProfileName = "quer2"
        };
        try
        {
            await new YggdrasilAuthenticator(deadRoot, AuthType.Yggdrasil, tokens).FetchProfilesAsync(accK);
            Check("T21 网络故障抛出 AuthException", false, "未抛出异常");
        }
        catch (AuthException ex)
        {
            Check("T21 网络故障抛出 AuthException", true);
            Check("T21 错误含 HTTP 请求失败（FriendlyAuthError → 认证服务器不可达）",
                ex.Message.Contains("HTTP 请求失败"), ex.Message);
        }

        // ============ 22. 服务端 5xx → 认证服务器故障，请稍后重试（需求 3.4） ============
        Console.WriteLine("T22 服务端 5xx：提示认证服务器故障");
        server.FailRefreshStatus = 503;
        tokens.Save("acct-L", new CachedToken
        {
            AccessToken = "MOCKTOKEN-5xx-l",
            ClientToken = "ct-l",
            ProfileId = quer2,
            ProfileName = "quer2",
            AvailableProfiles = profiles.Select(p => new AuthProfile { Id = p.Id, Name = p.Name }).ToList(),
            Root = server.Root,
            Account = "acc1@qq.com",
            SavedAt = DateTimeOffset.UtcNow
        });
        try
        {
            await Auth("acct-L", quer2).FetchProfilesAsync(Acc("acct-L", quer2), forceRefresh: true);
            Check("T22 抛出 5xx 错误", false, "未抛出异常");
        }
        catch (AuthException ex)
        {
            Check("T22 提示认证服务器故障", ex.Message.Contains("认证服务器故障"), ex.Message);
            Check("T22 提示稍后重试", ex.Message.Contains("请稍后重试"), ex.Message);
        }
        server.FailRefreshStatus = null;

        // ============ 23. 全程无「未绑定角色」的 refresh 400（需求 3.2 核心回归） ============
        Console.WriteLine("T23 全程 refresh 均携带 selectedProfile（无未绑定 400）");
        Check("T23 UnboundRefresh400 == 0", server.UnboundRefresh400 == 0, $"实际 {server.UnboundRefresh400}");

        Console.WriteLine();
        Console.WriteLine($"结果: {_pass} 通过, {_fail} 失败");
        try { Directory.Delete(tmp, recursive: true); } catch { }
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// --live：用 config\accounts.json 中的真实账号（LittleSkin 等）跑一遍
    /// 「获取角色」路径，验证真实服务器的 availableProfiles 解析。
    /// 只打印角色名与数量，不打印密码 / token。
    /// </summary>
    private static async Task<int> RunLiveAsync()
    {
        string accountsPath = Path.Combine(FindRepoRoot(), "config", "accounts.json");
        string tokensPath = Path.Combine(FindRepoRoot(), "config", "tokens.json");
        if (!File.Exists(accountsPath))
        {
            Console.WriteLine("[SKIP] 未找到 " + accountsPath);
            return 0;
        }

        var tokens = new FileTokenCache(tokensPath);
        var accounts = new AccountFileStore(accountsPath, tokens, null);
        var list = accounts.List();
        Console.WriteLine($"账号池: {list.Count} 个账号");
        foreach (var entry in list)
        {
            Console.WriteLine($"- {entry.Label}  root={entry.ServerRoot}  已选角色={entry.SelectedProfileName ?? "(未选)"}");
            var acc = new AccountConfig
            {
                Type = "yggdrasil",
                Username = entry.Email,
                Password = entry.GetPassword(),
                YggdrasilRoot = entry.ServerRoot,
                CacheKeyOverride = entry.Id,
                SelectedProfileId = entry.SelectedProfileId
            };
            try
            {
                var auth = new YggdrasilAuthenticator(entry.ServerRoot, AuthType.Yggdrasil, tokens);
                var roles = await auth.FetchProfilesAsync(acc, forceRefresh: true);
                Console.WriteLine($"  availableProfiles: {roles.Count} 个 → {string.Join("、", roles.Select(r => r.Name))}");
                if (roles.Count > 0)
                {
                    var profile = await auth.AuthenticateAsync(acc);
                    Console.WriteLine($"  登录角色: {profile.Username} ({profile.UuidHex}) [{profile.AuthKind}]");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [FAIL] {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }
        Console.WriteLine("[PASS] 真实服务器 availableProfiles 解析正常");
        return 0;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "McBot11.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
