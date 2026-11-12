using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hizashi;

// Chrome ウェブストアの拡張機能（.crx）を取得して、WebView2 に読み込める「展開済みのフォルダ」にする。
// WebView2 には拡張機能のストアや管理画面が無いので、その部分を Hizashi が受け持つ。
static class ExtensionPackage
{
    static readonly Regex IdRe = new("^[a-p]{32}$", RegexOptions.Compiled);

    public static bool IsStoreHost(string host) =>
        host == "chromewebstore.google.com" || host == "chrome.google.com";

    // Chrome ウェブストアの商品ページか
    public static bool IsStoreUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && IsStoreHost(u.Host) &&
        (u.AbsolutePath.Contains("/detail/") || u.AbsolutePath.Contains("/webstore/detail/"));

    // ストアの URL（…/detail/名前/<ID>）か、32文字の ID から、拡張機能の ID を取り出す。読み取れなければ null
    public static string ParseId(string input)
    {
        var s = (input ?? "").Trim();
        if (IdRe.IsMatch(s)) return s;
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == "https" && IsStoreHost(u.Host))
            foreach (var seg in u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (IdRe.IsMatch(seg)) return seg;
        return null;
    }

    // ブラウザ（Chromium）の版数の先頭部分（"157.0.3500.1" → そのまま、余計な文字は落とす）
    public static string ChromeVersion(string browserVersion)
    {
        var m = Regex.Match(browserVersion ?? "", @"^\d+(\.\d+){1,3}");
        return m.Success ? m.Value : "120.0.0.0";
    }

    public static string CrxUrl(string id, string chromeVersion) =>
        "https://clients2.google.com/service/update2/crx?response=redirect&os=win&arch=x64&os_arch=x86_64&nacl_arch=x86-64" +
        $"&prod=chromecrx&prodchannel=&prodversion={chromeVersion}&lang=ja&acceptformat=crx2,crx3&x=id%3D{id}%26installsource%3Dondemand%26uc";

    // Chrome ウェブストアのページに出す「Hizashi に追加」ボタン（ストア自身の「Chrome に追加」は Chrome の中でしか動かないため）。
    // ページから送るのは「押された」という合図だけ。追加する拡張機能は、送信元のページ URL から本体側で決める
    public const string StoreButtonJs = """
        (() => {
          if (document.getElementById('hizashi-ext-btn')) return;
          const b = document.createElement('button');
          b.id = 'hizashi-ext-btn';
          b.textContent = 'Hizashi に追加';
          b.style.cssText = 'position:fixed;right:20px;bottom:20px;z-index:2147483647;padding:12px 18px;border:0;border-radius:24px;background:#7fbf6a;color:#10200c;font:600 14px system-ui,sans-serif;box-shadow:0 4px 16px rgba(0,0,0,.35);cursor:pointer;display:none';
          b.addEventListener('click', () => window.chrome.webview.postMessage({ c: 'k:ext-store-add' }));
          document.documentElement.appendChild(b);
          const upd = () => { b.style.display = /\/detail\//.test(location.pathname) ? 'block' : 'none'; };
          upd();
          setInterval(upd, 700);
        })()
        """;

    public const int MaxBytes = 150 * 1024 * 1024;

