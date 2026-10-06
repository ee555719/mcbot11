# McBot11 — Minecraft ↔ QQ 双向消息桥接机器人

在 **Minecraft 服务器（假人登录）** 与 **QQ 群/私聊（OneBot 11）** 之间双向转发聊天消息的 Windows 桌面程序。

- **技术栈**：C# / .NET 8 / WPF，Windows 10 x64
- **MC 协议栈、Yggdrasil / 微软认证链、OneBot 11 客户端**：全部自研（无第三方 MC 客户端库 / QQ 框架）
- **第三方依赖**：仅 `CommunityToolkit.Mvvm`（UI MVVM 框架）

---

## 1. 构建与运行

### 环境要求

- Windows 10 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（构建）或 .NET 8 Runtime（仅运行）

### 构建

```powershell
# 方式一：构建脚本
.\build.ps1            # Debug
.\build.ps1 -Release   # Release
.\build.ps1 -Run       # 构建并启动

# 方式二：直接使用 dotnet CLI
dotnet build McBot11.sln
```

构建产物：`src\McBot11.App\bin\Debug\net8.0-windows\McBot11.App.exe`

### 运行

双击 `McBot11.App.exe`，或：

```powershell
.\build.ps1 -Run
```

**首次运行**会自动生成以下目录与文件（根目录固定为 `D:\mcbot11`）：

```
D:\mcbot11\
├── config\
│   ├── config.json     # 首次运行自动生成的默认配置
│   └── tokens.json     # 认证 token 缓存（不含密码）
└── logs\
    └── mcbot-yyyyMMdd.log   # 日志，按天切分
```

---

## 2. 功能

### 2.1 Minecraft 假人客户端（自研协议栈）

| 能力 | 说明 |
|---|---|
| 协议版本 | **767~777**（1.21 ~ 26.3），可选"自动"（status 查询服务器协议） |
| 登录方式 | `offline` / `mojang`（Yggdrasil）/ `microsoft`（设备码）/ `yggdrasil`（LittleSkin 等） |
| 特性 | 会话加密（CFB8）、压缩、配置阶段应答、传送确认、断线自动重连 |

支持协议版本对照：

| 客户端版本 | 协议号 |
|---|---|
| 1.21 / 1.21.1 | 767 |
| 1.21.2 / 1.21.3 | 768 |
| 1.21.4 | 769 |
| 1.21.5 | 770 |
| 1.21.6 | 771 |
| 1.21.7 / 1.21.8 | 772 |
| 1.21.9 / 1.21.10 | 773 |
| 1.21.11 | 774 |
| 26.1.x | 775 |
| 26.2 | 776 |
| 26.3 | 777 |

### 2.2 认证（全自研，纯 `HttpClient`）

- **offline**：UUID 按玩家名 MD5（version 3）派生
- **Yggdrasil / Mojang**：`/authserver/authenticate`、`/refresh`、`/validate`、session `join`
  （yggdrasil 走 authlib-injector 前缀路径 `/authserver/*`、`/sessionserver/session/minecraft/join`，404 时自动回退标准路径，反之亦然）
  token 缓存到 `config\tokens.json`，优先 refresh 复用；**不保存密码以外的凭据、不落盘密码**
- **Microsoft**：Azure OAuth2 device code → Xbox Live → XSTS → `login_with_xbox` → 权益校验 → 游戏档案
  refresh token 缓存于 `tokens.json`
- Yggdrasil `GET /` 元数据（skinDomains / signaturePublickey / serverName）用于连接前测试
- 登录失败按 HTTP 状态与错误内容分类提示（403 密码错、404 路径错、无档案、无权益等）

### 2.2.1 多角色选择使用说明

一个第三方账号（如 LittleSkin）下可有多个角色，登录假人时使用其中**一个**：

1. **主界面「登录账号」下拉框**下方是**「登录角色」下拉框**，展示当前账号的可选角色
   （数据来自 `availableProfiles`，从 `tokens.json` 离线读取，切换账号时自动刷新）。
