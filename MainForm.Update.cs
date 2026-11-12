using System.Diagnostics;

namespace Hizashi;

// 「更新を確認」: GitHub のリリースを見て、新しい版があれば exe を入れ替えて再起動する
partial class MainForm
{
    bool updating;

    void CheckForUpdate() => Fire(CheckForUpdateAsync);

    async Task CheckForUpdateAsync()
    {
        if (updating) { Toast("更新を処理しています"); return; }
        updating = true;
        string tmp = null;
        try
        {
            Toast("更新を確認しています…");
            using var http = Updater.CreateClient();
            Updater.Release rel;
            try
            {
                rel = await Updater.CheckAsync(http);
            }
            catch (Exception e)
            {
                Program.LogError("更新の確認", e);
                Toast("更新を確認できませんでした（ネットワークか GitHub の状態を確認してください）");
                return;
            }

            var cur = Updater.Current();
            if (!Updater.IsNewer(rel, cur)) { Toast($"最新版です（v{cur.ToString(3)}）"); return; }

            var exe = Environment.ProcessPath ?? "";
            if (!Updater.CanReplace(exe, out var why))
            {
                // 自動では入れ替えられない: ダウンロードのページを案内する
                var open = await AskPrompt($"新しい版 v{rel.Version.ToString(3)} があります",
                    why + "\nダウンロードのページを開きますか？", "", "ページを開く", false) != null;
                if (open) CreateTab(SiteDownloadUrl, new TabOpts { Activate = true });
                return;
            }

            var notes = SummarizeNotes(rel.Notes);
            var ok = await AskPrompt($"新しい版 v{rel.Version.ToString(3)} があります",
                $"いまは v{cur.ToString(3)} です。更新しますか？（ダウンロードして入れ替え、自動で再起動します）" +
                (S.RestoreSession ? "\n開いているタブは、再起動後に復元されます。" : "\n※ 開いているタブは、再起動すると閉じます。") +
                (notes.Length > 0 ? "\n\n" + notes : ""),
                "", "更新する", false) != null;
            if (!ok) return;

            tmp = exe + ".update";
            var last = -10;
            var progress = new Progress<int>(p =>
            {
                if (p >= last + 10 || p == 100) { last = p; Toast($"ダウンロード中… {p}%"); }
            });
            try
            {
                await Updater.DownloadAsync(http, rel, tmp, progress);
                Updater.Swap(tmp, exe);
                tmp = null;
            }
            catch (Exception e)
            {
                Program.LogError("更新", e);
                Toast("更新できませんでした: " + e.Message);
                return;
            }

            Toast("更新しました。再起動します…");
            await Task.Delay(900);
            Process.Start(new ProcessStartInfo(exe, $"--restart-after {Environment.ProcessId}")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
            Close();
        }
        finally
        {
            updating = false;
            if (tmp != null) { try { File.Delete(tmp); } catch { /* 残っても次回の更新で上書きされる */ } }
        }
    }

    // リリースノートの箇条書きだけを、短くまとめて見せる
    static string SummarizeNotes(string body)
    {
        var lines = (body ?? "").Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ") || l.StartsWith("* "))
            .Select(l => "・" + l[2..].Replace("`", "").Replace("**", ""))
            .Take(6)
            .ToList();
        return lines.Count == 0 ? "" : "更新の内容:\n" + string.Join("\n", lines.Select(l => l.Length > 70 ? l[..70] + "…" : l));
    }

    const string SiteDownloadUrl = "https://kaikomziu.github.io/hizashi/download.html";
}
