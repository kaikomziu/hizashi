using System.Diagnostics;

namespace Hizashi;

partial class MainForm
{
    // ポータブルモードで動いているか（データが exe と同じフォルダの Hizashi-data にある）
    internal bool IsPortable { get; private set; }

    string ExeDir => Path.GetDirectoryName(Environment.ProcessPath) ?? appDir;

    // データの保存先を、exe のフォルダに切り替える。いまのデータは、再起動後にコピーされる（元のデータは消さない）
    internal async Task EnablePortableAsync()
    {
        if (IsPortable) { Toast("すでにポータブルモードです"); return; }
        if (SelfTest) return;
        var exeDir = ExeDir;
        if (!Portable.CanWrite(exeDir))
        {
            Toast("この exe のあるフォルダには書き込めないため、ポータブルにできません");
            return;
        }
        var target = Path.Combine(exeDir, Portable.DataFolderName);
        var ok = await AskPrompt("ポータブルモードにする",
            $"データの保存先を、この exe のあるフォルダ内の「{Portable.DataFolderName}」に切り替えます。\n{target}\n\n" +
            "いまの設定・ブックマーク・履歴・ログイン状態をコピーして、再起動します（元のデータは消しません）。\n" +
            "exe とこのフォルダを USB メモリなどにまとめて入れれば、持ち運べます。",
            "", "切り替える", false) != null;
        if (!ok) return;
        try
        {
            Portable.Enable(exeDir, dataDir, wvDir);
        }
        catch (Exception e)
        {
            Program.LogError("ポータブルへの切り替え", e);
            Toast("切り替えられませんでした: " + e.Message);
            return;
        }
        Toast("再起動します…");
        await Task.Delay(900);
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--restart-after {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = exeDir });
        Close();
    }

    internal void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dataDir}\"") { UseShellExecute = true });
        }
        catch { Toast("フォルダを開けませんでした"); }
    }
}