2. 点击**「获取角色」**按钮从认证服务器拉取最新角色列表：
   - 0 个角色 → 日志提示「该账号没有可用角色，请先到皮肤站创建角色」
   - 1 个角色 → 自动选中，无需干预
   - 多个角色 → 填充下拉框，由用户选择
3. **点击「连接 MC」**时若尚未选定角色且存在多个角色，会弹出
   **「选择使用的角色」窗口**（单选 RadioButton + 确定/取消）：
   - 选择并确定 → 该角色写入 `accounts.json` 的 `selectedProfileId` / `selectedProfileName`，
     并同步 `tokens.json` 的 `selectedProfile`，下次启动直接复用，不再弹窗
   - 点击取消 → 中止登录，日志输出「用户取消了角色选择」
4. **添加 / 编辑账号**对话框内有「获取角色列表」按钮，可预览角色并点击选择；
   保存后若只有 1 个角色则自动选中，多个角色弹出选择窗口。
5. 若已选角色在皮肤站被删除：登录时会提示「已选角色已失效」，
   自动清除缓存并重新拉取角色列表，再次弹窗让用户重新选择。
6. **删除账号**时 `tokens.json` 中对应的角色缓存与 token 一并清除；
   **编辑账号（改密码 / 根地址）**后角色缓存被清空，强制重新获取。

选定角色的 `id`（32 位无横线 UUID）与 `name` 用于 `login_start` 包与
`session/minecraft/join` 的 `selectedProfile` 字段，并回写 `accounts.json`、`tokens.json`。

### 2.2.2 Yggdrasil `availableProfiles` 处理逻辑

- `POST /authserver/authenticate`（密码登录）与 `POST /authserver/refresh` 的响应中，
  解析 `availableProfiles` 数组的每一项 `{ "id": "<32位UUID>", "name": "<角色名>" }`，
  去重后全量保存到 `tokens.json` 该账号条目的 `availableProfiles` 字段。
- `selectedProfile`（服务端默认角色）与本地 `selectedProfileId` 的优先级：
  1. 本地已选（`accounts.json.selectedProfileId`）且存在于 `availableProfiles` → 使用本地选定角色
  2. 未本地选定 → 使用服务端返回的 `selectedProfile`
  3. 连 `selectedProfile` 都没有，但 `availableProfiles` 非空 → 使用列表第一个角色
  4. 两者都为空 → 抛出「该账号没有可用角色，请先到皮肤站创建角色」
- 认证成功后，选中的角色会同步覆盖 `tokens.json` 的 `selectedProfile`，
  确保后续 `validate` / `refresh` / `join` 使用同一角色。
- `offline` 不涉及角色列表；`microsoft` 本次做兼容处理（不参与角色选择流程，
  其 `availableProfiles` 保持为空，登录行为与此前一致）。

### 2.2.3 角色缓存失效策略（1 小时过期）

- `tokens.json` 每个条目的 `updatedAt` 记录**最近一次 authenticate/refresh 的时间**
  （`CachedToken.SavedAt`；保存 token 时不再刷新该时间，只有真正重新认证才更新）。
- 判断规则：`updatedAt` 距当前时间 **超过 1 小时** 即视为缓存过期。
- 过期后的读取顺序（「获取角色」与连接前检查一致）：
  1. 有 token → 先 `POST /authserver/refresh` 拿最新 `availableProfiles`
  2. refresh 失败（403 等）且有密码 → 用密码 `authenticate` 重新登录
  3. 无密码 → 报「请重新输入密码」，由界面弹窗补录
- 未过期时直接使用缓存中的 `availableProfiles`（已绑定角色时先 `validate`，通过则不发 refresh）。
- 手动点「获取角色」也会先检查过期：未过期（validate 通过）直接读缓存，过期 / 强制刷新则带 `selectedProfile` refresh。

### 2.2.4 Yggdrasil Token 绑定角色处理逻辑

Yggdrasil 的 `refresh` 接口存在两种令牌状态（LittleSkin 等 authlib-injector 服务）：

