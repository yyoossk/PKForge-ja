# PKForge 日本語化パッチャー

[Shurt/PKForge](https://github.com/Shurt/PKForge)（Android 用ポケモンセーブエディタ）の**配布 APK をそのまま日本語化する Windows ソフト**です。
本家が更新されても、新しい APK を読み込ませるだけで日本語版が作れます。ビルド環境は要りません。

## 使い方

1. [Releases](../../releases) から `PKForgeJaPatcher.exe` をダウンロードして起動（インストール不要）
2. 「最新版をダウンロード」を押す（または手元の本家 APK を選ぶ／ドラッグ＆ドロップ）
3. 「日本語化して保存…」を押す → `PKForge-vX.Y.Z-ja.apk` ができる
4. その APK を端末に入れる

- 初期設定では本家版と**別アプリ**（`org.pkforge.shurt.ja`）になるので、両方並べてインストールできます。データ（バンク等）は別々なので、本家版でエクスポート → 日本語版でインポートしてください。
- ポケモン名・わざ・どうぐ・とくせい・せいかく・タイプ・地名なども日本語で表示されます（PKHeX に入っている公式の日本語名を使用）。

## しくみ

APK の中の .NET アセンブリを直接書き換えます。

- **表示の直前だけ翻訳**: 文字を描く・測る・表示する処理（SkiaSharp の DrawText / MeasureText、MAUI の Label.Text など）に翻訳を差し込みます。アプリ内部の比較・保存データ・設定キーは英語のままなので、動作やセーブデータに影響しません。
- **翻訳辞書**: `ja/strings.json`（画面の文字列 約 4,200 件）を `windows/Core/ui-table.txt` に変換して使います。`{0}` を含む文（例「Found {0} Pokémon saves in {1}.」）も、表示された文字列から数字や名前を取り出して日本語の語順に組み直します。
- **辞書の自動更新**: パッチャーは起動時にこのリポジトリの最新の辞書を取りに来ます。本家に新しい文字列が増えると、毎日の自動処理（`.github/workflows/dictionary.yml`）が見つけて一覧にします。Secrets に `ANTHROPIC_API_KEY` を入れておけば自動で訳して辞書に追加します。
- **再署名**: APK Signature Scheme v2 で署名し直します（同梱の鍵 `signing/pkforge-ja.p12`）。

## 開発

```
python3 tool/build_table.py                 # 辞書 → 実行時テーブル
dotnet build windows/Cli/Cli.csproj -c Release
dotnet windows/Cli/bin/Release/net10.0/pkforge-ja.dll 本家.apk 出力.apk
```

GitHub Actions（`patcher.yml`）が本家の最新 APK に実際にパッチを当て、apksigner で署名を検証し、Android エミュレーターで起動テストしてから exe を公開します。

本家 PKForge は GPLv3 です。このリポジトリも GPLv3 で配布します。
