using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using PKForgeJa.Patcher;

namespace PKForgeJa.Gui;

public sealed class MainForm : Form
{
    private const string UpstreamRepo = "Shurt/PKForge";
    private const string DictionaryUrl = "https://raw.githubusercontent.com/yyoossk/PKForge-ja/main/windows/Core/ui-table.txt";

    private readonly TextBox _apkPath = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    private readonly Button _browse = new() { Text = "参照…", AutoSize = true };
    private readonly Button _download = new() { Text = "最新版をダウンロード", AutoSize = true };
    private readonly CheckBox _separate = new() { Text = "本家版とは別のアプリにする（両方インストールできる・おすすめ）", Checked = true, AutoSize = true };
    private readonly CheckBox _gameData = new() { Text = "ポケモン名・わざ・どうぐ・とくせいなども日本語で表示する", Checked = true, AutoSize = true };
    private readonly CheckBox _onlineDict = new() { Text = "最新の翻訳辞書をネットから取得する", Checked = true, AutoSize = true };
    private readonly Button _run = new() { Text = "日本語化して保存…", Height = 44, Dock = DockStyle.Fill };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Dock = DockStyle.Fill, Height = 8 };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };

    private static readonly HttpClient Http = CreateHttp();

    public MainForm()
    {
        Text = "PKForge 日本語化パッチャー";
        Font = new Font("Yu Gothic UI", 10f);
        MinimumSize = new Size(640, 480);
        Size = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AllowDrop = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "本家 PKForge の APK（ドラッグ＆ドロップでも指定できます）", AutoSize = true, Margin = new Padding(0, 0, 0, 4) });

        var row = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Dock = DockStyle.Top, AutoSize = true };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _apkPath.Dock = DockStyle.Fill;
        row.Controls.Add(_apkPath, 0, 0);
        row.Controls.Add(_browse, 1, 0);
        row.Controls.Add(_download, 2, 0);
        layout.Controls.Add(row);

        var opts = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 8) };
        opts.Controls.Add(_separate);
        opts.Controls.Add(_gameData);
        opts.Controls.Add(_onlineDict);
        layout.Controls.Add(opts);

        layout.Controls.Add(new Label
        {
            Text = "※ 別アプリにした場合、データ（バンク等）は本家版と別になります。本家版でバンクをエクスポートし、日本語版でインポートしてください。",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = SystemColors.GrayText,
        });
        layout.Controls.Add(_run);
        layout.Controls.Add(_progress);
        layout.Controls.Add(_log);
        Controls.Add(layout);

        _browse.Click += (_, _) => Browse();
        _download.Click += async (_, _) => await DownloadLatestAsync();
        _run.Click += async (_, _) => await RunAsync();
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) _apkPath.Text = files[0];
        };

        Log("本家の APK を選ぶか「最新版をダウンロード」を押してから、「日本語化して保存…」を押してください。");
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PKForgeJaPatcher", "1.0"));
        return http;
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog { Filter = "APK ファイル (*.apk)|*.apk|すべてのファイル|*.*", Title = "本家 PKForge の APK を選択" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _apkPath.Text = dlg.FileName;
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }
        _log.AppendText(message.Replace("\n", Environment.NewLine) + Environment.NewLine);
    }

    private void Busy(bool busy)
    {
        _run.Enabled = _download.Enabled = _browse.Enabled = !busy;
        _progress.MarqueeAnimationSpeed = busy ? 30 : 0;
        UseWaitCursor = busy;
    }

    private async Task DownloadLatestAsync()
    {
        Busy(true);
        try
        {
            Log("本家の最新リリースを確認中…");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{UpstreamRepo}/releases/latest"));
            var tag = doc.RootElement.GetProperty("tag_name").GetString();
            var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString()?.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) == true);
            if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("最新リリースに APK がありません");
            var name = asset.GetProperty("name").GetString()!;
            var url = asset.GetProperty("browser_download_url").GetString()!;
            var dir = Path.Combine(AppContext.BaseDirectory, "downloads");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            Log($"{tag} をダウンロード中: {name}");
            await using (var stream = await Http.GetStreamAsync(url))
            await using (var file = File.Create(path))
                await stream.CopyToAsync(file);
            _apkPath.Text = path;
            Log("ダウンロードしました: " + path);
        }
        catch (Exception e)
        {
            Log("ダウンロードに失敗しました: " + e.Message);
        }
        finally
        {
            Busy(false);
        }
    }

    private async Task RunAsync()
    {
        var input = _apkPath.Text.Trim().Trim('"');
        if (!File.Exists(input))
        {
            MessageBox.Show(this, "本家の APK を指定してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var options = new PatchOptions { SeparatePackage = _separate.Checked, GameData = _gameData.Checked };
        Busy(true);
        try
        {
            if (_onlineDict.Checked)
            {
                try
                {
                    var table = await Http.GetStringAsync(DictionaryUrl);
                    if (table.StartsWith("E\t") || table.StartsWith("T\t"))
                    {
                        options.UiTable = table;
                        Log("最新の翻訳辞書を使います。");
                    }
                }
                catch (Exception e)
                {
                    Log("翻訳辞書を取得できなかったので、内蔵の辞書を使います（" + e.Message + "）");
                }
            }

            PatchResult? result = null;
            var bytes = await Task.Run(() =>
            {
                var r = Patcher.Patcher.Patch(File.ReadAllBytes(input), options, Log, out var res);
                result = res;
                return r;
            });

            var defaultName = $"PKForge-v{result!.Version}-ja.apk";
            using var dlg = new SaveFileDialog
            {
                Filter = "APK ファイル (*.apk)|*.apk",
                FileName = defaultName,
                InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(input)),
                Title = "日本語版 APK の保存先",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                Log("保存をキャンセルしました。");
                return;
            }
            await File.WriteAllBytesAsync(dlg.FileName, bytes);
            Log($"保存しました: {dlg.FileName}");
            Log($"アプリ ID: {result.Package} / 画面の訳 {result.UiEntries} 件 / ゲームデータの訳 {result.DataEntries} 件");
            if (MessageBox.Show(this, "日本語版 APK を保存しました。保存先のフォルダを開きますか？", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
        catch (Exception e)
        {
            Log("失敗しました: " + e.Message);
            MessageBox.Show(this, e.Message, "日本語化に失敗しました", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Busy(false);
        }
    }
}
