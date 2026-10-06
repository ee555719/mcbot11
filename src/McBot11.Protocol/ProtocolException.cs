using System;

namespace McBot11.Protocol
{
    /// <summary>协议编解码/校验失败时抛出的异常。</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
        public ProtocolException(string message, Exception inner) : base(message, inner) { }
    }
}
