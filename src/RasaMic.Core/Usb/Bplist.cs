using System.Buffers.Binary;
using System.Text;

namespace RasaMic.Core.Usb;

/// <summary>
/// Minimal binary plist (bplist00) codec for the usbmuxd protocol subset:
/// dicts with string keys, ASCII/UTF-16 strings, integers, bools, arrays.
/// </summary>
public static class Bplist
{
    public static byte[] Write(object? root)
    {
        var flat = new List<object?>();
        Flatten(root, flat);
        int refSize = flat.Count <= 0xFF ? 1 : flat.Count <= 0xFFFF ? 2 : 4;

        var body = new MemoryStream();
        body.Write("bplist00"u8);
        var offsets = new List<long>();
        foreach (object? node in flat)
        {
            offsets.Add(body.Position);
            WriteObject(body, node, flat, refSize);
        }
        long tableOffset = body.Position;
        int offsetSize = tableOffset <= 0xFF ? 1 : tableOffset <= 0xFFFF ? 2 : 4;
        foreach (long off in offsets)
            WriteBE(body, off, offsetSize);

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)flat.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0); // top object
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)tableOffset);
        body.Write(trailer);
        return body.ToArray();
    }

    public static object? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40 || !data[..8].SequenceEqual("bplist00"u8))
            throw new InvalidDataException("Not a binary plist");
        var trailer = data[^32..];
        int offsetSize = trailer[6];
        int refSize = trailer[7];
        long numObjects = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        long top = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        long tableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
        if (numObjects <= 0 || top >= numObjects || tableOffset >= data.Length - 32)
            throw new InvalidDataException("Bad plist trailer");

        var offsets = new long[numObjects];
        for (long i = 0; i < numObjects; i++)
            offsets[i] = ReadBE(data.Slice((int)(tableOffset + i * offsetSize), offsetSize));
        return ParseObject(data, offsets, top, refSize);
    }

    private static object? ParseObject(ReadOnlySpan<byte> data, long[] offsets, long index, int refSize)
    {
        long offset = offsets[index];
        byte marker = data[(int)offset];
        int type = marker >> 4, info = marker & 0xF;
        int pos = (int)offset + 1;
        switch (type)
        {
            case 0x0:
                return info switch { 8 => false, 9 => true, _ => null };
            case 0x1:
                return ReadBE(data.Slice(pos, 1 << info));
            case 0x5:
            {
                int len = ReadLength(data, ref pos, info);
                return Encoding.ASCII.GetString(data.Slice(pos, len));
            }
            case 0x6:
            {
                int len = ReadLength(data, ref pos, info);
                return Encoding.BigEndianUnicode.GetString(data.Slice(pos, len * 2));
            }
            case 0xA:
            {
                int count = ReadLength(data, ref pos, info);
                var list = new List<object?>(count);
                for (int i = 0; i < count; i++, pos += refSize)
                    list.Add(ParseObject(data, offsets, ReadBE(data.Slice(pos, refSize)), refSize));
                return list;
            }
            case 0xD:
            {
                int count = ReadLength(data, ref pos, info);
                var keys = new string[count];
                for (int i = 0; i < count; i++, pos += refSize)
                    keys[i] = (string)ParseObject(data, offsets, ReadBE(data.Slice(pos, refSize)), refSize)!;
                var dict = new Dictionary<string, object?>(count);
                for (int i = 0; i < count; i++, pos += refSize)
                    dict[keys[i]] = ParseObject(data, offsets, ReadBE(data.Slice(pos, refSize)), refSize);
                return dict;
            }
            default:
                throw new InvalidDataException($"Unsupported plist object type 0x{type:X}");
        }
    }

    private static int ReadLength(ReadOnlySpan<byte> data, ref int pos, int info)
    {
        if (info != 0xF)
            return info;
        byte marker = data[pos++];
        if (marker >> 4 != 0x1)
            throw new InvalidDataException("Bad extended length");
        int n = 1 << (marker & 0xF);
        long len = ReadBE(data.Slice(pos, n));
        pos += n;
        return (int)len;
    }

    private static int Flatten(object? node, List<object?> flat)
    {
        int index = flat.Count;
        flat.Add(node);
        switch (node)
        {
            case Dictionary<string, object?> dict:
                foreach (var (k, v) in dict)
                {
                    Flatten(k, flat);
                    Flatten(v, flat);
                }
                break;
            case IList<object?> list:
                foreach (object? item in list)
                    Flatten(item, flat);
                break;
        }
        return index;
    }

    private static void WriteObject(MemoryStream s, object? node, List<object?> flat, int refSize)
    {
        switch (node)
        {
            case null:
                s.WriteByte(0x00);
                break;
            case bool b:
                s.WriteByte(b ? (byte)0x09 : (byte)0x08);
                break;
            case long n:
                WriteInt(s, n);
                break;
            case int n:
                WriteInt(s, n);
                break;
            case string str:
                WriteString(s, str);
                break;
            case IList<object?> list:
                WriteContainerHeader(s, 0xA, list.Count);
                foreach (object? item in list)
                    WriteBE(s, flat.IndexOf(item), refSize);
                break;
            case Dictionary<string, object?> dict:
                WriteContainerHeader(s, 0xD, dict.Count);
                foreach (string k in dict.Keys)
                    WriteBE(s, flat.IndexOf(k), refSize);
                foreach (object? v in dict.Values)
                    WriteBE(s, flat.IndexOf(v), refSize);
                break;
            default:
                throw new NotSupportedException($"Unsupported plist value {node.GetType()}");
        }
    }

    private static void WriteInt(MemoryStream s, long n)
    {
        int size = n is >= 0 and <= 0xFF ? 1 : n is >= 0 and <= 0xFFFF ? 2 : n is >= 0 and <= 0xFFFFFFFF ? 4 : 8;
        s.WriteByte((byte)(0x10 | (size switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 })));
        WriteBE(s, n, size);
    }

    private static void WriteString(MemoryStream s, string str)
    {
        bool ascii = str.All(c => c < 0x80);
        byte[] bytes = ascii ? Encoding.ASCII.GetBytes(str) : Encoding.BigEndianUnicode.GetBytes(str);
        int charLen = ascii ? bytes.Length : bytes.Length / 2;
        s.WriteByte((byte)((ascii ? 0x50 : 0x60) | Math.Min(charLen, 0xF)));
        if (charLen >= 0xF)
            WriteInt(s, charLen);
        s.Write(bytes);
    }

    private static void WriteContainerHeader(MemoryStream s, int type, int count)
    {
        s.WriteByte((byte)((type << 4) | Math.Min(count, 0xF)));
        if (count >= 0xF)
            WriteInt(s, count);
    }

    private static void WriteBE(MemoryStream s, long value, int size)
    {
        for (int i = size - 1; i >= 0; i--)
            s.WriteByte((byte)(value >> (8 * i)));
    }

    private static long ReadBE(ReadOnlySpan<byte> bytes)
    {
        long v = 0;
        foreach (byte b in bytes)
            v = (v << 8) | b;
        return v;
    }
}