using System.Reflection;
using System.Text;
using dnlib.DotNet;

namespace PKForgeJa.Patcher;

public sealed class PatchOptions
{
    /// <summary>本家版と別アプリ（パッケージ名 + ".ja"）にする。</summary>
    public bool SeparatePackage { get; set; } = true;
    /// <summary>ポケモン名・わざ・どうぐ等も日本語で表示する。</summary>
    public bool GameData { get; set; } = true;
    /// <summary>UI の翻訳テーブル（null なら同梱版）。</summary>
    public string? UiTable { get; set; }
    /// <summary>署名鍵（null なら同梱の鍵）。</summary>
    public byte[]? KeyStore { get; set; }
    public string KeyPassword { get; set; } = "pkforge-ja";
    public string AppLabel { get; set; } = "PKForge 日本語版";
}

public sealed class PatchResult
{
    public string Package = "";
    public string Version = "";
    public int UiEntries, DataEntries;
    public string Hooks = "";
}

public static class Patcher
{
    private const string ChromeAsm = "PKForge.Chrome";
    private const string AppAsm = "PKForge.App";

    public static byte[] Resource(string name)
    {
        using var s = typeof(Patcher).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"内蔵リソース {name} がありません");
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static string BundledUiTable() => Encoding.UTF8.GetString(Resource("ui-table.txt"));

    public static byte[] Patch(byte[] apkBytes, PatchOptions options, Action<string> log, out PatchResult result)
    {
        result = new PatchResult();
        log("APK を読み込み中…");
        var zip = ApkZip.Read(apkBytes);

        var manifestEntry = zip.Find("AndroidManifest.xml") ?? throw new InvalidDataException("AndroidManifest.xml がありません");
        var manifest = manifestEntry.ReadAll();
        var package = Axml.ReadPackage(manifest);
        result.Package = package;
        result.Version = Axml.ReadManifestAttribute(manifest, "versionName") ?? "";
        if (package.EndsWith(".ja", StringComparison.Ordinal))
            throw new InvalidOperationException("この APK はすでに日本語化されています。本家の APK を選んでください。");

        var storeEntries = zip.Entries.Where(e => e.Name.StartsWith("lib/") && e.Name.EndsWith("/libassembly-store.so")).ToList();
        if (storeEntries.Count == 0)
            throw new InvalidDataException("アセンブリストアが見つかりません。PKForge の APK ではないか、形式が変わっています。");

        foreach (var storeEntry in storeEntries)
        {
            var abi = storeEntry.Name.Split('/')[1];
            log($"[{abi}] アセンブリを展開中…");
            var store = new AssemblyStore(storeEntry.ReadAll());
            var il = new IlPatcher(store, log);
            var chrome = il.Load(ChromeAsm);
            var app = il.Load(AppAsm);

            // 1. 翻訳テーブル
            var table = new StringBuilder();
            var ui = options.UiTable ?? BundledUiTable();
            result.UiEntries = ui.Split('\n').Count(l => l.Length > 2);
            table.Append(ui.TrimEnd('\n')).Append('\n');
            if (options.GameData)
            {
                log("ポケモン名・わざ・どうぐなどの日本語データを PKHeX から作成中…");
                result.DataEntries = GameData.Append(il, table);
            }

            // 2. ランタイムの移植
            log("翻訳ランタイムを組み込み中…");
            var movedChrome = il.MoveTypes(Resource("PKForge.Chrome.Runtime.dll"), chrome);
            var movedApp = il.MoveTypes(Resource("PKForgeJa.RuntimeApp.dll"), app);
            chrome.Resources.Add(new EmbeddedResource("PKForgeJa.table.txt",
                Encoding.UTF8.GetBytes(table.ToString()), ManifestResourceAttributes.Public));

            var missing = il.Verify(movedChrome.Concat(movedApp));
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "アプリ内で見つからない API があり、組み込めません（本家の構成が変わった可能性）:\n  " +
                    string.Join("\n  ", missing.Take(30)));

            var jaText = chrome.Find("PKForgeJa.JaText", false) ?? throw new InvalidOperationException("JaText がありません");
            var tChrome = jaText.FindMethod("T");
            var tApp = app.Import(tChrome);
            var binding = app.Find("PKForgeJa.JaBinding", false);

            // 3. 描画呼び出しの置き換え
            log("文字の描画処理に翻訳を差し込み中…");
            var s1 = il.HookModule(chrome, tChrome, null, null);
            var s2 = il.HookModule(app, tApp, binding?.FindMethod("SetBinding"), binding?.FindMethod("SetBindingPath"));
            result.Hooks = $"PKForge.Chrome: {s1}\nPKForge.App: {s2}";
            log(result.Hooks);
            if (s1.Calls + s2.Calls == 0)
                throw new InvalidOperationException("文字を描く処理が見つかりませんでした。本家の構成が大きく変わった可能性があります。");

            log("アセンブリを書き出し中…");
            store.Replace(ChromeAsm + ".dll", il.Write(chrome));
            store.Replace(AppAsm + ".dll", il.Write(app));
            var newStore = store.Build();
            zip.Entries[zip.Entries.IndexOf(storeEntry)] = ApkZip.Entry.Stored(storeEntry.Name, newStore, storeEntry.Time, storeEntry.Date);

            // 書き換えたアセンブリの事前コンパイル済みコード（AOT）は古いので外す → JIT で動く
            foreach (var asm in new[] { ChromeAsm, AppAsm })
            {
                var aot = zip.Find($"lib/{abi}/libaot-{asm}.dll.so");
                if (aot != null) zip.Entries.Remove(aot);
            }
        }