| 状态 | 判断依据 | 处理方式 |
|------|----------|----------|
| Token 已绑定角色 | `tokens.json` 条目有 `selectedProfile`（或 `accounts.json` 已选角色可解析） | `refresh` **始终携带 `selectedProfile`**（保持一致性，见下） |
| Token 未绑定角色 | 有 accessToken 但本地无法解析 `selectedProfile`（如多角色账号从未选过角色、或手动清空） | **不允许直接 refresh**（否则服务端 HTTP 400 `The access token has no profile assigned`）→ 必须先 `authenticate` 拿 `availableProfiles` 给用户选择，选定后再调带 `selectedProfile` 的 `refresh` 完成绑定 |

- **`refresh` 请求体固定构造**（需求 3.1）：

  ```json
  {
    "accessToken": "...",
    "clientToken": "...",
    "selectedProfile": { "id": "<32位UUID>", "name": "<角色名>" },
    "requestUser": true
  }
  ```

  `selectedProfile` 的 `id` 与 `name` 必须**同时**给出：优先取 `accounts.json` 已选角色
  （在 `availableProfiles` 中查名字，或用 `selectedProfileName` 兜底），其次取 `tokens.json`
  已绑定角色；两者都解析不出名字时按未绑定处理（走 `authenticate`），绝不发送残缺字段。
- **选定角色后的即时绑定**（需求 3.3.3）：下拉框切换角色 / 弹窗选定 / 唯一角色自动选中后，
  立刻调一次带 `selectedProfile` 的 `refresh` 让服务器完成令牌绑定，
  并把 `accessToken` / `clientToken` / `selectedProfile` / `availableProfiles` 写入 `tokens.json`、
  把 `selectedProfileId` / `selectedProfileName` 写入 `accounts.json`（对应
  `YggdrasilAuthenticator.BindProfileAsync` / `BridgeService.BindSelectedRoleAsync`）。
- **`tokens.json` 的 `selectedProfile` 只写服务端返回值**：`authenticate` 未返回
  `selectedProfile` 时本地不伪造绑定（首次登录多角色账号在选定前该字段为空）。
- **错误映射（需求 3.4）**：
  - HTTP 400 + `no profile assigned` → `该账号未绑定角色，请先选择角色后再登录`（中英文对照），触发选择角色流程
  - HTTP 401/403 → token 失效，丢弃缓存后重新 `authenticate`
  - HTTP 5xx → `认证服务器故障，请稍后重试 (HTTP xxx)`
  - 网络异常 → `HTTP 请求失败: …`（界面翻译为 `认证服务器不可达`），不崩溃

> 说明：登录路径（`AuthenticateAsync`）在 token 有效但本地无绑定时，仍保留「自动绑定到
> 已选角色/第一个角色」的兜底（该路径的 refresh 同样携带 `selectedProfile`，不会触发 400）；
> 界面连接前已通过「获取角色 → 选择角色」流程先行绑定，此兜底仅覆盖无 UI 调用场景。

### 2.2.5 获取角色列表的完整状态机说明

`YggdrasilAuthenticator.FetchProfilesAsync`（「获取角色」按钮、连接前检查、账号保存后解析共用）
按 `GetTokenState(cached, account)` 判定状态后走三条路径：

