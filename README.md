# PKForge 日本語版 自動ビルド

[Shurt/PKForge](https://github.com/Shurt/PKForge) を自動で日本語化して APK を作るリポジトリです。
本家が更新されたら、毎日 03:07 (JST) に GitHub Actions が新しいタグを見つけて

1. 本家のソースを取得
2. `ja/strings.json`（翻訳辞書）で画面の英語を日本語に置換
3. ゲームデータ（ポケモン名・わざ・どうぐ・タイプ等）を日本語に切り替え
4. APK をビルドして [Releases](../../releases) に公開

まで全部やります。手作業は不要です。

## インストール

Releases から `PKForge-ja-v*.apk` を入れてください。
アプリ ID は `org.pkforge.shurt.ja` なので、本家版と **並べてインストール** できます（データは別々。バンクは本家版でエクスポート → 日本語版でインポート）。
アプリ内の「アップデートを確認」は、このリポジトリのリリースを見に来ます。

## 本家に新しい文字列が増えたら

辞書にない文字列は英語のまま表示され、ビルドのサマリーと成果物 `untranslated.json` に一覧が出ます。

- **自動で訳す**: リポジトリの Settings → Secrets and variables → Actions に `ANTHROPIC_API_KEY` を登録すると、新しい文字列を Claude API で自動翻訳し、辞書にコミットしてからビルドします。
- **手で訳す**: `ja/strings.json` に `"英語": "日本語"` を追加して push すると、すぐ再ビルドされます。

訳文のルール: `{0}` `{name}` などの記号は残す（語順は入れ替え可）、`"` は使わず「」を使う、`\n` はそのまま。
`python3 tool/pkforge_ja.py check ja/strings.json` で検査できます。

## 手動でビルド

Actions → 「日本語版をビルド」→ Run workflow。
`upstream_ref` に `main` を入れると本家の開発中の最新版を日本語化します。

## 仕組みと安全策

- `tool/cslex.py` が C# の文字列リテラルを正確に切り出し、`tool/pkforge_ja.py` が辞書で置き換えます。補間文字列 `$"..."` は `{0}` 形式のキーに変換して照合します。
- 設定キー・JSON キー・ファイル名・辞書キーとして使われる文字列や、本家同梱データ / PKHeX の英語データと比較される文字列は自動で除外して英語のまま残します（動作を壊さないため）。
- メニューで「表示した文字列をそのまま比較する」箇所は、表示側と比較側を同じ訳にそろえます。前方一致などで比較される項目は、整合が取れない場合だけ英語のまま残します。
- アプリ ID・アプリ名・アップデート確認先・ゲームデータの言語は `ja/config.json` の `code_patches` で変更しています。本家の構造が変わって必須パッチが当たらなくなるとビルドが止まって知らせます。
- 署名鍵は `signing/pkforge-ja.p12`（パスワード `pkforge-ja`）。自分だけの鍵にしたい場合は Secrets に `ANDROID_KEYSTORE_BASE64` / `ANDROID_KEYSTORE_PASSWORD` / `ANDROID_KEY_ALIAS` / `ANDROID_KEY_PASSWORD` を登録してください（鍵を変えると一度アンインストールが必要です）。
- ゲームデータを英語のままにしたい場合は `ja/config.json` の該当パッチに `"enabled": false` を付けます。

本家 PKForge は GPLv3 です。このリポジトリのツールも同じく GPLv3 で配布します。
