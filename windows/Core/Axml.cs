using System.Text;

namespace PKForgeJa.Patcher;

/// <summary>バイナリ AndroidManifest.xml の文字列プールを書き換える。</summary>
public static class Axml
{
    public static string ReadPackage(byte[] axml) => ReadManifestAttribute(axml, "package")
        ?? throw new InvalidDataException("AndroidManifest.xml にパッケージ名がありません");

    public static string? ReadManifestAttribute(byte[] axml, string attribute)
    {
        var (strings, _, _) = ReadPool(axml);
        var d = axml.AsSpan();
        int p = Le.U16(d, 2);
        while (p < d.Length)
        {
            int type = Le.U16(d, p), hsize = Le.U16(d, p + 2);
            int size = (int)Le.U32(d, p + 4);
            if (type == 0x0102) // START_ELEMENT
            {
                int name = (int)Le.U32(d, p + 20);
                if (strings[name] == "manifest")
                {
                    int attrStart = Le.U16(d, p + 24), attrSize = Le.U16(d, p + 26), attrCount = Le.U16(d, p + 28);
                    for (int i = 0; i < attrCount; i++)
                    {
                        int a = p + 16 + attrStart + i * attrSize;
                        int an = (int)Le.U32(d, a + 4);
                        int raw = (int)Le.U32(d, a + 8);
                        if (strings[an] == attribute && raw != -1) return strings[raw];
                    }
                }
            }
            p += size;
        }
        return null;
    }

    /// <summary>文字列プール内の文字列を map で置き換えた新しい AXML を返す。</summary>
    public static byte[] RewriteStrings(byte[] axml, Func<string, string> map)
    {
        var (strings, poolAt, poolSize) = ReadPool(axml);
        var d = axml.AsSpan();
        int hsize = Le.U16(d, poolAt + 2);
        int stringCount = (int)Le.U32(d, poolAt + 8);
        int styleCount = (int)Le.U32(d, poolAt + 12);
        uint flags = Le.U32(d, poolAt + 16);
        int stringsStart = (int)Le.U32(d, poolAt + 20);
        int stylesStart = (int)Le.U32(d, poolAt + 24);
        bool utf8 = (flags & 0x100) != 0;

        var data = new MemoryStream();
        var offsets = new List<int>();
        foreach (var s0 in strings)
        {
            var s = map(s0);
            offsets.Add((int)data.Length);
            if (utf8)
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                WriteLen8(data, s.Length);
                WriteLen8(data, bytes.Length);
                data.Write(bytes);
                data.WriteByte(0);
            }
            else
            {
                if (s.Length > 0x7FFF)
                {
                    Le.Put16(data, 0x8000 | (s.Length >> 16));
                    Le.Put16(data, s.Length & 0xFFFF);
                }
                else Le.Put16(data, s.Length);
                data.Write(Encoding.Unicode.GetBytes(s));
                Le.Put16(data, 0);
            }
        }
        while (data.Length % 4 != 0) data.WriteByte(0);
        byte[] styles = styleCount > 0
            ? d.Slice(poolAt + stylesStart, poolSize - stylesStart).ToArray()
            : Array.Empty<byte>();

        var pool = new MemoryStream();
        int offsetsSize = (stringCount + styleCount) * 4;
        int newStringsStart = hsize + offsetsSize;
        int newStylesStart = styleCount > 0 ? newStringsStart + (int)data.Length : 0;
        int newSize = newStringsStart + (int)data.Length + styles.Length;
        var header = d.Slice(poolAt, hsize).ToArray();
        Le.W32(header, 4, (uint)newSize);
        Le.W32(header, 20, (uint)newStringsStart);
        Le.W32(header, 24, (uint)newStylesStart);
        pool.Write(header);
        foreach (var o in offsets) Le.Put32(pool, (uint)o);
        for (int i = 0; i < styleCount; i++)
            Le.Put32(pool, Le.U32(d, poolAt + hsize + stringCount * 4 + i * 4));
        pool.Write(data.ToArray());
        pool.Write(styles);

        var outMs = new MemoryStream();
        outMs.Write(d[..poolAt]);
        outMs.Write(pool.ToArray());
        outMs.Write(d[(poolAt + poolSize)..]);
        var result = outMs.ToArray();
        Le.W32(result, 4, (uint)result.Length);
        return result;
    }

    private static void WriteLen8(Stream s, int len)
    {
        if (len > 0x7F)
        {
            s.WriteByte((byte)(0x80 | (len >> 8)));
            s.WriteByte((byte)(len & 0xFF));
        }
        else s.WriteByte((byte)len);
    }

    private static (List<string> strings, int poolAt, int poolSize) ReadPool(byte[] axml)
    {
        var d = axml.AsSpan();
        if (Le.U16(d, 0) != 0x0003) throw new InvalidDataException("バイナリ XML ではありません");
        int p = Le.U16(d, 2);
        if (Le.U16(d, p) != 0x0001) throw new InvalidDataException("文字列プールがありません");
        int size = (int)Le.U32(d, p + 4);
        int hsize = Le.U16(d, p + 2);
        int count = (int)Le.U32(d, p + 8);
        uint flags = Le.U32(d, p + 16);
        int stringsStart = (int)Le.U32(d, p + 20);
        bool utf8 = (flags & 0x100) != 0;
        var list = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int q = p + stringsStart + (int)Le.U32(d, p + hsize + i * 4);
            if (utf8)
            {
                int n = d[q++];
                if ((n & 0x80) != 0) q++;
                int bytes = d[q++];
                if ((bytes & 0x80) != 0) bytes = ((bytes & 0x7F) << 8) | d[q++];
                list.Add(Encoding.UTF8.GetString(d.Slice(q, bytes)));
            }
            else
            {
                int n = Le.U16(d, q);
                q += 2;
                if ((n & 0x8000) != 0) { n = ((n & 0x7FFF) << 16) | Le.U16(d, q); q += 2; }
                list.Add(Encoding.Unicode.GetString(d.Slice(q, n * 2)));
            }
        }
        return (list, p, size);
    }
}
