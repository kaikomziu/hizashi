using System.Text.Json;

namespace Hizashi;

// ポータブルモード: exe と同じフォルダの「Hizashi-data」にデータをすべて保存する。
// exe とこのフォルダを USB メモリなどにまとめて入れれば、どのパソコンでも、同じ設定・ブックマーク・ログイン状態で使える。
// 有効にするには、exe と同じフォルダに「Hizashi.portable」というファイルを置く（アプリの「ポータブルにする」でも作れる）。
static class Portable
{
    public const string MarkerName = "Hizashi.portable";
    public const string DataFolderName = "Hizashi-data";
    const string MigrateName = "migrate.json";

    // ポータブルモードなら、データのルート（exe のフォルダ\Hizashi-data）。そうでなければ null
    public static string Root(string exeDir)
    {
        if (string.IsNullOrEmpty(exeDir)) return null;
        var dir = Path.Combine(exeDir, DataFolderName);
        return File.Exists(Path.Combine(exeDir, MarkerName)) || Directory.Exists(dir) ? dir : null;
    }

    // そのフォルダに書き込めるか（Program Files などでは、ポータブルにできない）
    public static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".hizashi-write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // ポータブルへの切り替えの準備: マーカーを置き、「次回起動時に、いまのデータをコピーする」という指示を書く
    public static void Enable(string exeDir, string oldDataDir, string oldWvDir)
    {
        var root = Path.Combine(exeDir, DataFolderName);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, MigrateName), JsonSerializer.Serialize(new Dictionary<string, string> { ["data"] = oldDataDir, ["webview2"] = oldWvDir }));
        File.WriteAllText(Path.Combine(exeDir, MarkerName), "このファイルがある間、Hizashi は、このフォルダの Hizashi-data にデータを保存します（ポータブルモード）。\r\n");
    }

    // 起動時（WebView2 を使い始める前）に、切り替え前のデータをコピーする。コピー先にすでにあるファイルは上書きしない
    public static bool ApplyMigration(string root)
    {
        var f = Path.Combine(root, MigrateName);
        if (!File.Exists(f)) return false;
        try
        {
            var m = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f)) ?? new();
            if (m.TryGetValue("data", out var d) && Directory.Exists(d)) CopyTree(d, Path.Combine(root, "data"));
            // WebView2 のデータ（Cookie・ログイン状態・拡張機能など）。キャッシュは大きいので持ち越さない
            if (m.TryGetValue("webview2", out var w) && Directory.Exists(w)) CopyTree(w, Path.Combine(root, "webview2"));
        }
        catch (Exception e) { Program.LogError("ポータブルへのデータのコピー", e); }
        try { File.Delete(f); } catch { }
        return true;
    }

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache", "DawnCache", "Crashpad", "component_crx_cache", "extensions_crx_cache", ".work", "ui",
    };

    internal static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            var to = Path.Combine(dst, Path.GetFileName(file));
            if (File.Exists(to)) continue;
            try
            {
                using var i = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var o = File.Create(to);
                i.CopyTo(o);
            }
            catch { /* 使用中などで読めないファイルは、飛ばして続ける */ }
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (SkipDirs.Contains(name) || name.StartsWith(".work")) continue;
            CopyTree(dir, Path.Combine(dst, name));
        }
    }
}