```
                 ┌────────────────────────────────────────────────────────────┐
                 │ 本地无 accessToken → NoToken                               │
   取 tokens.json ├────────────────────────────────────────────────────────────┤
   判定绑定状态   │ 有 token，无法解析 selectedProfile → TokenUnbound          │
                 │  → 不允许直接 refresh，走 authenticate（需密码）            │
                 │  → 无密码：抛「未配置密码（请输入密码以获取角色列表）」     │
                 │    界面据此弹窗补录密码后重试                               │
                 └────────────────────────────────────────────────────────────┘
                 ┌────────────────────────────────────────────────────────────┐
                 │ 有 token 且可解析 selectedProfile → TokenBound             │
                 │  1) 缓存 1 小时内且非强制刷新 → validate 通过 → 直接返回   │
                 │     缓存 availableProfiles（不发 refresh）                 │
                 │  2) 否则 → 带 selectedProfile 的 refresh：                 │
                 │     200 → 返回最新 availableProfiles                       │
                 │     400 未绑定 → 重新 authenticate → 交给用户选择          │
                 │     401/403 → 丢弃缓存 → 重新 authenticate                │
                 │     5xx → 抛「认证服务器故障，请稍后重试」                 │
                 │     网络异常 → 抛「HTTP 请求失败」（→ 认证服务器不可达）   │
                 └────────────────────────────────────────────────────────────┘
   用户选定角色 → BindProfileAsync：带 selectedProfile 的 refresh 完成绑定 → 写入
   tokens.json（selectedProfile/availableProfiles）+ accounts.json（selectedProfileId/Name）
   返回空列表 → 界面提示「该账号没有可用角色，请先到皮肤站创建角色」
```

- 单角色账号选定后无需弹窗（自动选中并绑定）；多角色且无有效已选时弹窗单选，取消则仅填充下拉框。
- 全流程回归断言：**任何路径都不会出现「未绑定令牌直接 refresh」**（离线测试 T18/T19/T23）。

### 2.3 OneBot 11 正向 WebSocket（自研）

- `ws://` / `wss://` 连接，可选 `Authorization: Bearer <token>`
- 断线指数退避自动重连
- 解析 `group_message` / `private_message`（消息数组段与 CQ 码双通道解析，@ 提取）
- 发送 `send_group_msg` / `send_private_msg`，`echo` 配对等待 API 应答
- `get_login_info` 自动学习机器人自身 QQ 号（用于过滤自发消息）

### 2.4 消息路由（回环过滤）

**QQ → MC**

- 群消息默认需 **@ 机器人**（可关闭），且群号在允许列表中
- 私聊需 QQ 号在允许列表中
- 格式：`@机器人 <游戏名字> <消息>`，MC 中显示为：

  ```
  <MyBot> (张三) (Steve) hello world
  ```

  即 `<假人名> (QQ名字) (输入的游戏名字) 消息`
- 私聊格式：`<游戏名字> <消息>`，输出格式相同
- 模板可配置：`qqToMcTemplate`，变量 `{botName}` `{qqName}` `{gameName}` `{message}`
- 超长截断到 256 字符

**MC → QQ**

- 转发到配置的目标群 / 目标私聊
- **玩家消息**：从 `player_chat` / `player_info_update` 解析真实玩家名，模板
  `mcToQqTemplate`（默认 `[MC] <{mcName}> {message}`），效果如 `[MC] <Steve> hi`
  （玩家名未知时跳过该条，绝不输出占位符）
- **系统消息**：模板独立 `systemMessageTemplate`（默认 `[MC] <Server> {message}`）；
  `system_chat` 的 translate 翻译键经 `SystemMessageLocalizer` 映射为中文
  （如 `multiplayer.player.joined` → `有玩家加入了游戏`，未知键原样输出不崩溃）
- 可选是否包含假人自身发出的消息（默认过滤）

**回环过滤**

- QQ→MC 发出的消息记录指纹（规范化 + FNV-1a 哈希），15 秒窗口内
- 服务器把同一条消息广播回来时命中指纹 → 丢弃，避免同一条消息在 QQ 与 MC 之间无限循环

### 2.6 自定义命令与 QQ 远程命令

**进服自动执行（customCommands）**

- 命令文件 `D:\mcbot11\config\commands.txt`（路径可在 config.json 修改，首次运行自动生成模板）
- 一行一条命令（可带可不带 `/`），空行与 `#` 注释忽略
- 假人进入 Play 阶段 2.5 秒后按顺序执行，每条间隔 `commandExecuteIntervalMs`（默认 1000ms）
- 使用 **`chat_command` 包**发送（按 767~777 版本映射包 ID），单条失败记录日志并跳过继续

**QQ 远程执行命令（commandPermissions，安全白名单）**

