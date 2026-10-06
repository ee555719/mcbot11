using System;

namespace McBot11.Logging
{
    /// <summary>日志级别</summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    /// <summary>
    /// 统一日志接口。定义在 Protocol 程序集中，供各类库通过依赖注入使用；
    /// 实现（文件 + UI 双通道、按天切分）由 McBot11.Core 提供。
    /// </summary>
    public interface IAppLogger
    {
        /// <summary>写一条日志。source 为产生日志的组件名（如 "Minecraft"、"OneBot"）。</summary>
        void Log(LogLevel level, string source, string message);

        /// <summary>便捷方法：Debug 级日志。</summary>
        void Debug(string source, string message) => Log(LogLevel.Debug, source, message);

        /// <summary>便捷方法：Info 级日志。</summary>
        void Info(string source, string message) => Log(LogLevel.Info, source, message);

        /// <summary>便捷方法：Warn 级日志。</summary>
        void Warn(string source, string message) => Log(LogLevel.Warn, source, message);

        /// <summary>便捷方法：Error 级日志（含异常详情）。</summary>
        void Error(string source, string message, Exception? ex = null)
            => Log(LogLevel.Error, source, ex == null ? message : message + " | " + ex);
    }

    /// <summary>空日志实现（单元测试 / 库默认值）。</summary>
    public sealed class NullLogger : IAppLogger
    {
        public static readonly NullLogger Instance = new NullLogger();
        public void Log(LogLevel level, string source, string message) { }
    }
}
