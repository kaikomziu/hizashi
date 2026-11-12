using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;

namespace Hizashi;

// 拡張機能（Chrome ウェブストアのもの・手元のファイル/フォルダ）の追加と管理。
// 読み込みは WebView2 の AddBrowserExtensionAsync に任せ、ストアからの取得・展開・管理画面を Hizashi が受け持つ。
partial class MainForm
{
    internal sealed class ExtRecord
    {
        public string Folder { get; set; }
        public string StoreId { get; set; }   // ストアから入れたものだけ
    }

    internal sealed class ExtItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public bool Enabled { get; set; }
        public bool HasPopup { get; set; }
        public bool HasOptions { get; set; }
        public bool FromStore { get; set; }
    }

    Dictionary<string, ExtRecord> extIndex;
    List<ExtItem> extItems = new();
    bool extBusy;
    bool extSyncStarted;

    string ExtDir => Path.Combine(dataDir, "extensions");
    string ExtIndexPath => Path.Combine(dataDir, "extensions.json");

    // いま生きているタブから、拡張機能を入れるプロファイルを得る（UI 用のビューは別プロファイルなので使わない）
    CoreWebView2Profile ExtProfile() => tabs.Select(t => t.Core).FirstOrDefault(c => c != null)?.Profile;

    void LoadExtIndex()
    {
        if (extIndex != null) return;
        try { extIndex = JsonSerializer.Deserialize<Dictionary<string, ExtRecord>>(File.ReadAllText(ExtIndexPath)) ?? new(); }
        catch { extIndex = new(); }
    }

    void SaveExtIndex()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(ExtIndexPath, JsonSerializer.Serialize(extIndex));
        }
        catch (Exception e) { Program.LogError("拡張機能の一覧の保存", e); }
    }

    // 最初のタブの準備ができたら一度だけ呼ぶ: フォルダはあるのにプロファイルから消えているものを読み込み直し、一覧を作る
    void StartExtensionSync()
    {
        if (extSyncStarted) return;
        extSyncStarted = true;
        _ = SafeRun(SyncExtensionsAsync);
    }

    internal async Task SyncExtensionsAsync()
    {
        var profile = ExtProfile();
        if (profile == null) { extSyncStarted = false; return; }
        LoadExtIndex();
        var have = (await profile.GetBrowserExtensionsAsync()).Select(x => x.Id).ToHashSet();
        foreach (var kv in extIndex.ToArray())
        {
            if (have.Contains(kv.Key)) continue;
            var dir = Path.Combine(ExtDir, kv.Value.Folder);
            if (!Directory.Exists(dir)) { extIndex.Remove(kv.Key); continue; }
            try { await profile.AddBrowserExtensionAsync(dir); }
            catch (Exception e) { Program.LogError("拡張機能の再読み込み " + kv.Key, e); }
        }
        SaveExtIndex();
        await RefreshExtItemsAsync();
    }

    internal async Task RefreshExtItemsAsync()
    {
        var profile = ExtProfile();
        if (profile == null) return;
        LoadExtIndex();
        var list = await profile.GetBrowserExtensionsAsync();
        // Edge ランタイムに最初から入っているもの（PDF ビューアーなど）は、触らせないので出さない
        extItems = list.Where(e => extIndex.ContainsKey(e.Id)).Select(e =>
        {
            extIndex.TryGetValue(e.Id, out var rec);
            var dir = rec != null ? Path.Combine(ExtDir, rec.Folder) : null;
            var (popup, options) = dir != null ? ExtensionPackage.Pages(dir) : (null, null);
            string ver = "";
            try
            {
                if (dir != null)
                    ver = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")).TrimStart('﻿'),
                        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                        .RootElement.GetProperty("version").GetString() ?? "";
            }
            catch { }
            return new ExtItem
            {
                Id = e.Id, Name = e.Name, Enabled = e.IsEnabled, Version = ver,
                HasPopup = popup != null, HasOptions = options != null, FromStore = rec?.StoreId != null,
            };
        }).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        RefreshPanel("extensions");
    }

    object ExtensionsPanelData() => new { items = extItems, busy = extBusy };

    // ------------------------------------------------------------------ 追加（Chrome ウェブストア）
    HttpClient CreateStoreClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ExtensionPackage.ChromeVersion(env?.BrowserVersionString)} Safari/537.36");
        return c;
    }

    internal async Task ExtAddFromStoreAsync(string input, bool update = false)
    {
        var id = ExtensionPackage.ParseId(input);
        if (id == null) { Toast("Chrome ウェブストアの拡張機能のページの URL か、32文字の ID を入力してください"); return; }
        if (extBusy) { Toast("別の拡張機能を処理しています"); return; }
        if (ExtProfile() == null) { Toast("ブラウザの準備ができていません。少し待ってからもう一度お試しください"); return; }
        extBusy = true;
        RefreshPanel("extensions");
        var work = Path.Combine(ExtDir, ".work-" + id);
        try
        {
            Toast("ストアから取得しています…");
            Directory.CreateDirectory(ExtDir);
            using var http = CreateStoreClient();
            var crx = await ExtensionPackage.DownloadCrxAsync(http, id, ExtensionPackage.ChromeVersion(env?.BrowserVersionString));
            var parsed = ExtensionPackage.ParseCrx(crx);
            var prep = ExtensionPackage.Prepare(parsed.Zip, parsed.PublicKey, work);
            if (prep.KeyId != null && prep.KeyId != id)
                throw new InvalidDataException("取得した拡張機能の ID が、指定した ID と一致しません（中止しました）");

            LoadExtIndex();
            var oldVer = CurrentVersionOf(id);
            if (update && oldVer != null && oldVer == prep.Version)
            {
                Toast($"「{prep.Name}」は最新版です（v{prep.Version}）");
                return;
            }
            if (!await ConfirmExtension(prep, oldVer, "Chrome ウェブストア")) return;
            await CommitExtensionAsync(prep, $"{id}_{prep.Version}", id);
        }
        catch (Exception e)
        {
            Program.LogError("拡張機能の追加", e);
            Toast("追加できませんでした: " + e.Message);
        }
        finally
        {
            extBusy = false;
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            ReopenExtPanel();
        }
    }

    // 確認画面（オーバーレイ）を出すとパネルが閉じるので、終わったら開き直す
    void ReopenExtPanel()
    {
        if (overlayMode == "panel" && panelName == "extensions") RefreshPanel("extensions");
        else if (overlayMode == null) OpenPanel("extensions");
    }

    string CurrentVersionOf(string storeId)
    {
        var kv = extIndex.FirstOrDefault(x => x.Value.StoreId == storeId);
        if (kv.Key == null) return null;
        return extItems.FirstOrDefault(x => x.Id == kv.Key)?.Version;
    }

    // 何ができる拡張機能かを見せて、追加してよいか確認する
    async Task<bool> ConfirmExtension(ExtensionPackage.Prepared p, string oldVersion, string source)
    {
        var lines = new List<string>
        {
            oldVersion != null ? $"「{p.Name}」を v{oldVersion} から v{p.Version} に更新します。" : $"「{p.Name}」（v{p.Version}）を追加します。",
            $"入手元: {source}",
        };
        lines.Add(p.Permissions.Count > 0 ? "この拡張機能は、次のことができます:" : "特別な権限は求めていません。");
        lines.AddRange(p.Permissions.Take(8).Select(x => "・" + x));
        if (p.AllSites) lines.Add("※ 見ているページの内容（入力した文字を含む）を読み取れます。信頼できるものだけ追加してください。");
        // 確認画面はオーバーレイで出すので、いま開いているパネルは閉じられる。終わったら開き直す
        var ok = await AskPrompt(oldVersion != null ? "拡張機能を更新" : "拡張機能を追加", string.Join("\n", lines), "", oldVersion != null ? "更新する" : "追加する", false) != null;
        return ok;
    }

    // 作業フォルダを正式な場所に移して、WebView2 に読み込ませる
    async Task CommitExtensionAsync(ExtensionPackage.Prepared prep, string folderName, string storeId)
    {
        var profile = ExtProfile() ?? throw new InvalidOperationException("ブラウザの準備ができていません");
        LoadExtIndex();

        // 同じ拡張機能（古い版）があれば、先に外す
        var existing = await profile.GetBrowserExtensionsAsync();
        var oldIds = extIndex.Where(kv => (storeId != null && kv.Value.StoreId == storeId) || kv.Key == prep.KeyId).Select(kv => kv.Key).ToList();
        var oldFolders = new List<string>();
        foreach (var oid in oldIds)
        {
            var ext = existing.FirstOrDefault(x => x.Id == oid);
            if (ext != null) await ext.RemoveAsync();
            oldFolders.Add(extIndex[oid].Folder);
            extIndex.Remove(oid);
        }

        var dest = Path.Combine(ExtDir, folderName);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.Move(prep.Dir, dest);
        CoreWebView2BrowserExtension added;
        try
        {
            added = await profile.AddBrowserExtensionAsync(dest);
        }
        catch
        {
            try { Directory.Delete(dest, true); } catch { }
            throw;
        }
        extIndex[added.Id] = new ExtRecord { Folder = folderName, StoreId = storeId };
        SaveExtIndex();
        foreach (var f in oldFolders.Distinct().Where(f => f != folderName))
            try { Directory.Delete(Path.Combine(ExtDir, f), true); } catch { /* 使用中なら残る（害はない） */ }
        Toast($"「{prep.Name}」を追加しました");
        await RefreshExtItemsAsync();
    }

    // ------------------------------------------------------------------ 追加（手元のファイル・フォルダ）
    internal async Task ExtAddFromFileAsync(bool folder)
    {
        if (extBusy) { Toast("別の拡張機能を処理しています"); return; }
        if (ExtProfile() == null) { Toast("ブラウザの準備ができていません。少し待ってからもう一度お試しください"); return; }
        string path;
        if (folder)
        {
            using var dlg = new FolderBrowserDialog { Description = "展開済みの拡張機能のフォルダ（manifest.json があるフォルダ）を選んでください", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.SelectedPath;
        }
        else
        {
            using var dlg = new OpenFileDialog { Title = "拡張機能のファイルを選んでください", Filter = "拡張機能 (*.crx;*.zip)|*.crx;*.zip|すべてのファイル|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        await ExtAddFromPathAsync(path, folder);
    }

    internal async Task ExtAddFromPathAsync(string path, bool folder, bool confirm = true)
    {
        extBusy = true;
        RefreshPanel("extensions");
        var work = Path.Combine(ExtDir, ".work-local-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(ExtDir);
            ExtensionPackage.Prepared prep;
            string folderName;
            if (folder)
            {
                CopyDirectory(path, work);
                prep = ExtensionPackage.PrepareFolder(work, null);
                folderName = "local-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())))[..12].ToLowerInvariant();
            }
            else
            {
                var bytes = await File.ReadAllBytesAsync(path);
                if (bytes.Length > ExtensionPackage.MaxBytes) throw new InvalidDataException("ファイルが大きすぎます");
                var parsed = ExtensionPackage.ParseCrx(bytes);
                prep = ExtensionPackage.Prepare(parsed.Zip, parsed.PublicKey, work);
                folderName = "local-" + Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
            }
            if (confirm && !await ConfirmExtension(prep, null, folder ? "手元のフォルダ" : "手元のファイル")) return;
            await CommitExtensionAsync(prep, folderName, null);
        }
        catch (Exception e)
        {
            Program.LogError("拡張機能の追加（ファイル）", e);
            Toast("追加できませんでした: " + e.Message);
        }
        finally
        {
            extBusy = false;
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            ReopenExtPanel();
        }
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(d);
            if (name is ".git" or "node_modules") continue;
            CopyDirectory(d, Path.Combine(dst, name));
        }
    }

    // ------------------------------------------------------------------ 有効/無効・削除・更新・ポップアップ
    internal async Task ExtToggleAsync(string id, bool enabled)
    {
        var profile = ExtProfile();
        if (profile == null) return;
        var ext = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == id);
        if (ext == null) return;
        try { await ext.EnableAsync(enabled); }
        catch (Exception e) { Toast("切り替えられませんでした: " + e.Message); }
        await RefreshExtItemsAsync();
    }

    internal async Task ExtRemoveAsync(string id)
    {
        var profile = ExtProfile();
        if (profile == null) return;
        var ext = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == id);
        if (ext == null) return;
        var name = ext.Name;
        if (await AskPrompt("拡張機能を削除", $"「{name}」を削除します。（この拡張機能が保存したデータも消えます）", "", "削除する", false) == null) { ReopenExtPanel(); return; }
        try
        {
            await ext.RemoveAsync();
            LoadExtIndex();
            if (extIndex.TryGetValue(id, out var rec))
            {
                extIndex.Remove(id);
                SaveExtIndex();
                try { Directory.Delete(Path.Combine(ExtDir, rec.Folder), true); } catch { }
            }
            Toast($"「{name}」を削除しました");
        }
        catch (Exception e) { Toast("削除できませんでした: " + e.Message); }
        await RefreshExtItemsAsync();
        ReopenExtPanel();
    }

    internal async Task ExtUpdateAsync(string id)
    {
        LoadExtIndex();
        if (!extIndex.TryGetValue(id, out var rec) || rec.StoreId == null) { Toast("ストアから入れた拡張機能だけ、更新を確認できます"); return; }
        await ExtAddFromStoreAsync(rec.StoreId, true);
    }

    // 拡張機能のポップアップや設定ページを、タブとして開く
    // （WebView2 には拡張機能のツールバー表示が無いので、ページとして開く。タブの情報を使う拡張機能は、動きが違うことがある）
    internal void ExtOpenPage(string id, bool options)
    {
        LoadExtIndex();
        if (!extIndex.TryGetValue(id, out var rec)) return;
        var (popup, opt) = ExtensionPackage.Pages(Path.Combine(ExtDir, rec.Folder));
        var page = options ? opt : popup;
        if (page == null) { Toast("このページはありません"); return; }
        CreateTab($"chrome-extension://{id}/{page}");
    }

    // Chrome ウェブストアのページに差し込んだボタンが押された。ストアのページ（https）からの合図だけを受け付け、
    // 追加する拡張機能は、メッセージの中身ではなく「送信元のページ URL」から決める
    void OnStorePageMessage(CoreWebView2WebMessageReceivedEventArgs e, string src)
    {
        if (!Uri.TryCreate(src, UriKind.Absolute, out var u) || u.Scheme != "https" || !ExtensionPackage.IsStoreHost(u.Host)) return;
        JsonElement root;
        try { root = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone(); } catch { return; }
        if (root.Str("c") != "k:ext-store-add") return;
        Fire(() => ExtAddFromStoreAsync(src));
    }

    // いま開いているのが Chrome ウェブストアのページなら、その拡張機能を追加する
    void ExtAddCurrentPage()
    {
        var url = ActiveTab?.Url ?? "";
        if (!ExtensionPackage.IsStoreUrl(url) || ExtensionPackage.ParseId(url) == null)
        {
            Toast("Chrome ウェブストアの拡張機能のページを開いてから実行してください");
            OpenPanel("extensions");
            return;
        }
        Fire(() => ExtAddFromStoreAsync(url));
    }
}