- 群聊：`@机器人 /say hello`；私聊：`/say hello`
- **权限独立于普通转发白名单**：`allowCommandGroups` / `allowCommandPrivate`
  默认为空 = 任何人无法执行；无权限时回复提示并记录审计日志
- 执行结果回复：`[MC] 已发送命令：/{command}` / `命令发送失败：{原因}`
- 空命令（仅 `/`）忽略并回复提示

### 2.5 WPF 界面

- **Minecraft 区**：地址/端口/名字/协议下拉/登录方式（字段随登录方式动态切换）/「测试认证」/自动重连
- **OneBot 区**：WS 地址、Token、启动/停止、连接状态
- **路由区**：双向开关、群号/私聊号列表（逗号分隔）、@ 要求、包含自身消息
- **模板区**：两条消息格式模板
- **日志区**：颜色区分级别（Debug 灰 / Info 白 / Warn 黄 / Error 红），自动滚动，最多保留 500 行；同时写入 `logs\`
- **按钮**：保存配置 / 保存并重启桥接 / 清空日志

---

## 3. 配置说明（config.json）

```jsonc
{
  "minecraft": {
    "host": "127.0.0.1",
    "port": 25565,
    "username": "MyBot",          // 假人名字
    "protocolVersion": null,      // null = 自动（status 查询）；或 767~777
    "authType": "offline",        // offline | mojang | microsoft | yggdrasil
    "auth": {
      "offline":   { "username": "MyBot" },
      "mojang":    { "email": "", "password": "" },
      "microsoft": { "refreshToken": "" },   // 可留空，tokens.json 会缓存
      "yggdrasil": {
        "serverRoot": "https://littleskin.cn/api/yggdrasil",
        "email": "", "password": ""
      }
    },
    "autoReconnect": true,
    "reconnectIntervalSeconds": 10
  },
  "onebot": {
    "wsUrl": "ws://127.0.0.1:3001",
    "accessToken": "",
    "reconnectIntervalSeconds": 5
  },
  "routing": {
    "mcToQQ": {
      "enabled": true,
      "targetGroups": [],            // MC→QQ 目标群号列表
      "targetPrivate": [],           // MC→QQ 目标私聊列表
      "includeBotSelfMessage": false
    },
    "qqToMc": {
      "enabled": true,
      "allowGroups": [],             // 允许触发的群号（空 = 不允许任何群）
      "allowPrivate": [],            // 允许触发的私聊（空 = 不允许任何私聊）
      "requireAtInGroup": true       // 群内必须 @ 机器人才转发
    }
  },
  "format": {
    "qqToMcTemplate": "<{botName}> ({qqName}) ({gameName}) {message}",
    "mcToQqTemplate": "[MC] <{mcName}> {message}",
    "systemMessageTemplate": "[MC] <Server> {message}"
  },
  "customCommands": {
    "enabled": true,
    "filePath": "config/commands.txt",   // 相对路径基于 D:\mcbot11
    "executeOnJoin": true,
    "commandExecuteIntervalMs": 1000,
    "commandPrefix": "/"
  },
  "commandPermissions": {
    "allowCommandGroups": [],            // 可远程执行 MC 命令的群（默认空 = 无人可执行）
    "allowCommandPrivate": [],           // 可远程执行 MC 命令的私聊 QQ 号
    "replyOnExecute": true,
    "replyOnFailure": true
  }
}
```

配置在界面上编辑后点 **保存配置**（热更新路由/模板）或 **保存并重启桥接**（重建连接）。
`config.json` 损坏时会自动备份为 `config.json.broken.<时间戳>` 并重建默认值。

---

## 4. 典型使用流程

1. 启动你的 QQ 机器人框架（NapCat / go-cqhttp 等，启用 **OneBot 11 正向 WebSocket**），确认端口
2. 运行 `McBot11.exe`，在 **OneBot 区** 填 `ws://127.0.0.1:<端口>`，点 **启动 OneBot**
3. 在 **MC 区** 填服务器地址与登录方式，点 **测试认证**（微软账号会弹设备码），再点 **连接 MC**
4. 在 **路由区** 填群号（MC→QQ 的 `targetGroups`，QQ→MC 的 `allowGroups`），保存
5. QQ 群里发 `@机器人 Steve 你好` → MC 中出现 `<MyBot> (QQ名字) (Steve) 你好`
6. MC 聊天栏消息 → 按 `[MC] <玩家名> 消息` 转发到配置的 QQ 群/私聊

