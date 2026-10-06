using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McBot11.Logging;

namespace McBot11.Core
{
    /// <summary>
    /// 自定义命令文件的加载与进服自动执行：
    /// 一行一条命令、忽略空行与 # 注释、进服延迟启动、按配置间隔顺序执行、
    /// 单条失败跳过继续（绝不卡死后续命令）。
    /// </summary>
    public sealed class CommandRunner
    {
        /// <summary>进服后延迟执行的毫秒数（避免被服务器判定为刷屏）。</summary>
        private const int JoinDelayMs = 2500;

        private readonly IAppLogger _log;

        public CommandRunner(IAppLogger log)
        {
            _log = log;
        }

        /// <summary>解析命令文件路径：绝对路径原样，相对路径基于程序根目录 D:\mcbot11。</summary>
        public static string ResolvePath(string configuredPath)
        {
            string p = string.IsNullOrWhiteSpace(configuredPath) ? "config/commands.txt" : configuredPath.Trim();
            return Path.IsPathRooted(p) ? p : Path.Combine(ConfigStore.RootDir, p.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>命令文件不存在时生成带注释的模板文件。</summary>
        public static void EnsureTemplateFile(string path)
        {
            try
            {
                if (File.Exists(path)) return;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var content = new StringBuilder()
                    .AppendLine("# 自定义命令文件：假人进入服务器后按顺序自动执行")
                    .AppendLine("# 一行一条命令，可带或不带开头的 /（程序自动补全）")
                    .AppendLine("# 空行与以 # 开头的行会被忽略")
                    .AppendLine("# 示例：")
                    .AppendLine("# say 机器人已进服")
                    .AppendLine("# list")
                    .ToString();
                File.WriteAllText(path, content, Encoding.UTF8);
            }
            catch
            {
                // 模板生成失败不阻断启动
            }
        }

        /// <summary>读取命令列表：跳过空行与 # 注释，去首尾空白。</summary>
        public List<string> LoadCommands(string path)
        {
            var result = new List<string>();
            try
            {
                if (!File.Exists(path)) return result;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                    result.Add(line);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Commands", $"读取命令文件失败: {path} | {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 进服后顺序执行命令文件：延迟 2.5 秒启动，每条间隔 configuredIntervalMs；
        /// 单条失败仅记录日志并跳过；连接断开（ct 取消）立即停止。
        /// send 为发送委托（chat_command 包）。
        /// </summary>
        public async Task RunAsync(Func<string, Task> send, CustomCommandsSection cfg, CancellationToken ct)
        {
            string path = ResolvePath(cfg.FilePath);
            EnsureTemplateFile(path);
            List<string> commands = LoadCommands(path);
            if (commands.Count == 0)
            {
                _log.Info("Commands", "命令文件为空或不存在，无需执行: " + path);
                return;
            }

            try
            {
                _log.Info("Commands", $"{JoinDelayMs} ms 后开始执行 {commands.Count} 条命令…");
                await Task.Delay(JoinDelayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            int interval = Math.Max(50, cfg.CommandExecuteIntervalMs);
            for (int i = 0; i < commands.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                string cmd = commands[i];
                string display = cmd.StartsWith("/", StringComparison.Ordinal) ? cmd : "/" + cmd;
                try
                {
                    await send(cmd).ConfigureAwait(false);
                    _log.Info("Commands", $"命令执行成功 ({i + 1}/{commands.Count}): {display}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Warn("Commands", $"命令发送失败，跳过 ({i + 1}/{commands.Count}): {display} | {ex.Message}");
                }

                if (i < commands.Count - 1)
                {
                    try
                    {
                        await Task.Delay(interval, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            _log.Info("Commands", "命令执行完毕");
        }
    }
}