        // 4. マニフェスト（パッケージ名・アプリ名）
        if (options.SeparatePackage || options.AppLabel.Length > 0)
        {
            var newPackage = options.SeparatePackage ? package + ".ja" : package;
            log(options.SeparatePackage ? $"アプリ ID を {newPackage} に変更（本家版と共存）" : "アプリ名を変更");
            var rewritten = Axml.RewriteStrings(manifest, s =>
            {
                if (options.SeparatePackage && (s == package || s.StartsWith(package + ".", StringComparison.Ordinal)))
                    return newPackage + s[package.Length..];
                if (s == "PKForge Personal" || s == "PKForge") return options.AppLabel;
                return s;
            });
            zip.Entries[zip.Entries.IndexOf(manifestEntry)] = ApkZip.Entry.Stored("AndroidManifest.xml", rewritten, manifestEntry.Time, manifestEntry.Date);
            result.Package = newPackage;
        }

        // 5. 古い署名を外して書き出し、署名
        zip.Entries.RemoveAll(e => e.Name.StartsWith("META-INF/", StringComparison.Ordinal) &&
            (e.Name.EndsWith(".SF") || e.Name.EndsWith(".RSA") || e.Name.EndsWith(".DSA") || e.Name.EndsWith(".EC") ||
             e.Name == "META-INF/MANIFEST.MF"));
        log("APK を組み立てて署名中…");
        var unsigned = zip.Write();
        using var cert = ApkSigner.LoadKey(options.KeyStore ?? Resource("pkforge-ja.p12"), options.KeyPassword);
        var signed = ApkSigner.Sign(unsigned, cert);
        log("完了");
        return signed;
    }
}

/// <summary>PKHeX に入っている英語・日本語のゲームテキストから対応表を作る。</summary>
public static class GameData
{
    // 先に書いたものが優先（同じ英語に複数の訳があるときは最初の訳）
    private static readonly string[] Order =
    {
        "other.en.text_Species_", "other.en.text_Moves_", "other.en.text_Abilities_", "other.en.text_Natures_",
        "other.en.text_Types_", "items.text_Items_", "items.gen3.text_ItemsG3_", "items.gen2.text_ItemsG2_",
        "items.gen1.text_ItemsG1_", "other.en.text_Forms_", "other.en.text_Games_", "other.en.text_Ribbons_",
        "other.en.text_Character_", "other.en.text_Wallpaper_", "locations.", "memories.text_Memories_",
    };

    public static int Append(IlPatcher il, StringBuilder table)
    {
        const string asm = "PKHeX.Core";
        const string prefix = "PKHeX.Core.Resources.text.";
        var names = il.ResourceNames(asm).Where(n => n.StartsWith(prefix) && n.EndsWith("_en.txt")).ToList();
        var seen = new HashSet<string>();
        int count = 0;
        foreach (var group in Order)
        {
            foreach (var en in names.Where(n => n[prefix.Length..].StartsWith(group)).OrderBy(n => n))
            {
                var ja = en.Replace(".en.", ".ja.").Replace("_en.txt", "_ja.txt");
                var enBytes = il.ReadResource(asm, en);
                var jaBytes = il.ReadResource(asm, ja);
                if (enBytes == null || jaBytes == null) continue;
                var enLines = Lines(enBytes);
                var jaLines = Lines(jaBytes);
                for (int i = 0; i < Math.Min(enLines.Length, jaLines.Length); i++)
                {
                    var e = enLines[i].Trim();
                    var j = jaLines[i].Trim();
                    if (e.Length < 2 || j.Length == 0 || e == j || !e.Any(char.IsLetter)) continue;
                    if (e.Contains('\t') || j.Contains('\t') || e.Contains('{')) continue;
                    if (!seen.Add(e)) continue;
                    table.Append("E\t").Append(Escape(e)).Append('\t').Append(Escape(j)).Append('\n');
                    count++;
                }
            }
        }
        return count;
    }

    private static string[] Lines(byte[] data) =>
        Encoding.UTF8.GetString(data).Replace("\r", "").Split('\n');

    private static string Escape(string s) => s.Replace("\\", "\\\\");
}