---

## 5. 项目结构

```
McBot11.sln
src\
├── McBot11.Protocol\    自研 MC 协议基础设施：帧编解码(Varint/CFB8 加密/压缩)、
│                        包读写、NBT(Network)、Text 组件、包 ID 表(767~777)、
│                        会话共享密钥/服务器哈希(Java 兼容)、日志接口
├── McBot11.Auth\        认证：offline / Yggdrasil / Mojang / Microsoft(device code)
│                        AuthHttp(AllowAutoRedirect=false, 404/405 回退)、
│                        UUID 工具、session join、token 缓存接口
├── McBot11.Minecraft\   自研客户端：握手/登录/配置/播放状态机、status 查询、
│                        事件化聊天输出、传送确认、聊天发送、断开处理
├── McBot11.OneBot11\    自研 OneBot 11 正向 WS 客户端：事件解析、CQ 码还原、
│                        API 调用(echo 配对)、自动重连
├── McBot11.Core\        配置(config.json/tokens.json)、文件+UI 双通道日志、
│                        回环过滤、桥接编排(路由/模板/重连)
└── McBot11.App\         WPF 界面 + MainViewModel
```

---

## 6. 数据来源披露（协议事实参考）

本项目**未使用任何现成的 Minecraft 客户端库或 MC↔QQ 桥接项目的代码**，所有协议实现为自研。
协议层面的**事实性数据**（各版本包 ID、包结构、字段名与语义）参考以下公开资料：

