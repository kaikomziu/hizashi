# hizashi — AI 引き継ぎガイド

## 概要
「Hizashi（日差し）」。Chromium（WebView2）で動く日本語ネイティブの多機能ブラウザ（Windows）。exe 1つで起動。コマンドパレット・ワークスペース・縦タブ・分割・リーダー・要素ブロッカー・集中モード等。**Komorebi の WebView2 版を改名・公開用に整えた後継**。MIT ライセンス。
- GitHub: https://github.com/kaikomziu/hizashi ／ 公式サイト: https://kaikomziu.github.io/hizashi/

## 技術・ビルド
- C# .NET 9 WinForms + Microsoft Edge WebView2。
- `run.bat` … `dist/Hizashi.exe` があれば起動、なければ `dotnet run -c Release`。
- `publish.bat` … 自己完結の単一 exe を `dist/` に生成（.NET 不要版）。
- 自動テスト `SelfTest.cs`（100項目超）。

## 構成
- `MainForm.cs` と partial 分割（`.Tabs` / `.Commands` / `.Ipc` / `.Network` / `.Tools` / `.App`）、`Blocklist.cs`、`PageScripts.cs`（注入 JS）、`Store.cs`、`Util.cs`、`Models.cs`、`Program.cs`。
- `ui/` … ブラウザ UI 本体（HTML/JS）。
- 公式サイト: `site/`（`layout.html` + `src/*.html`）が原本で、そこから `docs/`（GitHub Pages 公開物）を生成する。**`docs/` を直接編集せず `site/` を直して再生成**。
- `CHANGELOG.md`（Keep a Changelog 形式）、`CONTRIBUTING.md`、`SECURITY.md`、`README.en.md`。

## ルール
- リリース時は CHANGELOG・バージョン・公式サイトの changelog/download を更新。
- 宣伝文の下書きは `../hizashi-promo/`。

---
## 共通ルール（このPCの全プロジェクト共通）
- 説明・コメント・コミットメッセージは日本語で書く（変数名・関数名は英語可）。
- 作業フォルダは `C:\Users\4yoma\Desktop\claude` 配下。OneDrive 配下には作らない。
- Netlify / Netlify Blobs を使う場合は `C:\Users\4yoma\Desktop\claude\docs\netlify-blobs-reference.md` を参照。
- MOD 等の author 表記は「かるわか」。GitHub アカウントは `kaikomziu`。
- このファイルは 2026-10-30 時点のコードを読んで自動作成した引き継ぎメモ。食い違いがあればコードを正とする。
