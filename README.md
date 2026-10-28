<p align="center">
  <img src="docs/logo.png" width="120" alt="Hizashi">
</p>

<h1 align="center">Hizashi（日差し）</h1>

<p align="center">
  Chromium で動く、日本語ネイティブの多機能ブラウザ（Windows）<br>
  <b>exe を1つダブルクリックするだけ</b>で起動します。
</p>

<p align="center">
  <a href="https://github.com/kaikomziu/hizashi/releases/latest"><img alt="Download" src="https://img.shields.io/github/v/release/kaikomziu/hizashi?label=download&color=4f8a54"></a>
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-blue"></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078d4">
  <img alt=".NET 9 + WebView2" src="https://img.shields.io/badge/.NET%209-WebView2-512bd4">
</p>

<p align="center">
  <a href="README.en.md">English</a> ・ <a href="https://kaikomziu.github.io/hizashi/">紹介ページ</a>
</p>

![分割表示とリーダーモード](docs/screenshots/split-reader.png)

## ダウンロード

1. [**Releases**](https://github.com/kaikomziu/hizashi/releases/latest) から `Hizashi.exe` をダウンロード
2. ダブルクリックで起動（インストール不要。.NET も別途入れる必要はありません）

> **「Windows によって PC が保護されました」と出たら**: 署名のない個人開発のアプリのため表示されます。「詳細情報」→「実行」で起動できます。
> 気になる場合は、ソースコードをそのまま読めますし、自分でビルドもできます（[ビルド方法](#ビルド)）。リリースには SHA-256 も載せています。

必要なもの: Windows 10 / 11 と **WebView2 ランタイム**（Windows 11 には最初から入っています。入っていなければ、起動時に案内が出ます）。

起動したら `Ctrl+K` を押してみてください。ほとんどの操作は、コマンドパレットから探せます。

## 特長

| | |
|---|---|
| **コマンドパレット** `Ctrl+K` | タブ・ブックマーク・履歴・全コマンドをあいまい検索。`>`コマンド `@`タブ `*`ブックマーク `?`履歴 で絞り込み。**計算**もできます（`1+2*3` → 7） |
| **ワークスペース** | 「仕事」「趣味」など色分けしたタブのグループ。`Alt+1〜9` で切り替え |
| **縦タブ** `Ctrl+Shift+L` | 横タブ ⇔ 縦タブ。サイドバーは折りたためます。ドラッグで並べ替え |
| **分割表示** `Alt+S` | 2つのタブを左右に並べます。リンクの右クリックから「横に並べて開く」も |
| **集中モード** `Ctrl+Shift+F` | 時間を決めて、気が散るサイト（YouTube・X など。編集可）をブロック |
| **リーダーモード** `F9` | 本文だけを抽出。文字サイズ・テーマ（白/セピア/黒）・読了時間つき |
| **要素ブロッカー** `Ctrl+Shift+X` | 邪魔な要素をクリックで消す。サイトごとに記憶され、次回も消えたまま |
| **広告・トラッカーブロック** | 主要な広告/計測ドメインへの通信を遮断。ブロック数を表示。サイトごとに ON/OFF |
| **HTTPS 自動アップグレード** | `http://` を `https://` に。開けないサイトは自動でHTTPに戻します |
| **追跡パラメータ除去** | `utm_*` `fbclid` `gclid` などをURLから自動で取り除きます |
| **強制ダークモード** `Ctrl+Shift+D` | サイトごと（または全サイト）。設定は記憶されます |
| **タブの自動休止** | 使っていないタブを休止してメモリを解放。再起動時も「休止状態」で復元 |
| **スクリーンショット** | 表示範囲 / **ページ全体**（縦長ページも1枚）。保存＋クリップボード |
| **クイックメモ** `Ctrl+Shift+N` | ページ上の文字を選択 → 右クリック →「選択範囲をメモに追加」（URL付き） |
| **検索ショートカット** | アドレスバーで `yt ねこ` `gh electron` `map 東京駅` `w 日差し` など |
| ほか | ページ内検索、ピクチャーインピクチャー、モバイル表示、ページの日本語翻訳、タブのミュート/ピン留め、閉じたタブの復元、ダウンロード管理、権限（カメラ等）の確認と記憶、新しいタブページ（時計・TODO・集中モード） |

<p align="center">
  <img src="docs/screenshots/palette.png" width="48%" alt="コマンドパレット">
  <img src="docs/screenshots/side-workspaces-dark.png" width="48%" alt="縦タブとワークスペース">
</p>

## ショートカット（主なもの）

| 操作 | キー |
|---|---|
| 新しいタブ / 閉じる / 閉じたタブを開く | `Ctrl+T` / `Ctrl+W` / `Ctrl+Shift+T` |
| タブ切替 | `Ctrl+Tab` / `Ctrl+Shift+Tab` / `Ctrl+1〜9` |
| ワークスペース切替 | `Alt+1〜9` |
| 戻る・進む・再読み込み | `Alt+←` `Alt+→` `Ctrl+R`（`F5`） |
| アドレスバー / パレット / ページ内検索 | `Ctrl+L` / `Ctrl+K` / `Ctrl+F` |
| ブックマーク | `Ctrl+D`（パネル `Ctrl+Shift+O`） |
| 履歴 / ダウンロード / 設定 | `Ctrl+H` / `Ctrl+J` / `Ctrl+,` |
| 印刷 / 開発者ツール / 全画面 | `Ctrl+P` / `F12` / `F11` |
| ヘルプ（全コマンド一覧） | `F1` |

## プライバシー

- Hizashi 自身は、**テレメトリ（利用状況の送信）を一切行いません**。アカウント登録もありません。
- 履歴・ブックマーク・メモ・設定は、すべてあなたのPC内（`%APPDATA%\Hizashi`）に保存されます。Cookie などは `%LOCALAPPDATA%\Hizashi\WebView2` です。
- 表示エンジンの WebView2 ランタイムは Microsoft 製で、診断データは Windows の設定に従います（Hizashi からは変更していません）。
- 広告ブロックは**ドメイン単位**です。YouTube の動画広告など、サイト自身が配信する広告は防げません。

## 制限事項

- Chrome ウェブストアの**拡張機能は使えません**。
- Google アカウントでのログインは、ブラウザの名乗りを Chrome に揃えていますが、「安全でないブラウザ」と判定される可能性があります（検証中）。
- シークレットウィンドウ、アカウント間の同期は未実装です。
- Windows 専用です。

## ビルド

.NET 9 SDK が必要です。

```bat
git clone https://github.com/kaikomziu/hizashi.git
cd hizashi
run.bat          :: ビルドして起動
publish.bat      :: dist\Hizashi.exe（ランタイム同梱・単一exe）を作る
```

## 仕組み

Windows 標準の Microsoft Edge WebView2（Chromium）を C# / WinForms で包んだブラウザです。ウィンドウの中には、奥から **外枠UI → 各タブ → オーバーレイ（パレットなど）** の順に WebView2 を重ねています。外枠・パレット・新しいタブページは HTML/CSS/JS で、exe に埋め込まれています。

```
Program.cs / MainForm*.cs   ウィンドウ・タブ・コマンド・ネットワーク制御・メッセージ処理
PageScripts.cs              ページに注入する JS/CSS（リーダー・要素ブロッカー・PiP・ダーク）
Blocklist.cs / Util.cs / Store.cs / Models.cs
ui/                         外枠・オーバーレイ・新しいタブ・集中モード画面（HTML/CSS/JS）
SelfTest.cs                 自動テスト
```

## 自動テスト

実際にウィンドウを開いて機能を一通り動かします。一時プロファイルで動くので、普段のデータには触れません。外部接続（example.com など）を使う項目があり、テスト中はウィンドウを最小化しないでください。

```bat
set HIZASHI_OUT=C:\temp\hizashi-out
dist\Hizashi.exe --selftest main
set HIZASHI_KEEP=1
dist\Hizashi.exe --selftest restore
```

## コントリビュート

バグ報告・要望は [Issues](https://github.com/kaikomziu/hizashi/issues) へ。詳しくは [CONTRIBUTING.md](CONTRIBUTING.md) を見てください。

## ライセンス

[MIT](LICENSE)