- [PrismarineJS/minecraft-data](https://github.com/PrismarineJS/minecraft-data) — `data/pc/common/protocol.json`
  与 `data/pc/<version>/version.json`（1.21 ~ 26.2 包 ID 与结构事实，协议 767~775 为事实基础，776/777 部分结构对照其版本数据）
- [minecraft.wiki](https://minecraft.wiki/) — [Protocol wiki](https://minecraft.wiki/w/Protocol) 及其 [wiki.vg 历史页](https://wiki.vg/Protocol)
  （776/777 即 26.2/26.3 的当前修订内容、`accept_teleportation` 带坐标、`chat_command` 结构、
  `client_information` 第 9 字段 `particleStatus` 分界等，均以 wiki 修订版本比对确认）

引用内容仅限**协议规格事实**（字段、编号、语义），不涉及上述项目的任何源代码。

---

## 7. 已知说明

- MC 聊天发送对 770+ 携带 `acknowledged` 校验和（空 last-seen 集时为 `1`），已按当前协议实现
- 聊天广播的回环过滤为**文本指纹匹配**（15 秒窗口），不做逐包关联
- Microsoft 账号需要已拥有 Minecraft 正版权益；device code 流程在界面弹窗中展示验证链接与代码
- 离线服务器 / 正服（Mojang session）/ LittleSkin 等 Yggdrasil 皮肤站均可接入
- 「QQ 发消息时键盘被注入奇怪字符串」的注入源在本程序之外（详见第 8.1 节排查结论）

---

## 8. 已知问题修复记录

### 8.1 Bug：MC 发消息后键盘被注入奇怪字符串（QQ 发消息时键盘输入被污染）

- **排查结论**：全仓检索 `SendKeys` / `keybd_event` / `SendInput` / `SetWindowsHookEx` / `user32`
  / `DllImport` / `Marshal` / `RegisterHotKey` / `Process.Start` / `cmd.exe` 等关键词 **零命中**，
  本项目**不存在任何键盘模拟、进程控制系统注入或系统 Hook 代码**。
- 日志中出现的可疑串（形如 `QQ private 208808551 … get_recent_context send_message MCP`）
  是以**玩家消息**形式进入 MC 聊天框、被服务器广播回流的文本，不是经本程序 QQ→MC
  转发路径产生的（该路径只输出模板渲染后的玩家消息，并记录 `→ MC` 日志）。
- **注入源在本程序之外**：opencode 的 qq-agent 唤醒系统使用 `SendInput` 聚焦目标窗口
  （窗口标题匹配 `opencode/OC/Administrator/cmd`，快捷键 `ctrl+l`），会把唤醒提示文本键入
  当前焦点窗口。是否调整或停用该唤醒规则由使用方决定，**本项目侧无需删除代码**。
- 本项目继续遵守约束：**绝不引入**键盘模拟、进程控制系统注入、系统 Hook 类代码。

### 8.2 Bug：MC 收到消息但 QQ 收不到（已修复）

- **根因**：`MinecraftClient.TryReadPlayerName` 解析 `player_info_update` 时**线序与位序全错**——
  先读 `count`（实际首字节是 `action`）、把 `count` 当 `uuid` 读、按位条件字段映射也全错，
  异常被静默吞掉，导致 uuid → 玩家名映射**恒为空**；于是 `player_chat` 拿不到发送者名，
  路由层按「玩家名未知，跳过该条玩家消息」丢弃全部玩家消息（系统消息不受影响）。
- **修复内容**（`src\McBot11.Minecraft\MinecraftClient.cs`）：
  - 按正确结构重写：`action: u8 bitflags` → `count: varint` → 每条 `uuid(16)` + 按位条件字段；
    字段物理顺序 `player → chatSession → gamemode → listed → latency → displayName →
    listPriority(varint) → showHat(bool)`（以 minecraft-data `protocol.json` 767/768/769/774/775 事实源比对）
  - 位掩码按版本区分：767 = 6 位、768 = 7 位、769+ = 8 位；出现本协议未知位时记 Warn 并中止本包剩余条目解析
  - `0x40` / `0x80` 在 minecraft-data 与 wiki 两套约定下位义相反（hat/listOrder 互换），
    两字段一律按 **VarInt 读取且固定按 0x40→0x80 顺序消费**，四种位组合在两套约定下字节消耗均精确对齐（值不使用）
  - `chat_session` 按 `option<uuid + publicKey{expireTime, key, sig}` 解析；`displayName` 按
    `option<anonymousNbt>`（存在位 + NBT，`NbtReader` 容错探测类型字节）
  - 新增 **`player_info_remove`** 处理：按 uuid 清理玩家名映射，避免陈旧/串号
  - 解析失败由**静默吞异常**改为 **Warn 日志**（含 actions/count/entry 上下文），
    并补充成功解析的 Debug 摘要；单包失败不影响连接（包已独立成帧）
- **全链路日志增强**（`src\McBot11.Core\BridgeService.cs`）：
  收到 MC 消息（Info）→ 回环/自发过滤（Debug）→ 渲染内容（Info）→ 转发成功（Info）/失败（Warn）；
  「玩家名未知」由 Debug 升为 **Warn**；QQ→MC 侧原本已有 `→ MC` Info 日志。

### 8.3 修复验证

- **协议结构**：与 `protocol.json`（1.21.1/1.21.3/1.21.4/1.21.11/26.1）逐字段比对确认
- **字节级对齐测试**：10 项用例全部通过（767 双条目含属性与 NBT、774 全 8 位含会话与
  NBT、769/768 单扩展位、`0x40/0x80` 在 minecraft-data 与 wiki 两套约定下的全部单/双位组合、
  未知位告警、`player_info_remove` 清理、会话缺省对齐）
- **构建**：`dotnet build McBot11.sln` → 0 警告 0 错误
- 端到端用例（QQ 发消息不控键盘 / MC `hello` → QQ 收到 `[MC] <玩家名> hello` /
  加入·死亡·退出翻译 / 群 `@Bot /say hello` 远程命令 / `logs\` 完整日志）见交付测试结论

---

## 9. 许可与免责

本项目仅供学习与自用。使用第三方认证服务（Microsoft / 皮肤站）时请遵守其服务条款。
