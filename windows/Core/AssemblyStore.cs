using System.Text;

namespace PKForgeJa.Patcher;

/// <summary>
/// .NET for Android のアセンブリストア（lib/&lt;abi&gt;/libassembly-store.so）。
/// ELF の "payload" セクションに XABA 形式のストアが入っている。
///   header: magic "XABA", version, entry_count, index_entry_count, index_size
///   index : (hash u64|u32, descriptor_index u32, ignore u8) × index_entry_count
///   descriptors: (mapping_index, data_off, data_size, debug_off, debug_size, config_off, config_size) × entry_count
///   names : (u32 長さ + UTF-8) × entry_count
///   data  : 各アセンブリ（"XALZ" ヘッダ付き LZ4 圧縮、または生の PE）
/// </summary>
public sealed class AssemblyStore
{
    private const uint Magic = 0x41424158; // "XABA"
    private readonly byte[] _elf;
    private readonly int _payloadOffset;
    private readonly int _payloadSize;
    private readonly int _payloadSectionHeader;
    private readonly int _descriptorsAt;      // payload 内オフセット
    private readonly int _dataStart;          // payload 内オフセット
    private readonly Dictionary<int, byte[]> _replaced = new();

    public uint Version { get; }
    public List<string> Names { get; } = new();
    private readonly List<uint[]> _descriptors = new();

    public AssemblyStore(byte[] elf)
    {
        _elf = elf;
        var d = elf.AsSpan();
        if (Le.U32(d, 0) != 0x464c457f || d[4] != 2) throw new InvalidDataException("libassembly-store.so が 64bit ELF ではありません");
        long shoff = (long)Le.U64(d, 0x28);
        int shentsize = Le.U16(d, 0x3A), shnum = Le.U16(d, 0x3C), shstrndx = Le.U16(d, 0x3E);
        int strtabOff = (int)Le.U64(d, (int)shoff + shstrndx * shentsize + 0x18);
        _payloadSectionHeader = -1;
        for (int i = 0; i < shnum; i++)
        {
            int sh = (int)shoff + i * shentsize;
            int nameOff = (int)Le.U32(d, sh);
            var name = ReadCString(d, strtabOff + nameOff);
            if (name == "payload")
            {
                _payloadSectionHeader = sh;
                _payloadOffset = (int)Le.U64(d, sh + 0x18);
                _payloadSize = (int)Le.U64(d, sh + 0x20);
            }
        }
        if (_payloadSectionHeader < 0) throw new InvalidDataException("アセンブリストアの payload セクションが見つかりません");

        var p = d.Slice(_payloadOffset, _payloadSize);
        if (Le.U32(p, 0) != Magic) throw new InvalidDataException("アセンブリストアの形式が想定外です (XABA ではない)");
        Version = Le.U32(p, 4);
        int count = (int)Le.U32(p, 8);
        int indexSize = (int)Le.U32(p, 16);
        _descriptorsAt = 20 + indexSize;
        for (int i = 0; i < count; i++)
        {
            var desc = new uint[7];
            for (int k = 0; k < 7; k++) desc[k] = Le.U32(p, _descriptorsAt + i * 28 + k * 4);
            _descriptors.Add(desc);
        }
        int q = _descriptorsAt + count * 28;
        for (int i = 0; i < count; i++)
        {
            int len = (int)Le.U32(p, q);
            Names.Add(Encoding.UTF8.GetString(p.Slice(q + 4, len)));
            q += 4 + len;
        }
        _dataStart = q;
    }

    private static string ReadCString(ReadOnlySpan<byte> d, int at)
    {
        int end = at;
        while (d[end] != 0) end++;
        return Encoding.ASCII.GetString(d[at..end]);
    }

    public int IndexOf(string name)
    {
        for (int i = 0; i < Names.Count; i++)
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>アセンブリ（PE イメージ）を取り出す。</summary>
    public byte[] Get(int index)
    {
        if (_replaced.TryGetValue(index, out var r)) return r;
        var desc = _descriptors[index];
        var blob = _elf.AsSpan(_payloadOffset + (int)desc[1], (int)desc[2]);
        if (blob.Length >= 12 && Le.U32(blob, 0) == 0x5A4C4158) // "XALZ"
        {
            int size = (int)Le.U32(blob, 8);
            return Lz4.Decode(blob[12..], size);
        }
        return blob.ToArray();
    }

    public byte[] Get(string name)
    {
        int i = IndexOf(name);
        if (i < 0) throw new KeyNotFoundException($"アセンブリ {name} が APK にありません");
        return Get(i);
    }

    /// <summary>アセンブリを差し替える（無圧縮で格納。展開用バッファのサイズ制約を受けない）。</summary>
    public void Replace(string name, byte[] image)
    {
        int i = IndexOf(name);
        if (i < 0) throw new KeyNotFoundException(name);
        _replaced[i] = image;
    }

    public bool Modified => _replaced.Count > 0;

    public byte[] Build()
    {
        var src = _elf.AsSpan(_payloadOffset, _payloadSize);
        var payload = new MemoryStream();
        payload.Write(src[.._dataStart]);
        var newDesc = _descriptors.Select(x => (uint[])x.Clone()).ToList();

        // 元のデータ順に並べ直して書く
        var order = Enumerable.Range(0, _descriptors.Count).OrderBy(i => _descriptors[i][1]).ToList();
        foreach (var i in order)
        {
            Align(payload, 16);
            byte[] data = _replaced.TryGetValue(i, out var r) ? r : src.Slice((int)_descriptors[i][1], (int)_descriptors[i][2]).ToArray();
            newDesc[i][1] = (uint)payload.Position;
            newDesc[i][2] = (uint)data.Length;
            payload.Write(data);
            // デバッグ情報・設定データ（通常は無い）も移動
            for (int k = 3; k <= 5; k += 2)
            {
                if (_descriptors[i][k + 1] == 0) continue;
                Align(payload, 16);
                var extra = src.Slice((int)_descriptors[i][k], (int)_descriptors[i][k + 1]);
                newDesc[i][k] = (uint)payload.Position;
                payload.Write(extra);
            }
        }
        var bytes = payload.ToArray();
        for (int i = 0; i < newDesc.Count; i++)
            for (int k = 0; k < 7; k++)
                Le.W32(bytes, _descriptorsAt + i * 28 + k * 4, newDesc[i][k]);

        // ELF を組み直す: [先頭〜payload 開始] [payload] [セクションヘッダ]
        var d = _elf.AsSpan();
        long shoff = (long)Le.U64(d, 0x28);
        int shentsize = Le.U16(d, 0x3A), shnum = Le.U16(d, 0x3C);
        var shdrs = d.Slice((int)shoff, shentsize * shnum).ToArray();
        int payloadSh = _payloadSectionHeader - (int)shoff;
        if (shoff < _payloadOffset + _payloadSize)
            throw new InvalidDataException("想定外の ELF 配置です（セクションヘッダが payload より前）");

        var elf = new MemoryStream();
        elf.Write(d[.._payloadOffset]);
        elf.Write(bytes);
        Align(elf, 16);
        long newShoff = elf.Position;
        Le.W64(shdrs, payloadSh + 0x20, (ulong)bytes.Length);
        elf.Write(shdrs);
        var result = elf.ToArray();
        Le.W64(result, 0x28, (ulong)newShoff);
        return result;
    }

    private static void Align(Stream s, int n)
    {
        while (s.Position % n != 0) s.WriteByte(0);
    }
}
