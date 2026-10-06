using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PKForgeJa.Patcher;

/// <summary>APK Signature Scheme v2 で署名する（RSA PKCS#1 v1.5 + SHA-256, 0x0103）。</summary>
public static class ApkSigner
{
    private const uint V2BlockId = 0x7109871a;
    private const uint AlgRsaPkcs1Sha256 = 0x0103;
    private const int Chunk = 1024 * 1024;

    public static byte[] Sign(byte[] unsigned, X509Certificate2 cert)
    {
        using var rsa = cert.GetRSAPrivateKey() ?? throw new InvalidOperationException("署名鍵が RSA ではありません");
        var d = unsigned.AsSpan();
        int eocd = -1;
        for (int i = d.Length - 22; i >= 0; i--)
            if (Le.U32(d, i) == 0x06054b50) { eocd = i; break; }
        int cdOffset = (int)Le.U32(d, eocd + 16);
        var entries = unsigned.AsMemory(0, cdOffset);
        var cd = unsigned.AsMemory(cdOffset, eocd - cdOffset);
        var eocdBytes = unsigned.AsMemory(eocd);

        var digest = ContentDigest(entries, cd, eocdBytes);

        // signed data
        var digests = Seq(LenPrefixed(Concat(U32(AlgRsaPkcs1Sha256), LenPrefixed(digest))));
        var certs = Seq(LenPrefixed(cert.RawData));
        var attrs = Seq();
        var signedData = Concat(digests, certs, attrs);

        var signature = rsa.SignData(signedData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signatures = Seq(LenPrefixed(Concat(U32(AlgRsaPkcs1Sha256), LenPrefixed(signature))));
        var publicKey = cert.PublicKey.ExportSubjectPublicKeyInfo();
        var signer = Concat(LenPrefixed(signedData), signatures, LenPrefixed(publicKey));
        var v2Value = Seq(LenPrefixed(signer));

        // APK Signing Block
        var pair = Concat(U64((ulong)(4 + v2Value.Length)), U32(V2BlockId), v2Value);
        ulong blockSize = (ulong)(pair.Length + 8 + 16);
        var block = Concat(U64(blockSize), pair, U64(blockSize), "APK Sig Block 42"u8.ToArray());

        var output = new MemoryStream(unsigned.Length + block.Length);
        output.Write(entries.Span);
        output.Write(block);
        output.Write(cd.Span);
        var newEocd = eocdBytes.ToArray();
        Le.W32(newEocd, 16, (uint)(cdOffset + block.Length));
        output.Write(newEocd);
        return output.ToArray();
    }

    private static byte[] ContentDigest(params ReadOnlyMemory<byte>[] sections)
    {
        var chunkDigests = new List<byte[]>();
        foreach (var section in sections)
        {
            for (int off = 0; off < section.Length; off += Chunk)
            {
                int len = Math.Min(Chunk, section.Length - off);
                var prefix = new byte[5];
                prefix[0] = 0xa5;
                Le.W32(prefix, 1, (uint)len);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                sha.AppendData(prefix);
                sha.AppendData(section.Span.Slice(off, len));
                chunkDigests.Add(sha.GetHashAndReset());
            }
        }
        using var top = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var head = new byte[5];
        head[0] = 0x5a;
        Le.W32(head, 1, (uint)chunkDigests.Count);
        top.AppendData(head);
        foreach (var c in chunkDigests) top.AppendData(c);
        return top.GetHashAndReset();
    }

    private static byte[] U32(uint v) { var b = new byte[4]; Le.W32(b, 0, v); return b; }
    private static byte[] U64(ulong v) { var b = new byte[8]; Le.W64(b, 0, v); return b; }
    private static byte[] LenPrefixed(byte[] data) => Concat(U32((uint)data.Length), data);
    private static byte[] Seq(params byte[][] items) => LenPrefixed(Concat(items));

    private static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    /// <summary>同梱の署名鍵、または指定の PKCS#12 を読み込む。</summary>
    public static X509Certificate2 LoadKey(byte[] pkcs12, string password) =>
        X509CertificateLoader.LoadPkcs12(pkcs12, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
}
