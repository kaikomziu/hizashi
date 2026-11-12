using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hizashi;

// GitHub のリリースを見て、新しい版があれば exe を入れ替える。
// 通信は「更新を確認」を押したときだけ。起動時などに自動では何も送らない。
static class Updater
{
    public const string Repo = "kaikomziu/hizashi";
    const string AssetName = "Hizashi.exe";
    const string SumsName = "SHA256SUMS.txt";

    internal sealed record Release(string Tag, Version Version, string Notes, string PageUrl, string ExeUrl, long ExeSize, string Sha256);

    // テスト用: 環境変数で問い合わせ先を差し替える（本物のリリースに触れずに、更新の流れを確かめるため）
    static string TestApi => Environment.GetEnvironmentVariable("HIZASHI_UPDATE_API");
    static string ApiUrl => TestApi ?? $"https://api.github.com/repos/{Repo}/releases/latest";

    // ダウンロードを許す URL（本家リポジトリのリリース添付だけ）
    static bool IsTrustedUrl(string url) =>
        url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal) ||
        (!string.IsNullOrEmpty(TestApi) && url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));

    public static Version Current()
    {
        var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static bool IsNewer(Release r, Version current) => r.Version > current;

    public static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Hizashi/" + Current().ToString(3));
        return c;
    }

    // 最新のリリースの情報を取る（新しいかどうかは IsNewer で判断する）
    public static async Task<Release> CheckAsync(HttpClient http, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver))
            throw new InvalidDataException("リリースの版数を読み取れません: " + tag);
        var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "";

        string exe = null, sums = null;
        long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            var url = a.GetProperty("browser_download_url").GetString();
            if (name == AssetName) { exe = url; size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0; }
            else if (name == SumsName) sums = url;
        }
        if (exe == null) throw new InvalidDataException("このリリースには Hizashi.exe がありません");
        if (!IsTrustedUrl(exe)) throw new InvalidDataException("ダウンロード先が想定外のため中止しました");

        // SHA-256 は、リリース本文の「SHA-256: `…`」か、SHA256SUMS.txt から読む。どちらも無ければ更新しない
        string hash = null;
        var m = Regex.Match(body, @"SHA-256:\s*`?([0-9a-fA-F]{64})`?");
        if (m.Success) hash = m.Groups[1].Value;
        else if (sums != null && IsTrustedUrl(sums))
        {
            var text = await http.GetStringAsync(sums, ct);
            var line = Regex.Match(text, @"([0-9a-fA-F]{64})\s+\*?" + Regex.Escape(AssetName));
            if (line.Success) hash = line.Groups[1].Value;
        }
        if (hash == null) throw new InvalidDataException("SHA-256 が見つからないため、更新できません");
        return new Release(tag, ver, body, page, exe, size, hash.ToLowerInvariant());
    }

    // exe を入れ替えられるか（Program Files のように書き込めない場所や、開発中の実行では不可）
    public static bool CanReplace(string exePath, out string why)
    {
        why = null;
        if (string.IsNullOrEmpty(exePath) || !string.Equals(Path.GetFileName(exePath), AssetName, StringComparison.OrdinalIgnoreCase))
        {
            why = "いま動いているのが Hizashi.exe ではないため（開発用の実行など）、自動では入れ替えられません。";
            return false;
        }
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(exePath)!, ".hizashi-write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch
        {
            why = "この場所（" + Path.GetDirectoryName(exePath) + "）には書き込めないため、自動では入れ替えられません。";
            return false;
        }
        return true;
    }

    // ダウンロードして、SHA-256 を照合する。一致しなければファイルを消して例外にする
    public static async Task DownloadAsync(HttpClient http, Release rel, string dest, IProgress<int> progress = null, CancellationToken ct = default)
    {
        try
        {
            using var res = await http.GetAsync(rel.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? rel.ExeSize;
            using (var src = await res.Content.ReadAsStreamAsync(ct))
            using (var dst = File.Create(dest))
            {
                var buf = new byte[81920];
                long done = 0;
                int n, last = -1;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0)
                    {
                        var p = (int)(done * 100 / total);
                        if (p != last) { last = p; progress?.Report(p); }
                    }
                }
            }

            string actual;
            using (var fs = File.OpenRead(dest)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (actual != rel.Sha256) throw new InvalidDataException("ダウンロードしたファイルの SHA-256 が一致しません（中止しました）");

            using (var fs = File.OpenRead(dest))
                if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new InvalidDataException("実行ファイルではありません");
        }
        catch
        {
            try { File.Delete(dest); } catch { /* 消せなくても元の例外を返す */ }
            throw;
        }
    }

    // 動いている exe は、削除はできないが名前の変更はできる。古い方を .old にして、新しい方を同じ名前にする
    public static void Swap(string newFile, string targetExe)
    {
        var old = targetExe + ".old";
        try { if (File.Exists(old)) File.Delete(old); } catch { /* 前回の残りが消せなくても続ける */ }
        File.Move(targetExe, old, true);
        try
        {
            File.Move(newFile, targetExe);
        }
        catch
        {
            File.Move(old, targetExe, true); // 失敗したら元に戻す
            throw;
        }
    }

    // 更新後の最初の起動で、古い exe の残りを消す
    public static void CleanupOld()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* まだ使われていれば、次回に消す */ }
    }
}