    public static async Task<byte[]> DownloadCrxAsync(HttpClient http, string id, string chromeVersion, string urlOverride = null, CancellationToken ct = default)
    {
        using var res = await http.GetAsync(urlOverride ?? CrxUrl(id, chromeVersion), HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)res.StatusCode == 204 || res.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidDataException("この拡張機能は、ストアから取得できませんでした（公開が終了しているか、ID が違います）");
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength is long len && len > MaxBytes) throw new InvalidDataException("ファイルが大きすぎます");
        using var ms = new MemoryStream();
        using (var src = await res.Content.ReadAsStreamAsync(ct))
        {
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxBytes) throw new InvalidDataException("ファイルが大きすぎます");
            }
        }
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- CRX → zip
    public sealed record Crx(byte[] Zip, byte[] PublicKey);

    // CRX2 / CRX3 のヘッダーを外して、中の zip と、拡張機能の公開鍵（ID の元）を取り出す。普通の zip ならそのまま返す
    public static Crx ParseCrx(byte[] b)
    {
        if (b.Length >= 4 && b[0] == 'P' && b[1] == 'K') return new Crx(b, null);
        if (b.Length < 16 || b[0] != 'C' || b[1] != 'r' || b[2] != '2' || b[3] != '4')
            throw new InvalidDataException("拡張機能のファイル（.crx / .zip）ではありません");
        var ver = BitConverter.ToUInt32(b, 4);
        if (ver == 2)
        {
            var pk = (int)BitConverter.ToUInt32(b, 8);
            var sg = (int)BitConverter.ToUInt32(b, 12);
            var start = 16L + pk + sg;
            if (pk < 0 || sg < 0 || start > b.Length) throw new InvalidDataException("CRX のヘッダーが壊れています");
            return new Crx(b[(int)start..], b[16..(16 + pk)]);
        }
        if (ver == 3)
        {
            var hl = (int)BitConverter.ToUInt32(b, 8);
            var start = 12L + hl;
            if (hl < 0 || start > b.Length) throw new InvalidDataException("CRX のヘッダーが壊れています");
            return new Crx(b[(int)start..], FindCrx3Key(b.AsSpan(12, hl)));
        }
        throw new InvalidDataException("未対応の CRX の版です: " + ver);
    }

    // CRX3 のヘッダー（protobuf）から、拡張機能の公開鍵を探す。
    //   CrxFileHeader { repeated AsymmetricKeyProof sha256_with_rsa = 2; ...ecdsa = 3; bytes signed_header_data = 10000 }
    //   AsymmetricKeyProof { bytes public_key = 1; bytes signature = 2 }   SignedData { bytes crx_id = 1 }
    // crx_id（= 公開鍵の SHA-256 の先頭16バイト）と一致する鍵が、この拡張機能の鍵。見つからなければ null
    static byte[] FindCrx3Key(ReadOnlySpan<byte> header)
    {
        var keys = new List<byte[]>();
        byte[] crxId = null;
        var r = new ProtoReader(header.ToArray());
        while (r.TryReadField(out var field, out var wire))
        {
            if (wire != 2) { r.Skip(wire); continue; }
            var data = r.ReadBytes();
            if (field == 2 || field == 3)
            {
                var inner = new ProtoReader(data);
                while (inner.TryReadField(out var f2, out var w2))
                {
                    if (w2 != 2) { inner.Skip(w2); continue; }
                    var d2 = inner.ReadBytes();
                    if (f2 == 1) keys.Add(d2);
                }
            }
            else if (field == 10000)
            {
                var inner = new ProtoReader(data);
                while (inner.TryReadField(out var f2, out var w2))
                {
                    if (w2 != 2) { inner.Skip(w2); continue; }
                    var d2 = inner.ReadBytes();
                    if (f2 == 1) crxId = d2;
                }
            }
        }
        if (crxId == null) return null;
        foreach (var k in keys)
            if (SHA256.HashData(k).AsSpan(0, 16).SequenceEqual(crxId)) return k;
        return null;
    }

    sealed class ProtoReader
    {
        readonly byte[] b;
        int i;
        public ProtoReader(byte[] bytes) { b = bytes; }

        ulong Varint()
        {
            ulong v = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (i >= b.Length) throw new InvalidDataException("CRX のヘッダーが壊れています");
                var c = b[i++];
                v |= (ulong)(c & 0x7f) << shift;
                if ((c & 0x80) == 0) return v;
            }
            throw new InvalidDataException("CRX のヘッダーが壊れています");
        }

        public bool TryReadField(out int field, out int wire)
        {
            field = wire = 0;
            if (i >= b.Length) return false;
            var tag = Varint();
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            return true;
        }

        public byte[] ReadBytes()
        {
            var n = (int)Varint();
            if (n < 0 || i + n > b.Length) throw new InvalidDataException("CRX のヘッダーが壊れています");
            var r = b[i..(i + n)];
            i += n;
            return r;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: i += 8; break;
                case 2: ReadBytes(); break;
                case 5: i += 4; break;
                default: throw new InvalidDataException("CRX のヘッダーが壊れています");
            }
        }
    }

    // 公開鍵（DER）から、Chrome と同じ方法で拡張機能の ID を作る（SHA-256 の先頭16バイトを a〜p で表す）
    public static string IdFromKey(byte[] publicKey)
    {
        var h = SHA256.HashData(publicKey);
        var sb = new System.Text.StringBuilder(32);
        for (int i = 0; i < 16; i++)
        {
            sb.Append((char)('a' + (h[i] >> 4)));
            sb.Append((char)('a' + (h[i] & 0xf)));
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 展開
    public sealed class Prepared
    {
        public string Dir;          // 展開したフォルダ
        public string Name;         // 表示名（翻訳済み）
        public string Version;
        public int ManifestVersion;
        public string KeyId;        // 公開鍵から求めた ID（鍵が無ければ null）
        public List<string> Permissions = new();   // 日本語の要約
        public bool AllSites;
    }

    // zip を作業用フォルダに展開し、manifest.json を検査する。鍵が分かっていれば manifest に書き足して、
    // フォルダに置いても（WebView2 が）ストアと同じ ID にするようにする
    public static Prepared Prepare(byte[] zip, byte[] publicKey, string workDir)
    {
        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        Directory.CreateDirectory(workDir);
        try
        {
            using (var ms = new MemoryStream(zip))
                ZipFile.ExtractToDirectory(ms, workDir, true); // フォルダの外へ出るパスは例外になる
            return PrepareFolder(workDir, publicKey);
        }
        catch
        {
            try { Directory.Delete(workDir, true); } catch { }
            throw;
        }
    }

    // すでにフォルダになっているもの（展開済みの拡張機能）を検査する
    public static Prepared PrepareFolder(string dir, byte[] publicKey)
    {
        // Chrome が付ける検証用の _metadata フォルダは、読み込みの邪魔になるので消す
        var meta = Path.Combine(dir, "_metadata");
        if (Directory.Exists(meta)) Directory.Delete(meta, true);

        var mpath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(mpath))
        {
            // 1段下のフォルダに入っている zip の場合
            var subs = Directory.GetDirectories(dir);
            if (subs.Length == 1 && File.Exists(Path.Combine(subs[0], "manifest.json")))
                throw new InvalidDataException("manifest.json が見つかりません（フォルダが1段深い可能性があります）");
            throw new InvalidDataException("manifest.json が見つかりません。拡張機能のファイルではないようです");
        }
        var text = File.ReadAllText(mpath).TrimStart('﻿');
        var node = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new InvalidDataException("manifest.json を読み取れません");
        var mv = node["manifest_version"]?.GetValue<int>() ?? 0;
        if (mv != 2 && mv != 3) throw new InvalidDataException("manifest_version が 2 でも 3 でもありません");
        var p = new Prepared { Dir = dir, ManifestVersion = mv, Version = node["version"]?.GetValue<string>() ?? "" };

        string id = null;
        if (publicKey != null && node["key"] == null)
        {
            node["key"] = Convert.ToBase64String(publicKey);
            File.WriteAllText(mpath, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            id = IdFromKey(publicKey);
        }
        else if (node["key"] != null)
        {
            try { id = IdFromKey(Convert.FromBase64String(node["key"]!.GetValue<string>())); } catch { }
        }
        p.KeyId = id;
        p.Name = ResolveMessage(dir, node, node["name"]?.GetValue<string>() ?? "（名前なし）");
        Summarize(node, p);
        return p;
    }

    // "__MSG_appName__" を、_locales の文字列に置き換える
    public static string ResolveMessage(string dir, JsonObject manifest, string s)
    {
        var m = Regex.Match(s ?? "", @"^__MSG_(.+)__$");
        if (!m.Success) return s;
        var def = manifest["default_locale"]?.GetValue<string>();
        foreach (var loc in new[] { "ja", "ja_JP", def, "en" }.Where(x => !string.IsNullOrEmpty(x)))
        {
            var f = Path.Combine(dir, "_locales", loc, "messages.json");
            if (!File.Exists(f)) continue;
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(f).TrimStart('﻿'), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
                if (o == null) continue;
                foreach (var kv in o)
                    if (string.Equals(kv.Key, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                    {
                        var msg = kv.Value?["message"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(msg)) return msg;
                    }
            }
            catch { /* 読めなければ次の言語へ */ }
        }
        return s;
    }

    static readonly Dictionary<string, string> PermLabels = new()
    {
        ["tabs"] = "開いているタブの URL やタイトルを読み取る",
        ["activeTab"] = "操作したタブの内容にアクセスする",
        ["history"] = "閲覧履歴を読み取る・変更する",
        ["bookmarks"] = "ブックマークを読み取る・変更する",
        ["cookies"] = "Cookie を読み取る・変更する",
        ["downloads"] = "ダウンロードを管理する",
        ["webRequest"] = "ページの通信を監視する",
        ["webRequestBlocking"] = "ページの通信をブロック・変更する",
        ["declarativeNetRequest"] = "ページの通信をブロック・変更する",
        ["declarativeNetRequestWithHostAccess"] = "ページの通信をブロック・変更する",
        ["clipboardRead"] = "クリップボードを読み取る",
        ["clipboardWrite"] = "クリップボードに書き込む",
        ["nativeMessaging"] = "このパソコンのアプリと通信する",
        ["management"] = "ほかの拡張機能を管理する",
        ["proxy"] = "通信の経由先（プロキシ）を変更する",
        ["geolocation"] = "現在地を取得する",
        ["scripting"] = "ページにスクリプトを差し込む",
        ["notifications"] = "通知を表示する",
        ["storage"] = "データを保存する",
        ["contextMenus"] = "右クリックメニューに項目を足す",
        ["webNavigation"] = "ページの移動を監視する",
        ["topSites"] = "よく見るサイトの一覧を読み取る",
        ["browsingData"] = "閲覧データを削除する",
        ["debugger"] = "ページをデバッグ操作する（強い権限）",
    };

    static bool IsBroadHost(string pattern) =>
        pattern is "<all_urls>" or "*://*/*" or "http://*/*" or "https://*/*";

    static void Summarize(JsonObject manifest, Prepared p)
    {
        var hosts = new List<string>();
        void AddArr(JsonNode n, List<string> into)
        {
            if (n is JsonArray a) foreach (var x in a) if (x is JsonValue v && v.TryGetValue<string>(out var s)) into.Add(s);
        }
        AddArr(manifest["host_permissions"], hosts);
        if (manifest["content_scripts"] is JsonArray cs)
            foreach (var c in cs) AddArr(c?["matches"], hosts);
        var perms = new List<string>();
        AddArr(manifest["permissions"], perms);
        // manifest v2 は、権限と URL が同じ配列に入っている
        var urlLike = perms.Where(x => x.Contains("://") || x == "<all_urls>").ToList();
        hosts.AddRange(urlLike);
        perms.RemoveAll(x => urlLike.Contains(x));

        if (hosts.Any(IsBroadHost))
        {
            p.AllSites = true;
            p.Permissions.Add("すべてのサイトのページの内容を読み取り・変更する");
        }
        else if (hosts.Count > 0)
        {
            p.Permissions.Add($"{hosts.Distinct().Count()} 件のサイトのページの内容を読み取り・変更する（{string.Join("、", hosts.Distinct().Take(3))}{(hosts.Distinct().Count() > 3 ? " ほか" : "")}）");
        }
        foreach (var x in perms.Distinct())
        {
            var label = PermLabels.TryGetValue(x, out var l) ? l : null;
            if (label == null || label == "データを保存する") continue; // 保存だけの権限は、見せても判断の役に立たないので省く
            if (!p.Permissions.Contains(label)) p.Permissions.Add(label);
        }
    }

    // ポップアップ・設定ページの場所（manifest から。無ければ null）
    public static (string popup, string options) Pages(string dir)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")).TrimStart('﻿'), null,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            if (node == null) return (null, null);
            var popup = node["action"]?["default_popup"]?.GetValue<string>() ?? node["browser_action"]?["default_popup"]?.GetValue<string>();
            var options = node["options_page"]?.GetValue<string>() ?? node["options_ui"]?["page"]?.GetValue<string>();
            return (popup?.TrimStart('/'), options?.TrimStart('/'));
        }
        catch { return (null, null); }
    }
}
