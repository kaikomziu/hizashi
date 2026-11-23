namespace Hizashi;

// 他のブラウザからの引き継ぎ（ブックマーク・履歴）。「取り込む」を押したときだけ、そのブラウザのファイルを読む
partial class MainForm
{
    string importReport = "";
    bool importBusy;
    bool firstRun;
    bool importWelcome;   // 初回起動で開いたときだけ、「はじめに」の案内を出す
    const int HistoryMax = 5000;

    // テスト用: 他のブラウザのデータの場所を差し替える
    internal string ImportLocalDir, ImportRoamingDir;

    List<BrowserImport.Source> DetectImportSources() => BrowserImport.Detect(
        ImportLocalDir ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ImportRoamingDir ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    object ImportPanelData() => new
    {
        sources = DetectImportSources().Select(s => new
        {
            id = s.Id, browser = s.Browser, profile = s.Profile, firefox = s.Firefox,
            hasBookmarks = s.HasBookmarks, hasHistory = s.HasHistory, bookmarkCount = s.BookmarkCount,
        }).ToList(),
        report = importReport,
        busy = importBusy,
        welcome = importWelcome,
    };

    // 初めての起動か（設定ファイルが無い）。セルフテストは、いつものテストの邪魔をしないよう対象外
    internal static bool IsFirstRun(string settingsPath, bool selfTest) => !selfTest && !File.Exists(settingsPath);

    // 初回起動のとき、引き継ぎ（取り込み）の画面を開く。いらなければ閉じるだけ。あとから設定でも開ける
    internal async Task ShowFirstRunWelcomeAsync()
    {
        for (int i = 0; i < 50 && !overlayReady; i++) await Task.Delay(100);
        await Task.Delay(500);
        importWelcome = true;
        if (overlayMode == "panel" && panelName == "import") RefreshPanel("import");
        else OpenPanel("import");
    }

    // ブックマーク・履歴を、いまのデータに足す。足した件数を文章にして返す
    string ApplyImport(List<Bookmark> bms, List<HistoryItem> hist, string from)
    {
        var parts = new List<string>();
        if (bms != null)
        {
            var (added, skipped) = BrowserImport.MergeBookmarks(Bookmarks, bms);
            bookmarksStore.Save();
            parts.Add($"ブックマーク {added:N0} 件を追加" + (skipped > 0 ? $"（すでにある {skipped:N0} 件は省きました）" : ""));
            RefreshPanel("bookmarks");
        }
        if (hist != null)
        {
            var added = BrowserImport.MergeHistory(History, hist, HistoryMax);
            historyStore.Save();
            parts.Add($"履歴 {added:N0} 件を追加");
            RefreshPanel("history");
        }
        ScheduleState();
        return $"{from}: " + (parts.Count > 0 ? string.Join("、", parts) : "取り込むものがありませんでした");
    }

    internal async Task ImportRunAsync(string id, bool bookmarks, bool history)
    {
        if (importBusy) { Toast("取り込み中です"); return; }
        importBusy = true;
        importReport = "取り込んでいます…";
        RefreshPanel("import");
        try
        {
            var s = DetectImportSources().FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("そのブラウザのデータが見つかりません");
            List<Bookmark> bms = null;
            List<HistoryItem> hist = null;
            string histError = null;
            await Task.Run(() =>
            {
                if (bookmarks && s.HasBookmarks) bms = BrowserImport.ReadBookmarks(s);
                if (history && s.HasHistory)
                {
                    try { hist = BrowserImport.ReadHistory(s, HistoryMax); }
                    catch (IOException e) { histError = e.Message; } // 使用中で読めない。ブックマークだけでも取り込む
                }
            });
            importReport = ApplyImport(bms, hist, $"{s.Browser}（{s.Profile}）") + (histError != null ? "\n履歴: " + histError : "");
            Toast("取り込みました");
        }
        catch (Exception e)
        {
            Program.LogError("他のブラウザからの取り込み", e);
            importReport = "取り込めませんでした: " + e.Message;
            Toast("取り込めませんでした");
        }
        finally
        {
            importBusy = false;
            RefreshPanel("import");
        }
    }

    // 書き出したブックマークのファイル（HTML）や、Chromium 系の「Bookmarks」ファイルから取り込む
    internal async Task ImportFileAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "ブックマークのファイルを選んでください",
            Filter = "ブックマークのファイル (*.html;*.htm;*.json;Bookmarks)|*.html;*.htm;*.json;Bookmarks|すべてのファイル|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await ImportPathAsync(dlg.FileName);
    }

    internal async Task ImportPathAsync(string path)
    {
        if (importBusy) { Toast("取り込み中です"); return; }
        importBusy = true;
        importReport = "取り込んでいます…";
        RefreshPanel("import");
        try
        {
            var bms = await Task.Run(() => BrowserImport.ReadBookmarkFile(path));
            importReport = ApplyImport(bms, null, Path.GetFileName(path));
            Toast("取り込みました");
        }
        catch (Exception e)
        {
            Program.LogError("ブックマークファイルの取り込み", e);
            importReport = "取り込めませんでした: " + e.Message;
            Toast("取り込めませんでした");
        }
        finally
        {
            importBusy = false;
            RefreshPanel("import");
        }
    }
}
