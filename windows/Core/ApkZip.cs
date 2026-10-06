using System.IO.Compression;
using System.Text;

namespace PKForgeJa.Patcher;

/// <summary>APK (ZIP) の最小限の読み書き。圧縮データを展開せずにそのままコピーでき、
/// 無圧縮エントリの位置合わせ（zipalign 相当）もできる。</summary>
public sealed class ApkZip
{
    public sealed class Entry
    {
        public string Name = "";
        public ushort Method;          // 0 = 無圧縮, 8 = deflate
        public ushort Time, Date;
        public uint Crc;
        public long CompressedSize, Size;
        public ReadOnlyMemory<byte> Raw; // 圧縮済み（または無圧縮）データ

        public byte[] ReadAll()
        {
            if (Method == 0) return Raw.ToArray();
            if (Method != 8) throw new NotSupportedException($"未対応の圧縮方式 {Method}: {Name}");
            using var input = new MemoryStream(Raw.ToArray());
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var output = new MemoryStream((int)Size);
            deflate.CopyTo(output);
            return output.ToArray();
        }

        public static Entry Stored(string name, byte[] data, ushort time = 0, ushort date = 0x21) => new()
        {
            Name = name,
            Method = 0,
            Time = time,
            Date = date,
            Crc = Crc32.Compute(data),
            CompressedSize = data.Length,
            Size = data.Length,
            Raw = data,
        };
    }

    public List<Entry> Entries { get; } = new();

    public Entry? Find(string name) => Entries.FirstOrDefault(e => e.Name == name);

    public static ApkZip Read(byte[] apk)
    {
        var d = apk.AsSpan();
        int eocd = -1;
        for (int i = d.Length - 22; i >= Math.Max(0, d.Length - 65557); i--)
            if (Le.U32(d, i) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("ZIP の終端が見つかりません（APK ではないか、壊れています）");
        int count = Le.U16(d, eocd + 10);
        int cd = (int)Le.U32(d, eocd + 16);
        var zip = new ApkZip();
        int p = cd;
        for (int i = 0; i < count; i++)
        {
            if (Le.U32(d, p) != 0x02014b50) throw new InvalidDataException("ZIP のディレクトリが壊れています");
            var e = new Entry
            {
                Method = Le.U16(d, p + 10),
                Time = Le.U16(d, p + 12),
                Date = Le.U16(d, p + 14),
                Crc = Le.U32(d, p + 16),
                CompressedSize = Le.U32(d, p + 20),
                Size = Le.U32(d, p + 24),
            };
            int nameLen = Le.U16(d, p + 28), extraLen = Le.U16(d, p + 30), commentLen = Le.U16(d, p + 32);
            int local = (int)Le.U32(d, p + 42);
            e.Name = Encoding.UTF8.GetString(d.Slice(p + 46, nameLen));
            p += 46 + nameLen + extraLen + commentLen;
            if (Le.U32(d, local) != 0x04034b50) throw new InvalidDataException($"ZIP エントリが壊れています: {e.Name}");
            int dataStart = local + 30 + Le.U16(d, local + 26) + Le.U16(d, local + 28);
            e.Raw = apk.AsMemory(dataStart, (int)e.CompressedSize);
            zip.Entries.Add(e);
        }
        return zip;
    }

    /// <summary>署名なしの APK を書き出す。無圧縮エントリは 4 バイト、.so は 16KB 境界に揃える。</summary>
    public byte[] Write()
    {
        var ms = new MemoryStream();
        var offsets = new List<long>();
        foreach (var e in Entries)
        {
            var name = Encoding.UTF8.GetBytes(e.Name);
            long headerAt = ms.Position;
            offsets.Add(headerAt);
            int extra = 0;
            if (e.Method == 0)
            {
                int align = e.Name.EndsWith(".so", StringComparison.Ordinal) ? 16384 : 4;
                long dataAt = headerAt + 30 + name.Length;
                // 0xD935 (apksigner と同じ「位置合わせ用」拡張フィールド): id, size, alignment, padding…
                int pad = (int)((align - (dataAt + 6) % align) % align);
                extra = 6 + pad;
            }
            Le.Put32(ms, 0x04034b50);
            Le.Put16(ms, e.Method == 0 ? 10 : 20);
            Le.Put16(ms, 0x0800);                 // UTF-8 ファイル名
            Le.Put16(ms, e.Method);
            Le.Put16(ms, e.Time);
            Le.Put16(ms, e.Date);
            Le.Put32(ms, e.Crc);
            Le.Put32(ms, (uint)e.CompressedSize);
            Le.Put32(ms, (uint)e.Size);
            Le.Put16(ms, name.Length);
            Le.Put16(ms, extra);
            ms.Write(name);
            if (extra > 0)
            {
                Le.Put16(ms, 0xD935);
                Le.Put16(ms, extra - 4);
                Le.Put16(ms, e.Name.EndsWith(".so", StringComparison.Ordinal) ? 16384 : 4);
                ms.Write(new byte[extra - 6]);
            }
            ms.Write(e.Raw.Span);
        }
        long cdStart = ms.Position;
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            var name = Encoding.UTF8.GetBytes(e.Name);
            Le.Put32(ms, 0x02014b50);
            Le.Put16(ms, 0x0314);
            Le.Put16(ms, e.Method == 0 ? 10 : 20);
            Le.Put16(ms, 0x0800);
            Le.Put16(ms, e.Method);
            Le.Put16(ms, e.Time);
            Le.Put16(ms, e.Date);
            Le.Put32(ms, e.Crc);
            Le.Put32(ms, (uint)e.CompressedSize);
            Le.Put32(ms, (uint)e.Size);
            Le.Put16(ms, name.Length);
            Le.Put16(ms, 0);
            Le.Put16(ms, 0);
            Le.Put16(ms, 0);
            Le.Put16(ms, 0);
            Le.Put32(ms, 0);
            Le.Put32(ms, (uint)offsets[i]);
            ms.Write(name);
        }
        long cdEnd = ms.Position;
        Le.Put32(ms, 0x06054b50);
        Le.Put16(ms, 0);
        Le.Put16(ms, 0);
        Le.Put16(ms, Entries.Count);
        Le.Put16(ms, Entries.Count);
        Le.Put32(ms, (uint)(cdEnd - cdStart));
        Le.Put32(ms, (uint)cdStart);
        Le.Put16(ms, 0);
        return ms.ToArray();
    }
}
