using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Hizashi;

// 他のブラウザ（Chrome・Edge・Vivaldi・Brave・Opera・Firefox など）から、ブックマークと履歴を取り込む。
// 読むのは、ブックマークのファイルと、履歴のデータベース（コピーしたもの）だけ。
// パスワード・Cookie・保存したカード情報などが入っているファイルには、一切触れない。
static class BrowserImport
{
    public sealed record Source(string Id, string Browser, string Profile, string Dir, bool Firefox, bool HasBookmarks, bool HasHistory, int BookmarkCount);

    // Chromium 系: (表示名, ユーザーデータのフォルダ)。フォルダの中の Default / Profile N がプロファイル
    static IEnumerable<(string name, string dir)> ChromiumRoots(string local) => new[]
    {
        ("Google Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
        ("Google Chrome Beta", Path.Combine(local, "Google", "Chrome Beta", "User Data")),
        ("Google Chrome Canary", Path.Combine(local, "Google", "Chrome SxS", "User Data")),
        ("Microsoft Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
        ("Microsoft Edge Beta", Path.Combine(local, "Microsoft", "Edge Beta", "User Data")),
        ("Microsoft Edge Dev", Path.Combine(local, "Microsoft", "Edge Dev", "User Data")),
        ("Microsoft Edge Canary", Path.Combine(local, "Microsoft", "Edge SxS", "User Data")),
        ("Vivaldi", Path.Combine(local, "Vivaldi", "User Data")),
        ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
        ("Chromium", Path.Combine(local, "Chromium", "User Data")),
        ("Yandex", Path.Combine(local, "Yandex", "YandexBrowser", "User Data")),
        ("Naver Whale", Path.Combine(local, "Naver", "Naver Whale", "User Data")),
    };

    // Opera 系は、フォルダそのものがプロファイル
    static IEnumerable<(string name, string dir)> OperaDirs(string roaming) => new[]
    {
        ("Opera", Path.Combine(roaming, "Opera Software", "Opera Stable")),
        ("Opera GX", Path.Combine(roaming, "Opera Software", "Opera GX Stable")),
    };

    // インストールされているブラウザのプロファイルを探す（local = %LOCALAPPDATA%、roaming = %APPDATA%）
    public static List<Source> Detect(string local, string roaming)
    {
        var list = new List<Source>();
        foreach (var (name, root) in ChromiumRoots(local))
        {
            if (!Directory.Exists(root)) continue;
            var names = ProfileNames(Path.Combine(root, "Local State"));
            foreach (var d in Directory.GetDirectories(root))
            {
                var dn = Path.GetFileName(d);
                if (dn != "Default" && !Regex.IsMatch(dn, @"^Profile \d+$")) continue;
                AddChromium(list, name, names.TryGetValue(dn, out var pn) && !string.IsNullOrWhiteSpace(pn) ? pn : dn, d);
            }
        }
        foreach (var (name, dir) in OperaDirs(roaming))
            if (Directory.Exists(dir)) AddChromium(list, name, "既定", dir);

        var ff = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(ff))
            foreach (var d in Directory.GetDirectories(ff))
            {
                var db = Path.Combine(d, "places.sqlite");
                if (!File.Exists(db)) continue;
                // プロファイルのフォルダ名は「ランダム文字列.default-release」のような形。後ろの部分を名前にする
                var pn = Path.GetFileName(d);
                var dot = pn.IndexOf('.');
                list.Add(new Source("firefox:" + pn, "Firefox", dot >= 0 ? pn[(dot + 1)..] : pn, d, true, true, true, -1));
            }
        return list;
    }

    static void AddChromium(List<Source> list, string browser, string profile, string dir)
    {
        var bm = Path.Combine(dir, "Bookmarks");
        var hasBm = File.Exists(bm);
        var hasHist = File.Exists(Path.Combine(dir, "History"));
        if (!hasBm && !hasHist) return;
        var count = -1;
        if (hasBm) { try { count = ReadChromiumBookmarks(bm).Count; } catch { count = -1; } }
        list.Add(new Source("chromium:" + dir, browser, profile, dir, false, hasBm, hasHist, count));
    }

    // Local State の「profile.info_cache」から、プロファイルの表示名を読む
    static Dictionary<string, string> ProfileNames(string localState)
    {
        var d = new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localState));
            if (doc.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("info_cache", out var ic))
                foreach (var kv in ic.EnumerateObject())
                    if (kv.Value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) d[kv.Name] = n.GetString();
        }
        catch { /* 読めなければ、フォルダ名を使う */ }
        return d;
    }

    static bool IsWeb(string url) => url != null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- ブックマーク
    // Chromium 系（Chrome・Edge・Vivaldi・Brave・Opera など）の「Bookmarks」ファイル
    public static List<Bookmark> ReadChromiumBookmarks(string file)
    {
        var list = new List<Bookmark>();
        using var doc = JsonDocument.Parse(File.ReadAllText(file).TrimStart('﻿'));
        if (!doc.RootElement.TryGetProperty("roots", out var roots)) return list;
        void Walk(JsonElement n)
        {
            if (n.ValueKind != JsonValueKind.Object) return;
            var type = n.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "url")
            {
                var url = n.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (IsWeb(url)) list.Add(new Bookmark { Url = url, Title = n.TryGetProperty("name", out var nm) ? nm.GetString() : null });
            }
            else if (n.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                foreach (var c in ch.EnumerateArray()) Walk(c);
        }
        // ブックマークバー → その他 → 同期したもの、の順
        foreach (var key in new[] { "bookmark_bar", "other", "synced" })
            if (roots.TryGetProperty(key, out var r)) Walk(r);
        return list;
    }

    // ブラウザから書き出した HTML（Netscape 形式。Chrome・Firefox・Safari・Edge・Brave など、ほぼ共通）
    public static List<Bookmark> ReadNetscapeHtml(string html)
    {
        var list = new List<Bookmark>();
        foreach (Match m in Regex.Matches(html ?? "", "<A\\s[^>]*?HREF=\"([^\"]+)\"[^>]*>(.*?)</A>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var url = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (!IsWeb(url)) continue;
            var title = System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, "<[^>]+>", "")).Trim();
            list.Add(new Bookmark { Url = url, Title = title });
        }
        return list;
    }

    // 選んだファイルが何かを見分けて読む（HTML か、Chromium の Bookmarks の JSON）
    public static List<Bookmark> ReadBookmarkFile(string path)
    {
        var text = File.ReadAllText(path).TrimStart('﻿');
        if (text.TrimStart().StartsWith("{")) return ReadChromiumBookmarks(path);
        var l = ReadNetscapeHtml(text);
        if (l.Count == 0) throw new InvalidDataException("ブックマークを読み取れませんでした（HTML か、Chromium 系の Bookmarks ファイルを選んでください）");
        return l;
    }

    // ---------------------------------------------------------------- SQLite（履歴・Firefox のブックマーク）
    // 使用中のブラウザは、データベースを開いたままにしている。そのまま開かず、コピーを作って読む（元のファイルは書き換えない）。
    // コピーもできないとき（ブラウザが排他的に開いている）は、終了してからやり直してもらう
    static string CopyDb(string db, string tmpDir)
    {
        Directory.CreateDirectory(tmpDir);
        var dest = Path.Combine(tmpDir, Path.GetFileName(db));
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var src = db + suffix;
            if (!File.Exists(src)) continue;
            try
            {
                using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var o = File.Create(dest + suffix);
                i.CopyTo(o);
            }
            catch (IOException) when (suffix == "")
            {
                throw new IOException("そのブラウザが履歴のファイルを使用中のため、読み取れませんでした。ブラウザを終了してから、もう一度お試しください。");
            }
            catch (IOException) { /* -wal / -shm が読めなくても、本体だけで続ける */ }
        }
        return dest;
    }

    static List<T> Query<T>(string db, string sql, Func<SqliteDataReader, T> map)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "hizashi-import-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var copy = CopyDb(db, tmp);
            var cs = new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var con = new SqliteConnection(cs);
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            var l = new List<T>();
            while (r.Read()) l.Add(map(r));
            return l;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tmp, true); } catch { /* 一時ファイルが消せなくても無害 */ }
        }
    }

    const long WebKitEpochMs = 11644473600000L; // 1601-01-01 から 1970-01-01 までのミリ秒

    // Chromium 系の履歴（Chrome の「History」）。新しいものから max 件
    public static List<HistoryItem> ReadChromiumHistory(string historyDb, int max)
        => Query(historyDb, $"SELECT url, title, last_visit_time FROM urls WHERE hidden = 0 AND last_visit_time > 0 ORDER BY last_visit_time DESC LIMIT {max}",
                r => new HistoryItem { Url = r.GetString(0), Title = r.IsDBNull(1) ? "" : r.GetString(1), Ts = r.GetInt64(2) / 1000 - WebKitEpochMs })
            .Where(h => IsWeb(h.Url)).ToList();

    public static List<HistoryItem> ReadFirefoxHistory(string placesDb, int max)
        => Query(placesDb, $"SELECT url, title, last_visit_date FROM moz_places WHERE last_visit_date IS NOT NULL ORDER BY last_visit_date DESC LIMIT {max}",
                r => new HistoryItem { Url = r.GetString(0), Title = r.IsDBNull(1) ? "" : r.GetString(1), Ts = r.GetInt64(2) / 1000 })
            .Where(h => IsWeb(h.Url)).ToList();

    public static List<Bookmark> ReadFirefoxBookmarks(string placesDb)
        => Query(placesDb, "SELECT p.url, b.title FROM moz_bookmarks b JOIN moz_places p ON b.fk = p.id WHERE b.type = 1 ORDER BY b.dateAdded",
                r => new Bookmark { Url = r.GetString(0), Title = r.IsDBNull(1) ? null : r.GetString(1) })
            .Where(b => IsWeb(b.Url)).ToList();

    // ---------------------------------------------------------------- 取り込み先へのまとめ
    public static List<Bookmark> ReadBookmarks(Source s)
        => s.Firefox ? ReadFirefoxBookmarks(Path.Combine(s.Dir, "places.sqlite")) : ReadChromiumBookmarks(Path.Combine(s.Dir, "Bookmarks"));

    public static List<HistoryItem> ReadHistory(Source s, int max)
        => s.Firefox ? ReadFirefoxHistory(Path.Combine(s.Dir, "places.sqlite"), max) : ReadChromiumHistory(Path.Combine(s.Dir, "History"), max);

    // ブックマーク: すでにある URL は足さない。足すのは末尾（いまのブックマークの並びは変えない）。足した件数と、重複で省いた件数を返す
    public static (int added, int skipped) MergeBookmarks(List<Bookmark> existing, IEnumerable<Bookmark> incoming)
    {
        var have = existing.Select(b => b.Url).ToHashSet();
        int added = 0, skipped = 0;
        foreach (var b in incoming)
        {
            if (string.IsNullOrEmpty(b.Url) || !have.Add(b.Url)) { skipped++; continue; }
            existing.Add(new Bookmark { Url = b.Url, Title = string.IsNullOrWhiteSpace(b.Title) ? b.Url : b.Title });
            added++;
        }
        return (added, skipped);
    }

    // 履歴: 同じ URL・同じ時刻のものは足さない。時刻順（古い→新しい）に並べ直して、新しい方から max 件だけ残す
    public static int MergeHistory(List<HistoryItem> existing, IEnumerable<HistoryItem> incoming, int max)
    {
        var have = existing.Select(h => (h.Url, h.Ts)).ToHashSet();
        var newOnes = new HashSet<HistoryItem>();
        foreach (var h in incoming)
            if (have.Add((h.Url, h.Ts))) { existing.Add(h); newOnes.Add(h); }
        existing.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        if (existing.Count > max) existing.RemoveRange(0, existing.Count - max);
        return existing.Count(newOnes.Contains); // 上限で押し出されたぶんは数えない
    }
}
