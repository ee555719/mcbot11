using System;
using System.Collections.Generic;
using McBot11.Protocol.IO;

namespace McBot11.Protocol.Nbt
{
    /// <summary>
    /// 网络 NBT（Network NBT / Anonymous NBT）读取器，只读不写。
    /// 模型映射：Compound → Dictionary&lt;string, object&gt;，List → List&lt;object&gt;，
    /// 数值 → sbyte/short/int/long/float/double/string/byte[]/int[]/long[]。
    /// 1.20.2+ 网络 NBT 无根名称；根类型字节在 1.21.5+ 是否移除未最终确认，
    /// 因此做容错探测：首个字节为 0x0A 时先按“带类型字节”解析，解析失败或
    /// 消费字节数异常过小时回退按“无类型字节”解析。
    /// </summary>
    public static class NbtReader
    {
        private const int MaxDepth = 64;
        private const int MaxArrayLength = 1_000_000;

        /// <summary>读取一个网络 NBT 复合标签（聊天组件、断开原因等场景）。</summary>
        public static Dictionary<string, object> ReadCompound(PacketReader r)
        {
            if (r.Remaining >= 2 && r.PeekByte() == 0x0A)
            {
                int mark = r.Position;
                r.Skip(1);
                try
                {
                    int before = r.Position;
                    var typed = ReadPayload(r, 10, 0);
                    if (typed is Dictionary<string, object> dict && r.Position - before >= 4)
                        return dict;
                }
                catch (ProtocolException) { }
                r.Position = mark;
            }
            if (ReadPayload(r, 10, 0) is Dictionary<string, object> result)
                return result;
            throw new ProtocolException("网络 NBT 根标签不是复合标签");
        }

        private static object ReadPayload(PacketReader r, byte type, int depth)
        {
            if (depth > MaxDepth) throw new ProtocolException("NBT 嵌套过深");
            switch (type)
            {
                case 1: return unchecked((sbyte)r.ReadByte());
                case 2: return r.ReadInt16();
                case 3: return r.ReadInt32();
                case 4: return r.ReadInt64();
                case 5: return r.ReadSingle();
                case 6: return r.ReadDouble();
                case 7:
                {
                    int len = r.ReadInt32();
                    if (len < 0 || len > MaxArrayLength) throw new ProtocolException("NBT byte[] 长度非法: " + len);
                    return r.ReadBytes(len);
                }
                case 8: return r.ReadNbtString();
                case 9:
                {
                    byte elemType = r.ReadByte();
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxArrayLength) throw new ProtocolException("NBT 列表长度非法: " + count);
                    var list = new List<object>(Math.Min(count, 4096));
                    for (int i = 0; i < count; i++)
                        list.Add(ReadPayload(r, elemType, depth + 1));
                    return list;
                }
                case 10:
                {
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    while (true)
                    {
                        byte entryType = r.ReadByte();
                        if (entryType == 0) break;
                        string name = r.ReadNbtString();
                        dict[name] = ReadPayload(r, entryType, depth + 1);
                    }
                    return dict;
                }
                case 11:
                {
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxArrayLength) throw new ProtocolException("NBT int[] 长度非法: " + count);
                    var arr = new int[count];
                    for (int i = 0; i < count; i++) arr[i] = r.ReadInt32();
                    return arr;
                }
                case 12:
                {
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxArrayLength) throw new ProtocolException("NBT long[] 长度非法: " + count);
                    var arr = new long[count];
                    for (int i = 0; i < count; i++) arr[i] = r.ReadInt64();
                    return arr;
                }
                default:
                    throw new ProtocolException("未知 NBT 标签类型: " + type);
            }
        }
    }
}
