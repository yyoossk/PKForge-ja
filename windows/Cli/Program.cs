using PKForgeJa.Patcher;

// 使い方: pkforge-ja <本家.apk> <出力.apk> [--same-package] [--no-game-data] [--table ui-table.txt]
if (args.Length < 2)
{
    Console.Error.WriteLine("使い方: pkforge-ja <本家.apk> <出力.apk> [--same-package] [--no-game-data] [--table ファイル]");
    return 2;
}
var options = new PatchOptions
{
    SeparatePackage = !args.Contains("--same-package"),
    GameData = !args.Contains("--no-game-data"),
};
int t = Array.IndexOf(args, "--table");
if (t >= 0 && t + 1 < args.Length) options.UiTable = File.ReadAllText(args[t + 1]);
try
{
    var output = Patcher.Patch(File.ReadAllBytes(args[0]), options, Console.WriteLine, out var result);
    File.WriteAllBytes(args[1], output);
    Console.WriteLine($"パッケージ: {result.Package} / バージョン: {result.Version} / UI 訳 {result.UiEntries} 件 / ゲームデータ {result.DataEntries} 件");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("エラー: " + e);
    return 1;
}
