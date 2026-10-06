using System.Buffers.Binary;

namespace PKForgeJa.Patcher;

/// <summary>LZ4 ブロック形式の展開（.NET for Android のアセンブリストア "XALZ" 用）。</summary>
public static class Lz4
{
    public static byte[] Decode(ReadOnlySpan<byte> src, int outputLength)
    {
        var dst = new byte[outputLength];
        int si = 0, di = 0;
        while (si < src.Length)
        {
            int token = src[si++];
            int lit = token >> 4;
            if (lit == 15)
            {
                int b;
                do { b = src[si++]; lit += b; } while (b == 255);
            }
            src.Slice(si, lit).CopyTo(dst.AsSpan(di));
            si += lit;
            di += lit;
            if (si >= src.Length) break;
            int offset = src[si] | (src[si + 1] << 8);
            si += 2;
            int len = token & 15;
            if (len == 15)
            {
                int b;
                do { b = src[si++]; len += b; } while (b == 255);
            }
            len += 4;
            int from = di - offset;
            if (offset <= 0 || from < 0) throw new InvalidDataException("LZ4 データが壊れています");
            for (int k = 0; k < len; k++) dst[di + k] = dst[from + k];
            di += len;
        }
        if (di != outputLength) throw new InvalidDataException($"LZ4 展開サイズ不一致 ({di} != {outputLength})");
        return dst;
    }
}

public static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

internal static class Le
{
    public static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
    public static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
    public static ulong U64(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d[o..]);
    public static void W16(Span<byte> d, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(d[o..], (ushort)v);
    public static void W32(Span<byte> d, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d[o..], v);
    public static void W64(Span<byte> d, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(d[o..], v);

    public static void Put16(Stream s, int v) { Span<byte> b = stackalloc byte[2]; W16(b, 0, v); s.Write(b); }
    public static void Put32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; W32(b, 0, v); s.Write(b); }
    public static void Put64(Stream s, ulong v) { Span<byte> b = stackalloc byte[8]; W64(b, 0, v); s.Write(b); }
}
