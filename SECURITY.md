# セキュリティについて

脆弱性を見つけた場合は、公開の Issue には書かず、GitHub の
[Private vulnerability reporting](https://github.com/kaikomziu/hizashi/security/advisories/new)
から非公開で報告してください。

対応できる範囲で、できるだけ早く確認します。個人開発のため、対応時期の約束はできません。

## 補足

- 表示エンジンは Microsoft Edge WebView2 ランタイムで、Chromium 自体の脆弱性修正は Windows 側のアップデートで配布されます。
- Hizashi は内部ページ（新しいタブなど）にだけ限定した API を公開し、通信の送信元URLも検証しています。外部サイトに Hizashi の内部 API は公開されません。
