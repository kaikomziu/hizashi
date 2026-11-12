using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Hizashi;

// 動作確認用セルフテスト。  Hizashi.exe --selftest main      （通常テスト）
//                          HIZASHI_KEEP=1 Hizashi.exe --selftest restore （セッション復元テスト）
// 出力先は環境変数 HIZASHI_OUT（結果とスクリーンショット）。一時プロファイルで動くので普段のデータには触れない。
public partial class MainForm
{
    const string Article = "<html><head><title>テスト記事</title></head><body><nav>メニュー<a href=\"#\">リンク</a></nav>" +
        "<div id=\"promo\" style=\"height:60px;background:#fc0;margin:10px\">広告バナー（消す対象）</div>" +
        "<article><h1>日差しのなかで</h1>%P%</article><footer>フッター</footer>" +
        "<img src=\"https://ad.doubleclick.net/pixel.gif\"><img src=\"https://www.google-analytics.com/collect.gif\"></body></html>";

    // スクリーンショット撮影用の、読み物ふうのサンプルページ（文章は自作）
    const string DemoPage = @"<!doctype html><html lang='ja'><head><meta charset='utf-8'><title>ブラウザの片隅に、小さな工夫を</title>
<style>
body{margin:0;font-family:'Yu Gothic UI','Hiragino Sans',sans-serif;color:#2a2a2a;background:#fbfaf7}
nav{display:flex;gap:22px;align-items:center;padding:16px 56px;white-space:nowrap;overflow:hidden;background:#fff;border-bottom:1px solid #e8e4da;font-size:14px;color:#6b6659}
nav b{color:#2f5d3a;font-size:18px;margin-right:12px}
.hero{height:150px;background:linear-gradient(120deg,#1f4a36,#4f8a54 55%,#d9c46a);}
main{max-width:720px;margin:-40px auto 60px;padding:0 24px}
.card{background:#fff;border-radius:14px;padding:40px 44px;box-shadow:0 6px 28px rgba(0,0,0,.08)}
h1{font-size:30px;line-height:1.4;margin:0 0 8px}
.meta{color:#8a8574;font-size:13px;margin-bottom:28px}
p{line-height:1.95;font-size:16px;margin:0 0 1.3em}
#promo{margin:26px 0;padding:22px;border-radius:10px;background:#ffe9a3;text-align:center;color:#7a5b00;font-size:14px}
footer{text-align:center;color:#a39e8f;font-size:13px;padding:30px}
</style></head><body>
<nav><b>暮らしのメモ帳</b><span>特集</span><span>コラム</span><span>お知らせ</span></nav>
<div class='hero'></div>
<main><div class='card'>
<article>
<h1>ブラウザの片隅に、小さな工夫を</h1>
<div class='meta'>2026年10月 ・ コラム</div>
<p>毎日いちばん長く向き合うアプリは、案外ブラウザかもしれません。仕事の資料、調べもの、動画、買い物。タブは増え続け、気づけば画面の上にずらりと並んでいます。</p>
<div id='promo'>広告スペース — このバナーは「要素ブロッカー」でクリックひとつで消せます</div>
<p>そこで試したいのが、用途ごとにタブを分けるという考え方です。仕事用、勉強用、趣味用。切り替えるたびに、いま必要なタブだけが目に入ります。</p>
<p>長い文章を読むときは、リーダーモードで本文だけを表示すると目が疲れにくくなります。文字の大きさや背景の色も、その場で好みに合わせられます。</p>
<p>集中したい日は、時間を決めて気が散るサイトをお休みさせる。たったそれだけで、作業の進み方が変わることがあります。</p>
<p>道具は、使う人の暮らしに合わせて少しずつ形を変えていくものです。小さな工夫を重ねて、自分だけの使いやすさを作っていきましょう。</p>
</article></div></main>
<footer>© 暮らしのメモ帳（サンプルページ）</footer>
</body></html>";

    readonly List<(string name, bool ok)> testResults = new();
    StreamWriter testLog;
    string testOut;

    void Check(string name, bool ok, string detail = "")
    {
        testResults.Add((name, ok));
        var line = $"{(ok ? "PASS" : "FAIL")}  {name}{(string.IsNullOrEmpty(detail) ? "" : "  -> " + detail)}";
        testLog?.WriteLine(line);
        Console.WriteLine(line);
    }

    async Task RunSelfTestAsync()
    {
        testOut = Environment.GetEnvironmentVariable("HIZASHI_OUT") ?? Path.Combine(appDir, "selftest-out");
        Directory.CreateDirectory(testOut);
        var which = SelfTestScript ?? "main";
        testLog = new StreamWriter(Path.Combine(testOut, $"selftest-{which}.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        DownloadsDir = testOut;
        PicturesDir = Path.Combine(testOut, "Hizashi");

        // ウォッチドッグ: テストが止まったままにならないよう、制限時間を超えたら結果を残して終了する
        _ = Task.Run(async () =>
        {
            await Task.Delay(which == "mem" ? 120000 : 200000);
            testLog.WriteLine("WATCHDOG: 制限時間を超えたため強制終了します");
            File.WriteAllText(Path.Combine(testOut, $"results-{which}.json"),
                JsonSerializer.Serialize(new { total = testResults.Count, failed = testResults.Count(r => !r.ok) + 1, watchdog = true }));
            Environment.Exit(2);
        });

        // UIとオーバーレイの準備ができるまで待つ
        for (int i = 0; i < 100 && !(uiReady && overlayReady && tabs.Count > 0); i++) await Task.Delay(100);
        var startupMs = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        testLog.WriteLine($"  [info] プロセス開始からUI準備完了まで: {startupMs:N0} ms");
        try
        {
            if (which == "restore") await TestRestore();
            else if (which == "mem") await TestMem();
            else if (which == "shots") await TestShots();
            else await TestMain();
        }
        catch (Exception e)
        {
            Check("テスト中に例外が発生しない", false, e.ToString().Split('\n')[0] + " | " + (e.StackTrace ?? "").Split('\n').FirstOrDefault());
        }
        var failed = testResults.Count(r => !r.ok);
        File.WriteAllText(Path.Combine(testOut, $"results-{which}.json"),
            JsonSerializer.Serialize(new { total = testResults.Count, failed, results = testResults.Select(r => new { r.name, r.ok }) }));
        Check($"== {testResults.Count - failed}/{testResults.Count} PASS ==", failed == 0);
        SaveSession();
        StoreRegistry.FlushAll();
        testLog.Dispose();
        await Task.Delay(300);
        Environment.Exit(failed == 0 ? 0 : 1);
    }

    // ---------------------------------------------------------------- 補助
    async Task Sleep(int ms) => await Task.Delay(ms);

    // 使用中のPCで動かすと、ウィンドウが最小化・最大化されることがある。レイアウト依存のテストの前に標準状態へ戻す
    async Task NormalizeWindow()
    {
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        if (ClientSize.Width != 1360 || ClientSize.Height != 820) ClientSize = new Size(1360, 820);
        Relayout();
        await Sleep(300);
    }

    async Task<bool> WaitLoad(Tab t, int ms = 12000)
    {
        await Sleep(300);
        var t0 = DateTime.UtcNow;
        while ((DateTime.UtcNow - t0).TotalMilliseconds < ms)
        {
            if (t.Core != null && !t.Loading && !string.IsNullOrEmpty(t.Core.Source) && !(t.Core.Source == "about:blank" && t.Url != "about:blank")) return true;
            await Sleep(150);
        }
        return false;
    }

    // 戻ってこない呼び出しでテスト全体が止まらないよう、上限時間を付けて待つ
    static async Task<T> Limit<T>(Task<T> task, int ms = 20000, string what = "スクリプト実行")
    {
        if (await Task.WhenAny(task, Task.Delay(ms)) != task) throw new TimeoutException($"{what}が{ms / 1000}秒以内に戻りませんでした");
        return await task;
    }

    async Task<string> JS(Tab t, string expr, bool awaitPromise = false)
    {
        var v = await Limit(Eval(t.Core, expr, awaitPromise), 20000, "ページ内スクリプト");
        if (v == null) return null;
        var e = v.Value;
        return e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
    }

    async Task<string> UiJs(Microsoft.Web.WebView2.WinForms.WebView2 view, string expr)
    {
        var v = await Limit(Eval(view.CoreWebView2, expr), 20000, "UIスクリプト");
        if (v == null) return null;
        return v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : v.Value.GetRawText();
    }

    async Task Shot(CoreWebView2 core, string name)
    {
        try
        {
            using var fs = File.Create(Path.Combine(testOut, name + ".png"));
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        }
        catch (Exception e) { testLog.WriteLine("撮影失敗 " + name + ": " + e.Message); }
    }

    // ウィンドウ全体（外枠 + タブ + オーバーレイが重なった状態）
    void WinShot(string name)
    {
        try
        {
            using var bmp = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                Native.PrintWindow(Handle, hdc, 2);
                g.ReleaseHdc(hdc);
            }
            bmp.Save(Path.Combine(testOut, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception e) { testLog.WriteLine("ウィンドウ撮影失敗 " + name + ": " + e.Message); }
    }

    async Task ClickAt(CoreWebView2 core, int x, int y)
    {
        async Task M(string type) => await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
            $"{{\"type\":\"{type}\",\"x\":{x},\"y\":{y},\"button\":\"left\",\"clickCount\":1}}");
        await M("mouseMoved");
        await Sleep(200);
        await M("mousePressed");
        await M("mouseReleased");
    }

    HttpListener StartTestServer(out string baseUrl)
    {
        var rnd = new Random();
        for (int i = 0; i < 20; i++)
        {
            var port = rnd.Next(20000, 60000);
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                l.Start();
                baseUrl = $"http://127.0.0.1:{port}";
                _ = Task.Run(async () =>
                {
                    while (l.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await l.GetContextAsync(); } catch { return; }
                        var path = ctx.Request.Url.AbsolutePath;
                        byte[] body;
                        if (path.StartsWith("/article"))
                        {
                            ctx.Response.ContentType = "text/html; charset=utf-8";
                            var paras = string.Concat(Enumerable.Range(1, 6).Select(n =>
                                $"<p>これは第{n}段落です。リーダーモードが本文を正しく抽出できるかを確かめるための、十分に長い文章をここに書いています。日本語の文章でも読了時間の計算ができます。</p>"));
                            body = Encoding.UTF8.GetBytes(Article.Replace("%P%", paras));
                        }
                        else if (path.StartsWith("/tall"))
                        {
                            ctx.Response.ContentType = "text/html; charset=utf-8";
                            body = Encoding.UTF8.GetBytes("<html><body style=\"margin:0\"><div style=\"height:3000px;background:linear-gradient(#e33,#33e)\">縦長ページ</div></body></html>");
                        }
                        else if (path.StartsWith("/demo"))
                        {
                            ctx.Response.ContentType = "text/html; charset=utf-8";
                            body = Encoding.UTF8.GetBytes(DemoPage);
                        }
                        else if (path.StartsWith("/p"))
                        {
                            // 見出しだけが違う簡単なページ（タブの見た目を作る用）
                            ctx.Response.ContentType = "text/html; charset=utf-8";
                            var t = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url.Query)["t"] ?? "ページ";
                            body = Encoding.UTF8.GetBytes($"<html><head><title>{System.Web.HttpUtility.HtmlEncode(t)}</title></head><body style=\"font-family:'Yu Gothic UI',sans-serif;background:#f6f4ef;margin:0\"><div style=\"padding:80px 60px\"><h1 style=\"font-weight:600\">{System.Web.HttpUtility.HtmlEncode(t)}</h1><p style=\"color:#666\">サンプルのページです。</p></div></body></html>");
                        }
                        else if (path.StartsWith("/popup"))
                        {
                            ctx.Response.ContentType = "text/html; charset=utf-8";
                            body = Encoding.UTF8.GetBytes("<html><head><title>ポップアップ</title></head><body>popup</body></html>");
                        }
                        else if (path.StartsWith("/file"))
                        {
                            ctx.Response.ContentType = "application/octet-stream";
                            ctx.Response.AddHeader("Content-Disposition", "attachment; filename=\"hz-test.bin\"");
                            body = new byte[4096];
                        }
                        else { ctx.Response.StatusCode = 404; body = Encoding.UTF8.GetBytes("not found"); }
                        try { ctx.Response.OutputStream.Write(body); ctx.Response.Close(); } catch { }
                    }
                });
                return l;
            }
            catch { l.Close(); }
        }
        throw new InvalidOperationException("テスト用サーバーを起動できません");
    }

    // ---------------------------------------------------------------- 通常テスト
    async Task TestMain()
    {
        var prevClip = "";
        try { prevClip = Clipboard.GetText(); } catch { }
        var server = StartTestServer(out var bs);
        Activate();

        // --- 単一exe化: UIが exe から展開され、ショートカットを作れる ---
        Check("UIが埋め込みリソースから展開されている", File.Exists(Path.Combine(uiDir, "index.html")) && File.Exists(Path.Combine(uiDir, "bridge.js")) && File.Exists(Path.Combine(uiDir, ".ok")), uiDir);
        var scDesk = Path.Combine(testOut, "sc-desktop");
        var scProg = Path.Combine(testOut, "sc-programs");
        Check("ショートカットを作れる", CreateShortcuts(scDesk, scProg) && File.Exists(Path.Combine(scDesk, "Hizashi.lnk")) && File.Exists(Path.Combine(scProg, "Hizashi.lnk")));
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic lnk = sh.CreateShortcut(Path.Combine(scDesk, "Hizashi.lnk"));
            Check("ショートカットのリンク先がこのexe", string.Equals((string)lnk.TargetPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase), (string)lnk.TargetPath);
        }
        catch (Exception e) { Check("ショートカットのリンク先がこのexe", false, e.Message); }
        Check("ウィンドウにアイコンが設定されている", Icon != null);

        // --- 起動直後の状態 ---
        await Sleep(1200);
        Check("起動時にタブが1つできる", tabs.Count == 1, $"tabs={tabs.Count}");
        var first = ActiveTab;
        Check("新しいタブページを表示", first != null && first.Url.StartsWith(NewtabUrl), first?.Url);
        Check("外枠UIの高さが報告されている", uiTop > 60 && uiTop < 200, $"top={uiTop}");
        await Sleep(800);
        await Shot(chrome.CoreWebView2, "01-ui-top");
        await Shot(first.Core, "02-newtab");
        WinShot("01-window");

        // 内部ページのURLはアドレスバーに出さない（空欄になる）。鍵ではなく「ローカル」アイコンになる
        Check("新しいタブではアドレスバーが空欄", await UiJs(chrome, "document.getElementById('address').value") == "");
        Check("新しいタブのアイコンが内部ページ用（「日」タイル）", await UiJs(chrome, "document.querySelector('.tab.active .favicon').textContent") == "日");
        Check("新しいタブのスキームアイコンが内部ページ用", (await UiJs(chrome, "document.getElementById('scheme-icon').className")).Contains("local"));
        Check("新しいタブにショートカットが並ぶ", int.Parse(await JS(first, "document.querySelectorAll('#grid a.sc').length")) >= 4);
        Check("新しいタブにだけ専用APIが公開される", await JS(first, "typeof window.hizashiNewtab") == "object");
        await JS(first, "hizashiNewtab.go('example.com'); 1");
        var ok = await WaitLoad(first, 15000);
        Check("新しいタブの検索から遷移できる", ok && first.Core.Source.StartsWith("https://example.com"), first.Core.Source);
        var leak = await JS(first, "typeof window.hizashiNewtab + '/' + typeof window.hizashi + '/' + typeof window.hizashiFocus");
        Check("外部サイトにはAPIが一切公開されない", leak == "undefined/undefined/undefined", leak);

        // --- ブラウザの名乗り（Chrome として振る舞うか）---
        var ua = await JS(first, "navigator.userAgent");
        Check("UAは Chrome（Edg/ を含まない）", ua.Contains("Chrome/") && !ua.Contains("Edg/"), ua);
        var brands = await JS(first, "JSON.stringify(navigator.userAgentData.brands.map(b => b.brand))");
        Check("クライアントヒントのブランドが Google Chrome", brands.Contains("Google Chrome") && !brands.Contains("Edge"), brands);

        // --- URL解釈 ---
        string U(string s) => Search.ToUrl(s, "google", NewtabUrl);
        Check("「yt ねこ」でYouTube検索", U("yt ねこ").StartsWith("https://www.youtube.com/results?search_query="), U("yt ねこ"));
        Check("ドメインはhttpsで開く", U("example.com") == "https://example.com");
        Check("単語はGoogle検索", U("foo bar").StartsWith("https://www.google.com/search?q="));
        Check("localhostはhttp", U("localhost:3000") == "http://localhost:3000");

        // --- パレット ---
        Check("パレットでコマンド検索", PaletteQuery("分割").Any(i => i.Id == "view.split"));
        var calc = PaletteQuery("1+2*3");
        Check("パレットで計算", calc.Count > 0 && calc[0].Kind == "calc" && calc[0].Value == "7", calc.FirstOrDefault()?.Title);
        Check("@でタブだけ検索", PaletteQuery("@example").All(i => i.Kind == "tab"));
        Activate();
        Native.ForceForeground(Handle);
        await Sleep(300);
        TogglePalette();
        await Sleep(900);
        Check("パレット表示中", overlayMode == "palette");
        await UiJs(overlayView, "(() => { const i = document.getElementById('pal-input'); i.value = 'ダウンロード'; i.dispatchEvent(new Event('input')); return 1; })()");
        await Sleep(700);
        await Shot(overlayView.CoreWebView2, "03-palette");
        Check("パレットの高さが結果に合わせて変わる", paletteHeight < 400, $"h={paletteHeight}");
        await UiJs(overlayView, "document.getElementById('pal-input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))");
        await Sleep(900);
        Check("パレットからコマンドを実行できる（ダウンロードパネルが開く）", overlayMode == "panel" && panelName == "downloads");
        HideOverlay(false);

        // --- 実ページ（HTTP）での機能 ---
        var at = CreateTab(bs + "/article");
        await WaitLoad(at);
        await Sleep(1500);
        Check("広告ドメインへの通信をブロック", at.Blocked >= 2, $"blocked={at.Blocked}");

        RunCommand("tools.reader");
        await Sleep(1800);
        var rtxt = await JS(at, "document.body.innerText");
        Check("リーダーモードに切替", at.Reader);
        Check("本文が抽出される（ナビ・フッターは除く）", rtxt.Contains("第3段落") && !rtxt.Contains("メニュー") && !rtxt.Contains("フッター"), rtxt[..Math.Min(40, rtxt.Length)].Replace("\n", " "));
        Check("読了時間が表示される", System.Text.RegularExpressions.Regex.IsMatch(rtxt, @"約\d+分"));
        await Shot(at.Core, "04-reader");
        RunCommand("tools.reader");
        await WaitLoad(at);
        await Sleep(500);
        Check("リーダーモードを解除（元のページに戻る）", !at.Reader && await JS(at, "!!document.getElementById('promo')") == "true");

        // 要素ブロッカー: 実際にクリックして消す
        RunCommand("tools.picker");
        await Sleep(700);
        var rect = JsonDocument.Parse(await JS(at, "JSON.stringify(document.getElementById('promo').getBoundingClientRect())")).RootElement;
        await ClickAt(at.Core, (int)(rect.Num("x") + rect.Num("width") / 2), (int)(rect.Num("y") + rect.Num("height") / 2));
        await Sleep(1000);
        Sites.Hidden.TryGetValue("127.0.0.1", out var rules);
        Check("要素ブロッカーでセレクタが保存される", rules != null && rules.Count == 1 && rules[0].Contains("promo"), rules == null ? "なし" : string.Join(",", rules));
        Check("クリックした要素が消える", await JS(at, "getComputedStyle(document.getElementById('promo')).display") == "none");
        at.Core.Reload();
        await WaitLoad(at);
        await Sleep(900);
        Check("再読み込み後も非表示が維持される", await JS(at, "getComputedStyle(document.getElementById('promo')).display") == "none");
        RunCommand("tools.unhide");
        await WaitLoad(at);
        await Sleep(700);
        Check("非表示を解除できる", await JS(at, "getComputedStyle(document.getElementById('promo')).display") != "none");

        // 強制ダークモード
        RunCommand("tools.dark");
        await Sleep(800);
        Check("サイト別ダークモードが効く", (await JS(at, "getComputedStyle(document.documentElement).filter")).Contains("invert"));
        await Shot(at.Core, "05-dark");
        at.Core.Reload();
        await WaitLoad(at);
        await Sleep(900);
        Check("ダークモードがサイトごとに記憶される", (await JS(at, "getComputedStyle(document.documentElement).filter")).Contains("invert"));
        RunCommand("tools.dark");
        await Sleep(600);
        Check("ダークモードを解除できる", !(await JS(at, "getComputedStyle(document.documentElement).filter")).Contains("invert"));

        // サイトごとのズーム記憶
        RunCommand("view.zoomIn");
        await Sleep(700);
        Sites.Zoom.TryGetValue("127.0.0.1", out var zv);
        Check("ズームがサイトごとに保存される", Math.Abs(zv - 1.1) < 0.001, $"zoom={zv}");
        at.Core.Reload();
        await WaitLoad(at);
        await Sleep(1000);
        Check("再読み込み後もズームが復元される", Math.Abs(at.View.Ctl().ZoomFactor - 1.1) < 0.001, $"zoom={at.View.Ctl().ZoomFactor}");
        RunCommand("view.zoomReset");
        await Sleep(500);

        // ページ内検索（UIの入力 → ホスト → 件数表示まで通す）
        ActivateTab(at.Id);
        await Sleep(400);
        RunCommand("view.find");
        await Sleep(500);
        await UiJs(chrome, "(() => { const i = document.getElementById('find-input'); i.value = '段落'; i.dispatchEvent(new Event('input')); return 1; })()");
        await Sleep(1500);
        var cnt = await UiJs(chrome, "document.getElementById('find-count').textContent");
        Check("ページ内検索で件数が表示される（1 / 6）", System.Text.RegularExpressions.Regex.IsMatch(cnt ?? "", @"1\s*/\s*6"), cnt);
        await UiJs(chrome, "document.getElementById('find-next').click()");
        await Sleep(700);
        cnt = await UiJs(chrome, "document.getElementById('find-count').textContent");
        Check("「次へ」で2件目に進む", System.Text.RegularExpressions.Regex.IsMatch(cnt ?? "", @"2\s*/\s*6"), cnt);
        await Shot(chrome.CoreWebView2, "05b-find");
        await UiJs(chrome, "document.getElementById('find-close').click()");
        await Sleep(300);

        // スクリーンショット（縦長ページで「ページ全体」が本当に全体か）
        await NormalizeWindow();
        var tall = CreateTab(bs + "/tall");
        await WaitLoad(tall);
        await Sleep(500);
        RunCommand("tools.shot");
        await Sleep(1200);
        RunCommand("tools.shotFull");
        await Sleep(2500);
        var shots = Directory.Exists(PicturesDir) ? Directory.GetFiles(PicturesDir, "*.png").OrderBy(x => x).ToArray() : Array.Empty<string>();
        Check("スクリーンショットが2枚保存される", shots.Length >= 2, $"{shots.Length}枚");
        if (shots.Length >= 2)
        {
            using var vis = Image.FromFile(shots[^2]);
            using var full = Image.FromFile(shots[^1]);
            Check("「ページ全体」は表示範囲より縦に長い", full.Height > vis.Height * 1.5, $"表示範囲={vis.Width}x{vis.Height} 全体={full.Width}x{full.Height}");
        }
        CloseTab(tall.Id);

        // ダウンロード（空タブは自動で閉じる）
        var before = tabs.Count;
        CreateTab(bs + "/file");
        await Sleep(3500);
        var dl = downloads.FirstOrDefault();
        Check("ダウンロードが完了して記録される", dl != null && dl.State == "completed" && File.Exists(dl.Path), dl == null ? "なし" : $"{dl.Name} {dl.State}");
        Check("ダウンロード用の空タブが残らない", tabs.Count == before, $"before={before} after={tabs.Count}");

        // window.open（別タブで開き、opener が保たれる）
        ActivateTab(at.Id);
        await Sleep(400);
        var nBefore = tabs.Count;
        await Eval(at.Core, $"window.__w = window.open('{bs}/popup', '_blank'); 1", false, true);
        await Sleep(2500);
        Check("window.open は新しいタブで開く", tabs.Count == nBefore + 1 && ActiveTab.Url.Contains("/popup"), $"tabs {nBefore}->{tabs.Count} url={ActiveTab?.Url}");
        if (tabs.Count == nBefore + 1)
        {
            var op = await JS(ActiveTab, "String(window.opener !== null)");
            Check("ポップアップから window.opener に届く", op == "true", op);
            CloseTab(ActiveTab.Id);
        }

        // モバイル表示 → 元に戻す
        ActivateTab(at.Id);
        await Sleep(300);
        RunCommand("tools.mobile");
        await Sleep(1800);
        Check("モバイル表示でUAが Android になる", (await JS(at, "navigator.userAgent")).Contains("Android"));
        RunCommand("tools.mobile");
        await Sleep(1800);
        Check("デスクトップ表示に戻せる", (await JS(at, "navigator.userAgent")).Contains("Windows"));

        // --- ネットワーク系（外部接続が必要）---
        var nt = CreateTab("https://example.com/?utm_source=news&fbclid=abc&keep=1");
        if (await WaitLoad(nt, 15000))
            Check("追跡パラメータを自動除去（keepは残る）", nt.Core.Source == "https://example.com/?keep=1", nt.Core.Source);
        var ht = CreateTab("http://example.com/");
        if (await WaitLoad(ht, 15000))
            Check("HTTPをHTTPSへ自動アップグレード", ht.Core.Source.StartsWith("https://"), ht.Core.Source);

        // --- 分割表示 ---
        await NormalizeWindow();
        ActivateTab(nt.Id);
        await Sleep(500);
        RunCommand("view.split");
        await Sleep(900);
        Check("分割表示が有効になる", split != null);
        if (split != null)
        {
            var A = GetTab(split.A); var B = GetTab(split.B);
            Check("2つのビューが左右に並ぶ", A.View.Left < B.View.Left && A.View.Width > 100 && B.View.Width > 100 && A.View.Top == B.View.Top,
                $"{A.View.Bounds} | {B.View.Bounds}");
            Check("両方のビューが表示されている", A.View.Visible && B.View.Visible);
        }
        WinShot("06-window-split");
        RunCommand("view.split");
        await Sleep(400);
        Check("分割表示を解除できる", split == null);

        // --- タブ休止 ---
        ActivateTab(at.Id);
        await Sleep(400);
        Check("バックグラウンドのタブを休止できる", SleepTab(nt) && nt.View == null);
        Check("アクティブなタブは休止されない", !SleepTab(ActiveTab));
        ActivateTab(nt.Id);
        await Sleep(1500);
        Check("休止タブを開くと復帰する", nt.View != null && await WaitLoad(nt, 10000));

        // --- ワークスペース ---
        S.Workspaces.Add(new Workspace { Id = "wtest", Name = "仕事", Color = "#f28b82" });
        var nWs = WsOrdered().Count;
        RunCommand("ws.switch:wtest");
        await Sleep(900);
        Check("ワークスペースを切り替えると別のタブ群になる", currentWs == "wtest" && WsOrdered().Count == 1 && nWs > 1, $"ws={currentWs} tabs={WsOrdered().Count}");
        RunCommand("ws.switch:w1");
        await Sleep(700);
        Check("元のワークスペースに戻る", currentWs == "w1" && WsOrdered().Count == nWs);

        // --- 閉じたタブの復元 ---
        var cur = tabs.Count;
        CloseTab(nt.Id);
        await Sleep(300);
        RunCommand("tab.restore");
        await Sleep(700);
        Check("閉じたタブを復元できる", tabs.Count == cur, $"tabs={tabs.Count}");

        // --- キーボードショートカット ---
        ActivateTab(at.Id);
        await Sleep(500);
        var nTabs = tabs.Count;
        Check("Ctrl+T は割り当て済み", TryShortcut(Keys.T, Keys.Control));
        await Sleep(1200);
        Check("Ctrl+T で新しいタブが開く", tabs.Count == nTabs + 1, $"{nTabs}->{tabs.Count}");
        Check("Ctrl+W は割り当て済み", TryShortcut(Keys.W, Keys.Control));
        await Sleep(900);
        Check("Ctrl+W でタブが閉じる", tabs.Count == nTabs, $"tabs={tabs.Count}");
        Activate();
        Native.ForceForeground(Handle);
        await Sleep(300);
        Check("Ctrl+K は割り当て済み", TryShortcut(Keys.K, Keys.Control));
        await Sleep(900);
        Check("Ctrl+K でパレットが開く", overlayMode == "palette");
        HideOverlay(false);
        Check("Alt+1〜9 / Ctrl+1〜9 の割り当て", HasShortcut(Keys.D1, Keys.Alt) && HasShortcut(Keys.D9, Keys.Control));
        Check("Ctrl+Shift+T / Alt+← / F9 / Ctrl+, / F11 / Ctrl+; も割り当て済み",
            HasShortcut(Keys.T, Keys.Control | Keys.Shift) && HasShortcut(Keys.Left, Keys.Alt) && HasShortcut(Keys.F9, Keys.None) &&
            HasShortcut(Keys.Oemcomma, Keys.Control) && HasShortcut(Keys.F11, Keys.None) && HasShortcut(Keys.Oemplus, Keys.Control | Keys.Shift));
        Check("F5（再読み込み）/ Ctrl+F5 / Ctrl+P（印刷）の割り当て", HasShortcut(Keys.F5, Keys.None) && HasShortcut(Keys.F5, Keys.Control) && HasShortcut(Keys.P, Keys.Control));
        // ウィンドウ側のフォールバック（ProcessCmdKey）: 割り当てのあるキーは受け取り、普通の入力は素通しする
        var nPc = tabs.Count;
        Check("フォールバック: Ctrl+T をウィンドウ側でも処理できる", TestCmdKey(Keys.T | Keys.Control));
        await Sleep(1500);
        Check("フォールバックで新しいタブが開く", tabs.Count == nPc + 1, $"{nPc}->{tabs.Count}");
        Check("フォールバック: Ctrl+W で閉じられる", TestCmdKey(Keys.W | Keys.Control));
        await Sleep(900);
        Check("フォールバックで閉じられる", tabs.Count == nPc, $"tabs={tabs.Count}");
        Check("フォールバックは普通の入力（A / Shift+A / Space / Enter）を横取りしない",
            !TestCmdKey(Keys.A) && !TestCmdKey(Keys.A | Keys.Shift) && !TestCmdKey(Keys.Space) && !TestCmdKey(Keys.Enter) && !TestCmdKey(Keys.Control | Keys.C) && !TestCmdKey(Keys.Control | Keys.V));
        Check("割り当てのないキーは無視する", !HasShortcut(Keys.Q, Keys.Control) && !HasShortcut(Keys.A, Keys.None));
        ActivateTab(at.Id);
        await Sleep(500);

        await NormalizeWindow();
        // 実際のキー入力（OSのキーボード入力 → WebView2 → AcceleratorKeyPressed）。
        // SendKeys は前面のウィンドウに送られるので、Hizashi が前面にあるときだけ行う（別アプリに誤送信しない）
        Activate();
        Native.ForceForeground(Handle);
        await Sleep(500);
        if (Native.GetForegroundWindow() == Handle)
        {
            ActiveTab.View.Focus();
            await Sleep(400);
            var n2 = tabs.Count;
            keyEventsSeen = 0;
            SendKeys.SendWait("^t");
            await Sleep(1500);
            if (keyEventsSeen == 0)
            {
                // OSのキー入力がアプリに一度も届いていない = テスト環境側の問題（前面が変わった等）。アプリの不具合とは区別する
                Check("（スキップ・警告）実キー入力: Hizashi にキーが届かなかったため検証できない", true, "ウィンドウが前面でなくなった可能性");
            }
            else
            {
                var opened = tabs.Count == n2 + 1;
                Check("実キー入力: Ctrl+T で新しいタブが開く", opened, $"{n2}->{tabs.Count} keyEvents={keyEventsSeen}");
                if (opened)
                {
                    await WaitLoad(ActiveTab, 8000); // 新しいタブの準備と読み込みが終わるのを待つ
                    await Sleep(500);
                    keyEventsSeen = 0;
                    SendKeys.SendWait("^w");
                    await Sleep(1000);
                    if (keyEventsSeen == 0) Check("（スキップ・警告）実キー入力: Ctrl+W がHizashiに届かなかったため検証できない", true);
                    else Check("実キー入力: Ctrl+W でタブが閉じる", tabs.Count == n2, $"tabs={tabs.Count} keyEvents={keyEventsSeen}");
                }
            }
        }        else
        {
            Check("（スキップ）実キー入力: Hizashi が前面でないため送信しない", true);
        }

        // --- ブックマーク・履歴 ---
        var nb = Bookmarks.Count;
        ActivateTab(ht.Id);
        await Sleep(400);
        RunCommand("bookmark.toggle");
        await Sleep(300);
        Check("ブックマークを追加できる", Bookmarks.Count == nb + 1, $"{nb}->{Bookmarks.Count}");
        Check("履歴が記録される", History.Count >= 3, $"count={History.Count}");

        // --- アドレスバー候補 ---
        var sug = BuildSuggestions("example");
        Check("候補に検索・タブ・履歴が出る", sug.Count >= 2 && sug[0].Kind == "search", string.Join(",", sug.Select(s => s.Kind)));

        // --- パネル ---
        foreach (var (name, file) in new[] { ("settings", "08-panel-settings"), ("history", "09-panel-history"), ("help", "10-panel-help"), ("downloads", "11-panel-downloads"), ("notes", "11b-panel-notes"), ("bookmarks", "11c-panel-bookmarks") })
        {
            OpenPanel(name);
            await Sleep(900);
            Check($"パネル「{name}」が開く", overlayMode == "panel" && panelName == name);
            await Shot(overlayView.CoreWebView2, file);
            if (name == "settings") WinShot("08-window-panel");
            HideOverlay(false);
        }

        // --- 入力ダイアログ ---
        var pr = AskPrompt("ワークスペースを追加", "名前", "勉強", "作成");
        await Sleep(900);
        Check("入力ダイアログが表示される", overlayMode == "prompt");
        await Shot(overlayView.CoreWebView2, "12-prompt");
        await UiJs(overlayView, "document.getElementById('pr-input').value = 'テスト入力'; document.getElementById('pr-ok').click(); 1");
        Check("入力した文字が返る", await pr == "テスト入力");
        pr = AskPrompt("確認", "x", "", "OK", false);
        await Sleep(600);
        await UiJs(overlayView, "document.getElementById('pr-cancel').click(); 1");
        Check("キャンセルするとnullが返る", await pr == null);

        // --- アドレスバー操作（入力 → 候補 → ↓ → Enter → 遷移）---
        var ot = CreateTab("about:blank");
        await Sleep(800);
        await UiJs(chrome, "(() => { const a = document.getElementById('address'); a.focus(); a.value = 'example.com/?omni=1'; a.dispatchEvent(new Event('input')); return 1; })()");
        await Sleep(800);
        Check("入力すると候補が出る", overlayMode == "suggest");
        await Sleep(300);
        WinShot("07-window-suggest");
        await Shot(overlayView.CoreWebView2, "07-suggest");
        await UiJs(chrome, "document.getElementById('address').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })); 1");
        await Sleep(500);
        var filled = await UiJs(chrome, "document.getElementById('address').value");
        Check("↓で候補を選ぶとアドレスバーに反映される", filled != null && filled.StartsWith("https://example.com"), filled);
        await UiJs(chrome, "document.getElementById('address').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); 1");
        await WaitLoad(ot, 15000);
        Check("Enterで遷移する", ot.Core?.Source == "https://example.com/?omni=1", ot.Core?.Source);
        CloseTab(ot.Id);

        // --- 集中モード ---
        StartFocus(1);
        Check("集中モード中はYouTubeをブロック判定", IsFocusBlocked("https://www.youtube.com/watch?v=1"));
        Check("集中モード中でも他サイトは通る", !IsFocusBlocked("https://example.com/"));
        var ft = CreateTab("https://www.youtube.com/");
        await Sleep(3500);
        Check("集中モード中は遷移が集中画面に差し替わる", (ft.Core?.Source ?? "").Contains("focus.html"), ft.Core?.Source);
        await Shot(ft.Core, "13-focus");
        StopFocus();

        // --- 縦タブレイアウト ---
        await NormalizeWindow();
        ActivateTab(at.Id);
        S.TabLayout = "side";
        ScheduleState();
        await Sleep(1200);
        Check("縦タブでは左にサイドバー分の余白ができる", uiLeft > 150, $"left={uiLeft}");
        WinShot("14-window-side");
        S.SidebarCollapsed = true;
        ScheduleState();
        await Sleep(900);
        Check("サイドバーを折りたためる", uiLeft < 100, $"left={uiLeft}");
        S.TabLayout = "top";
        S.SidebarCollapsed = false;
        ScheduleState();
        await Sleep(800);
        Check("横タブに戻せる", uiLeft == 0);

        // --- 透明度 ---
        S.Opacity = 80;
        ApplyOpacityAndTop();
        await Sleep(500);
        WinShot("15-window-opacity80");
        Check("ウィンドウの透明度を変えられる", Math.Abs(Opacity - 0.8) < 0.01);
        S.Opacity = 100;
        ApplyOpacityAndTop();

        await TestTabScroll(bs);
        await TestUpdater();
        await TestExtensions(bs);

        try { server.Stop(); } catch { }
        try { if (!string.IsNullOrEmpty(prevClip)) Clipboard.SetText(prevClip); } catch { }
    }


    // ---------------------------------------------------------------- タブが増えたとき横にスクロールする
    async Task TestTabScroll(string baseUrl)
    {
        await NormalizeWindow();
        var keep = ActiveTab;
        var added = new List<Tab>();
        for (int i = 1; i <= 24; i++)
            added.Add(CreateTab($"{baseUrl}/p?t=スクロール確認{i}", new TabOpts { Sleeping = true, Activate = false, Title = $"スクロール確認{i}" }));
        await Sleep(1200);
        const string info = "(() => { const s = document.getElementById('tabs-slot-top'); return JSON.stringify({ sw: s.scrollWidth, cw: s.clientWidth, sl: s.scrollLeft, ox: getComputedStyle(s).overflowX }); })()";
        var j = JsonDocument.Parse(await UiJs(chrome, info)).RootElement;
        Check("タブが多いと、横タブが横スクロールになる", j.GetProperty("sw").GetInt32() > j.GetProperty("cw").GetInt32() + 20 && j.GetProperty("ox").GetString() == "auto",
            j.GetRawText());

        // ホイールで左右に動く
        await UiJs(chrome, "document.getElementById('tabs-slot-top').scrollLeft = 0; 1");
        await UiJs(chrome, "document.getElementById('tabs-slot-top').dispatchEvent(new WheelEvent('wheel', { deltaY: 240, bubbles: true, cancelable: true })); 1");
        await Sleep(200);
        var sl = double.Parse(await UiJs(chrome, "document.getElementById('tabs-slot-top').scrollLeft"));
        Check("ホイールでタブを横にスクロールできる", sl > 100, $"scrollLeft={sl}");

        // 最後のタブを選ぶと、そのタブが見える位置まで動く
        ActivateTab(added[^1].Id);
        await Sleep(1500);
        var vis = await UiJs(chrome, "(() => { const s = document.getElementById('tabs-slot-top').getBoundingClientRect(); const a = document.querySelector('.tab.active'); if (!a) return 'none'; const r = a.getBoundingClientRect(); return (r.left >= s.left - 1 && r.right <= s.right + 1) ? 'visible' : 'hidden:' + [r.left, r.right, s.left, s.right].join(','); })()");
        Check("選んだタブが見える位置までスクロールする", vis == "visible", vis);
        WinShot("16-window-many-tabs");

        foreach (var t in added) CloseTab(t.Id, true);
        if (keep != null && tabs.Contains(keep)) ActivateTab(keep.Id);
        await Sleep(600);
    }

    // ---------------------------------------------------------------- 更新（GitHub のリリースを見て exe を入れ替える）
    // 本物の GitHub には触れず、手元の偽サーバーで「確認 → ダウンロード → SHA-256 照合 → 入れ替え」を通す
    async Task TestUpdater()
    {
        var dir = Path.Combine(testOut, "updater");
        try { Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);

        var good = Encoding.ASCII.GetBytes("MZ" + new string('x', 30000));
        var goodSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(good)).ToLowerInvariant();
        var bad = Encoding.ASCII.GetBytes("MZ" + new string('y', 30000));
        string mode = "ok";

        HttpListener l = null;
        string root = null;
        var rnd = new Random();
        for (int i = 0; i < 20 && l == null; i++)
        {
            var port = rnd.Next(20000, 60000);
            var x = new HttpListener();
            x.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { x.Start(); l = x; root = $"http://127.0.0.1:{port}"; } catch { x.Close(); }
        }
        if (l == null) { Check("更新テスト用サーバーを起動できる", false); return; }
        var listener = l;
        var baseRoot = root;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { return; }
                var path = c.Request.Url.AbsolutePath;
                byte[] body;
                if (path == "/api")
                {
                    c.Response.ContentType = "application/json";
                    var json = JsonSerializer.Serialize(new
                    {
                        tag_name = "v9.9.9",
                        html_url = "https://github.com/kaikomziu/hizashi/releases/tag/v9.9.9",
                        body = mode == "nosha" ? "## 変更\n- なし" : $"## ダウンロード\nSHA-256: `{goodSha}`\n\n- 更新機能のテスト\n- 2つ目の変更",
                        assets = new[] { new { name = "Hizashi.exe", browser_download_url = baseRoot + (mode == "badhash" ? "/bad.exe" : "/Hizashi.exe"), size = good.Length } },
                    });
                    body = Encoding.UTF8.GetBytes(json);
                }
                else if (path == "/Hizashi.exe") { c.Response.ContentType = "application/octet-stream"; body = good; }
                else if (path == "/bad.exe") { c.Response.ContentType = "application/octet-stream"; body = bad; }
                else { c.Response.StatusCode = 404; body = Array.Empty<byte>(); }
                try { c.Response.OutputStream.Write(body); c.Response.Close(); } catch { }
            }
        });

        Environment.SetEnvironmentVariable("HIZASHI_UPDATE_API", baseRoot + "/api");
        try
        {
            using var http = Updater.CreateClient();

            // 確認
            var rel = await Limit(Updater.CheckAsync(http), 15000, "更新の確認");
            Check("更新の確認でリリース情報を読める", rel.Tag == "v9.9.9" && rel.Version == new Version(9, 9, 9) && rel.Sha256 == goodSha, rel.Tag);
            Check("いまの版より新しいと判定する", Updater.IsNewer(rel, Updater.Current()));
            Check("同じ版・古い版は更新なしと判定する", !Updater.IsNewer(rel, new Version(9, 9, 9)) && !Updater.IsNewer(rel, new Version(10, 0, 0)));
            Check("リリースノートの箇条書きを要約できる", SummarizeNotes(rel.Notes).Contains("・更新機能のテスト") && SummarizeNotes(rel.Notes).Contains("・2つ目の変更"), SummarizeNotes(rel.Notes));

            // ダウンロードして入れ替え
            var target = Path.Combine(dir, "Hizashi.exe");
            File.WriteAllText(target, "OLD-VERSION");
            var tmp = target + ".update";
            await Updater.DownloadAsync(http, rel, tmp).WaitAsync(TimeSpan.FromSeconds(20));
            Check("ダウンロードして SHA-256 が一致する", File.Exists(tmp) && File.ReadAllBytes(tmp).SequenceEqual(good));
            Updater.Swap(tmp, target);
            Check("入れ替え後は新しい exe になる", File.ReadAllBytes(target).SequenceEqual(good));
            Check("古い exe は .old として残る（動作中でも名前変更で退避できる）", File.ReadAllText(target + ".old") == "OLD-VERSION");
            Check("入れ替え後にダウンロードの一時ファイルが残らない", !File.Exists(tmp));

            // 2回目の更新でも、前回の .old を片付けて入れ替えられる
            File.WriteAllText(tmp, "MZ-second");
            Updater.Swap(tmp, target);
            Check("続けて入れ替えても失敗しない", File.ReadAllText(target) == "MZ-second" && File.Exists(target + ".old"));

            // SHA-256 が違うファイルは捨てる
            mode = "badhash";
            var rel2 = await Limit(Updater.CheckAsync(http), 15000, "更新の確認");
            var tmp2 = Path.Combine(dir, "bad.update");
            var rejected = false;
            try { await Updater.DownloadAsync(http, rel2, tmp2).WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (InvalidDataException) { rejected = true; }
            Check("SHA-256 が一致しないファイルは受け付けず、消す", rejected && !File.Exists(tmp2));

            // SHA-256 が無いリリースは更新しない
            mode = "nosha";
            var noSha = false;
            try { await Limit(Updater.CheckAsync(http), 15000, "更新の確認"); }
            catch (InvalidDataException) { noSha = true; }
            Check("SHA-256 の無いリリースは更新しない", noSha);

            // 入れ替えられない場所・開発用の実行は自動更新しない
            Check("Hizashi.exe 以外（開発用の実行など）は自動で入れ替えない", !Updater.CanReplace(Path.Combine(dir, "dotnet.exe"), out var why) && !string.IsNullOrEmpty(why));
            Check("書き込めるフォルダの Hizashi.exe は入れ替えられる", Updater.CanReplace(target, out _));
        }
        catch (Exception e)
        {
            Check("更新の流れが最後まで通る", false, e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HIZASHI_UPDATE_API", null);
            try { listener.Stop(); } catch { }
        }

        // 実際の GitHub を読めることの確認（HIZASHI_TEST_NET=1 のときだけ。公開後に一度、手元で確かめる用）
        if (Environment.GetEnvironmentVariable("HIZASHI_TEST_NET") == "1")
        {
            try
            {
                using var h = Updater.CreateClient();
                var r = await Limit(Updater.CheckAsync(h), 20000, "GitHubへの確認");
                Check("（通信あり）GitHub の最新リリースを読める", r.Sha256.Length == 64 && r.ExeUrl.StartsWith("https://github.com/kaikomziu/hizashi/releases/download/"), r.Tag + " " + r.ExeUrl);
            }
            catch (Exception e) { Check("（通信あり）GitHub の最新リリースを読める", false, e.Message); }
        }

        // 通常時は本家リポジトリの最新リリースを見に行く（通信はしない）
        var api = typeof(Updater).GetProperty("ApiUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) as string;
        Check("通常時の問い合わせ先は kaikomziu/hizashi の releases/latest", api == "https://api.github.com/repos/kaikomziu/hizashi/releases/latest", api);
        Check("「更新を確認」コマンドがある", commands.Any(c => c.Id == "app.update"));
    }


    // ---------------------------------------------------------------- 拡張機能
    static string WriteTestExtension(string dir, string name, string version = "1.2.3")
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["manifest_version"] = 3,
            ["name"] = name,
            ["version"] = version,
            ["description"] = "Hizashi の自動テスト用",
            ["permissions"] = new[] { "storage", "tabs" },
            ["host_permissions"] = new[] { "http://127.0.0.1/*" },
            ["content_scripts"] = new[] { new Dictionary<string, object> { ["matches"] = new[] { "http://127.0.0.1/*", "https://hizashi.invalid/*" }, ["js"] = new[] { "cs.js" }, ["run_at"] = "document_start" } },
            ["action"] = new Dictionary<string, object> { ["default_popup"] = "popup.html" },
            ["options_page"] = "options.html",
        }));
        File.WriteAllText(Path.Combine(dir, "cs.js"), "document.documentElement.setAttribute('data-hz-ext', '1');");
        File.WriteAllText(Path.Combine(dir, "popup.html"), "<!doctype html><title>HZ-POPUP</title><body>popup</body>");
        File.WriteAllText(Path.Combine(dir, "options.html"), "<!doctype html><title>HZ-OPTIONS</title><body>options</body>");
        return dir;
    }

    static void PutProtoBytes(MemoryStream ms, int field, byte[] data)
    {
        void Varint(ulong v) { while (v >= 0x80) { ms.WriteByte((byte)(v | 0x80)); v >>= 7; } ms.WriteByte((byte)v); }
        Varint((ulong)((field << 3) | 2));
        Varint((ulong)data.Length);
        ms.Write(data);
    }

    // 本物の CRX3 と同じ作りのファイル（ヘッダー + zip）を作る
    static (byte[] crx, byte[] pub) BuildCrx3(byte[] zip)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pub = rsa.ExportSubjectPublicKeyInfo();
        var crxId = System.Security.Cryptography.SHA256.HashData(pub)[..16];
        var proof = new MemoryStream();
        PutProtoBytes(proof, 1, pub);
        PutProtoBytes(proof, 2, new byte[256]);
        var signed = new MemoryStream();
        PutProtoBytes(signed, 1, crxId);
        var header = new MemoryStream();
        PutProtoBytes(header, 2, proof.ToArray());
        PutProtoBytes(header, 10000, signed.ToArray());
        var h = header.ToArray();
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("Cr24"));
        ms.Write(BitConverter.GetBytes(3u));
        ms.Write(BitConverter.GetBytes((uint)h.Length));
        ms.Write(h);
        ms.Write(zip);
        return (ms.ToArray(), pub);
    }

    async Task ClickPromptOk()
    {
        for (int i = 0; i < 50 && overlayMode != "prompt"; i++) await Sleep(100);
        await Sleep(200);
        await UiJs(overlayView, "document.getElementById('pr-ok').click(); 1");
    }

    async Task TestExtensions(string baseUrl)
    {
        var src = Path.Combine(testOut, "ext-src");
        try { Directory.Delete(src, true); } catch { }
        var dirA = WriteTestExtension(Path.Combine(src, "a"), "Hizashi Test Ext");

        // --- ID の読み取り・CRX の解析（通信なし）
        const string sampleId = "eimadpbcbfnmbkopoojfekhnkhdbieeh";
        Check("拡張機能の ID だけでも読み取れる", ExtensionPackage.ParseId(sampleId) == sampleId);
        Check("ストアの URL（新旧どちらも）から ID を読み取れる",
            ExtensionPackage.ParseId($"https://chromewebstore.google.com/detail/dark-reader/{sampleId}?hl=ja") == sampleId &&
            ExtensionPackage.ParseId($"https://chrome.google.com/webstore/detail/dark-reader/{sampleId}") == sampleId);
        Check("ストア以外の URL・不正な ID は受け付けない",
            ExtensionPackage.ParseId($"https://evil.example/detail/x/{sampleId}") == null && ExtensionPackage.ParseId("abc") == null && ExtensionPackage.ParseId("") == null);
        Check("ストアの商品ページかどうかを判定できる",
            ExtensionPackage.IsStoreUrl($"https://chromewebstore.google.com/detail/x/{sampleId}") && !ExtensionPackage.IsStoreUrl("https://example.com/detail/x"));

        var zipPath = Path.Combine(testOut, "ext-a.zip");
        try { File.Delete(zipPath); } catch { }
        System.IO.Compression.ZipFile.CreateFromDirectory(dirA, zipPath);
        var zipBytes = File.ReadAllBytes(zipPath);
        var (crx, pub) = BuildCrx3(zipBytes);
        var parsed = ExtensionPackage.ParseCrx(crx);
        Check("CRX3 のヘッダーから zip と公開鍵を取り出せる", parsed.Zip.SequenceEqual(zipBytes) && parsed.PublicKey != null && parsed.PublicKey.SequenceEqual(pub));
        Check("普通の zip もそのまま扱える", ExtensionPackage.ParseCrx(zipBytes).Zip.SequenceEqual(zipBytes));
        var bad = false;
        try { ExtensionPackage.ParseCrx(Encoding.ASCII.GetBytes("これは拡張機能ではありません")); } catch (InvalidDataException) { bad = true; }
        Check("拡張機能でないファイルは受け付けない", bad);

        var work = Path.Combine(testOut, "ext-work");
        var prep = ExtensionPackage.Prepare(parsed.Zip, parsed.PublicKey, work);
        var expectId = ExtensionPackage.IdFromKey(pub);
        Check("鍵から拡張機能の ID を求められる（32文字の a〜p）", System.Text.RegularExpressions.Regex.IsMatch(expectId, "^[a-p]{32}$") && prep.KeyId == expectId, prep.KeyId);
        Check("manifest に鍵が書き足される（ストアと同じ ID になる）", File.ReadAllText(Path.Combine(work, "manifest.json")).Contains("\"key\""));
        Check("拡張機能の名前・版数・権限の要約を読める", prep.Name == "Hizashi Test Ext" && prep.Version == "1.2.3" && prep.Permissions.Any(x => x.Contains("タブ")) && !prep.AllSites,
            string.Join(" / ", prep.Permissions));
        try { Directory.Delete(work, true); } catch { }

        // 「_metadata」フォルダは消す。フォルダの外へ書き出す zip は拒否する
        var metaZip = new MemoryStream();
        using (var za = new System.IO.Compression.ZipArchive(metaZip, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            foreach (var f in Directory.GetFiles(dirA)) za.CreateEntryFromFile(f, Path.GetFileName(f));
            using var w = new StreamWriter(za.CreateEntry("_metadata/verified_contents.json").Open()); w.Write("{}");
        }
        var prep2 = ExtensionPackage.Prepare(metaZip.ToArray(), null, work);
        Check("「_metadata」フォルダは取り除く", !Directory.Exists(Path.Combine(work, "_metadata")) && prep2.KeyId == null);
        try { Directory.Delete(work, true); } catch { }

        var evilZip = new MemoryStream();
        using (var za = new System.IO.Compression.ZipArchive(evilZip, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            za.CreateEntryFromFile(Path.Combine(dirA, "manifest.json"), "manifest.json");
            using var w = new StreamWriter(za.CreateEntry("../evil-from-zip.txt").Open()); w.Write("x");
        }
        var blocked = false;
        try { ExtensionPackage.Prepare(evilZip.ToArray(), null, work); } catch { blocked = true; }
        Check("フォルダの外へ書き出す zip は拒否する", blocked && !File.Exists(Path.Combine(testOut, "evil-from-zip.txt")));
        try { Directory.Delete(work, true); } catch { }

        var allZip = new MemoryStream();
        using (var za = new System.IO.Compression.ZipArchive(allZip, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(za.CreateEntry("manifest.json").Open());
            w.Write("{\"manifest_version\":2,\"name\":\"全サイト\",\"version\":\"1\",\"permissions\":[\"<all_urls>\",\"cookies\"]}");
        }
        var prep3 = ExtensionPackage.Prepare(allZip.ToArray(), null, work);
        Check("全サイトに触れる拡張機能は、権限の要約でそう表示される", prep3.AllSites && prep3.Permissions.Any(x => x.Contains("すべてのサイト")) && prep3.Permissions.Any(x => x.Contains("Cookie")), string.Join(" / ", prep3.Permissions));
        try { Directory.Delete(work, true); } catch { }

        // --- 実際に WebView2 へ読み込む
        Check("起動時に拡張機能が使える状態になっている", ExtProfile() != null);
        await ExtAddFromPathAsync(dirA, true, confirm: false);
        var item = extItems.FirstOrDefault(x => x.Name == "Hizashi Test Ext");
        Check("フォルダから拡張機能を追加できる", item != null && item.Enabled && item.Version == "1.2.3" && item.HasPopup && item.HasOptions,
            string.Join(",", extItems.Select(x => x.Name)));
        if (item == null) return;

        var page = CreateTab($"{baseUrl}/p?t=ext");
        Check("拡張機能のコンテンツスクリプトがページで動く", await WaitLoad(page) && await JS(page, "document.documentElement.getAttribute('data-hz-ext')") == "1");

        var nt = CreateTab(NewtabUrl);
        await WaitLoad(nt);
        Check("Hizashi 自身の画面（外枠・オーバーレイ）には、拡張機能が入り込まない",
            await UiJs(chrome, "String(document.documentElement.getAttribute('data-hz-ext'))") == "null" &&
            await UiJs(overlayView, "String(document.documentElement.getAttribute('data-hz-ext'))") == "null" &&
            await UiJs(chrome, "location.origin") == "https://hizashi.invalid",
            "（同じ https://hizashi.invalid でも、タブの中のページには入る: " + await JS(nt, "String(document.documentElement.getAttribute('data-hz-ext'))") + "）");
        CloseTab(nt.Id, true);

        // ポップアップ・設定ページ
        ExtOpenPage(item.Id, false);
        await Sleep(500);
        var pop = ActiveTab;
        var okPop = pop != null && pop.Url.StartsWith("chrome-extension://" + item.Id) && await WaitLoad(pop) && await JS(pop, "document.title") == "HZ-POPUP";
        Check("拡張機能のポップアップをタブで開ける", okPop, pop?.Url);
        if (pop != null) CloseTab(pop.Id, true);
        ExtOpenPage(item.Id, true);
        await Sleep(500);
        var opt = ActiveTab;
        Check("拡張機能の設定ページをタブで開ける", opt != null && await WaitLoad(opt) && await JS(opt, "document.title") == "HZ-OPTIONS", opt?.Url);
        if (opt != null) CloseTab(opt.Id, true);

        // 無効 → 有効
        await ExtToggleAsync(item.Id, false);
        var p2 = CreateTab($"{baseUrl}/p?t=ext2");
        await WaitLoad(p2);
        var offVal = await JS(p2, "String(document.documentElement.getAttribute('data-hz-ext'))");
        Check("無効にすると、コンテンツスクリプトが動かなくなる", offVal == "null" && !extItems.First(x => x.Id == item.Id).Enabled, offVal);
        await ExtToggleAsync(item.Id, true);
        p2.Core.Reload();
        await Sleep(1500);
        await WaitLoad(p2);
        Check("有効に戻すと、また動く", await JS(p2, "document.documentElement.getAttribute('data-hz-ext')") == "1" && extItems.First(x => x.Id == item.Id).Enabled);
        CloseTab(p2.Id, true);
        CloseTab(page.Id, true);

        // 削除（確認画面で「削除する」を押す）
        var dirB = WriteTestExtension(Path.Combine(src, "b"), "Hizashi Test Ext B");
        await ExtAddFromPathAsync(dirB, true, confirm: false);
        var itemB = extItems.FirstOrDefault(x => x.Name == "Hizashi Test Ext B");
        var folderB = itemB != null ? Path.Combine(ExtDir, extIndex[itemB.Id].Folder) : null;
        Check("ほかの拡張機能も追加でき、一覧に並ぶ", itemB != null && extItems.Count >= 2 && Directory.Exists(folderB));
        // 追加が終わると、拡張機能のパネルは自動で開き直される（確認画面で閉じたあとの復帰）
        Check("追加が終わると、拡張機能のパネルが開き直される", overlayMode == "panel" && panelName == "extensions", $"mode={overlayMode} panel={panelName}");
        if (!(overlayMode == "panel" && panelName == "extensions")) OpenPanel("extensions");
        await Sleep(1200);
        Check("パネル「extensions」が開く", overlayMode == "panel" && panelName == "extensions");
        await Shot(overlayView.CoreWebView2, "17-panel-extensions");
        HideOverlay(false);
        if (itemB != null)
        {
            var rm = ExtRemoveAsync(itemB.Id);
            await ClickPromptOk();
            await rm;
            Check("拡張機能を削除すると、一覧とフォルダから消える", extItems.All(x => x.Id != itemB.Id) && !Directory.Exists(folderB) && extItems.Any(x => x.Id == item.Id));
        }

        // ストアのページに出すボタン
        Check("Edge に組み込みの拡張機能（PDF ビューアーなど）は、一覧に出さない", extItems.All(x => extIndex.ContainsKey(x.Id)) && extItems.All(x => !x.Name.Contains("PDF")));
        var sp = CreateTab($"{baseUrl}/p?t=store");
        await WaitLoad(sp);
        await sp.Core.ExecuteScriptAsync(ExtensionPackage.StoreButtonJs);
        Check("ストアのページに出す「Hizashi に追加」ボタンを差し込める", await JS(sp, "String(!!document.getElementById('hizashi-ext-btn'))") == "true");
        await JS(sp, "document.getElementById('hizashi-ext-btn').click(); 1");
        await Sleep(800);
        Check("ストア以外のページからの追加要求は無視する", overlayMode != "prompt" && !extBusy);
        CloseTab(sp.Id, true);
        Check("ストアかどうかはホスト名で判定する（似た名前のサイトは対象外）",
            ExtensionPackage.IsStoreHost("chromewebstore.google.com") && !ExtensionPackage.IsStoreHost("chromewebstore.google.com.evil.example") && !ExtensionPackage.IsStoreHost("evil.example"));

        // 実際のウェブストアから取得（HIZASHI_TEST_NET=1 のときだけ）
        if (Environment.GetEnvironmentVariable("HIZASHI_TEST_NET") == "1")
        {
            try
            {
                var add = ExtAddFromStoreAsync($"https://chromewebstore.google.com/detail/dark-reader/{sampleId}");
                await ClickPromptOk();
                await add.WaitAsync(TimeSpan.FromSeconds(90));
                var dr = extItems.FirstOrDefault(x => x.Id == sampleId);
                Check("（通信あり）ウェブストアから拡張機能を取得して追加できる（ID がストアと同じ）", dr != null && dr.FromStore, string.Join(",", extItems.Select(x => x.Name + ":" + x.Id[..6])));
                if (dr != null)
                {
                    var rm = ExtRemoveAsync(dr.Id);
                    await ClickPromptOk();
                    await rm;
                }
            }
            catch (Exception e) { Check("（通信あり）ウェブストアから拡張機能を取得して追加できる", false, e.Message); }
        }
    }

    // ---------------------------------------------------------------- README・紹介ページ用のスクリーンショット
    // 使い捨てのプロファイルで、サンプルサイト（https://kurashi-memo.example/ ＝実在しない予約ドメイン）を使って撮る。
    // 個人の履歴やブックマークは写らない。
    async Task Snap(string name)
    {
        Activate();
        Native.ForceForeground(Handle);
        await Sleep(500);
        WinShot(name);
    }
    async Task TestShots()
    {
        const string host = "kurashi-memo.example";
        var siteDir = Path.Combine(testOut, "demo-site");
        Directory.CreateDirectory(siteDir);
        File.WriteAllText(Path.Combine(siteDir, "column.html"), DemoPage);
        foreach (var (file, title) in new[] { ("recipe.html", "今週のレシピ"), ("game.html", "新作ゲームのレビュー"), ("trip.html", "週末の旅行プラン"), ("english.html", "英語の勉強メモ"), ("coding.html", "プログラミング入門") })
            File.WriteAllText(Path.Combine(siteDir, file), $"<!doctype html><html lang='ja'><head><meta charset='utf-8'><title>{title}</title></head><body style=\"font-family:'Yu Gothic UI',sans-serif;background:#f6f4ef;margin:0\"><div style=\"padding:80px 60px\"><h1 style=\"font-weight:600\">{title}</h1><p style=\"color:#666\">サンプルのページです。</p></div></body></html>");
        extraHosts[host] = siteDir;
        string Url(string f) => $"https://{host}/{f}";

        Directory.CreateDirectory(Path.Combine(testOut, "shots"));
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        ClientSize = new Size(1360, 840);
        Activate();
        await Sleep(1500);

        // 1) 新しいタブ
        await Sleep(1500);
        await Snap("shots/01-newtab");

        // 2) コマンドパレット（読み物ページの上に重ねる）
        var a = CreateTab(Url("column.html"));
        await WaitLoad(a);
        await Sleep(800);
        TogglePalette();
        await Sleep(900);
        await UiJs(overlayView, "(() => { const i = document.getElementById('pal-input'); i.value = '分割'; i.dispatchEvent(new Event('input')); return 1; })()");
        await Sleep(900);
        await Snap("shots/02-palette");
        HideOverlay(false);

        // 3) 分割表示: 左は通常のページ、右はリーダーモード
        var b = CreateTab(Url("column.html"));
        await WaitLoad(b);
        await Sleep(600);
        RunCommand("tools.reader");
        await Sleep(1800);
        ActivateTab(a.Id);
        await Sleep(500);
        RunCommand("view.split");
        await Sleep(3600); // トーストが消えるのを待つ
        await Snap("shots/03-split-reader");
        RunCommand("view.split");
        await Sleep(500);

        // 4) 縦タブ + ワークスペース + ダークモード + ピン留め
        S.Workspaces.Add(new Workspace { Id = "w2", Name = "趣味", Color = "#f28b82" });
        S.Workspaces.Add(new Workspace { Id = "w3", Name = "勉強", Color = "#81c995" });
        foreach (var f in new[] { "recipe.html", "game.html", "trip.html" }) CreateTab(Url(f), new TabOpts { Ws = "w2", Activate = false });
        foreach (var f in new[] { "english.html", "coding.html" }) CreateTab(Url(f), new TabOpts { Ws = "w3", Activate = false });
        CloseTab(b.Id);
        a.Pinned = true;
        ActivateTab(a.Id);
        S.TabLayout = "side";
        ScheduleState();
        await Sleep(1200);
        RunCommand("tools.dark");
        await Sleep(3800);
        await Snap("shots/04-side-workspaces-dark");
        RunCommand("tools.dark");
        await Sleep(3300); // 「ダークモード OFF」のトーストが消えるのを待つ
        S.TabLayout = "top";
        ScheduleState();
        await Sleep(800);

        // 5) クイックメモ
        Notes.Insert(0, new Note { Id = Util.Now(), Ts = Util.Now(), Text = "週末にやること\n・図書館で本を返す\n・ブラウザの設定を整える\n・散歩のついでにカフェで読書\n\n— ブラウザの片隅に、小さな工夫を\n" + Url("column.html") });
        OpenPanel("notes");
        await Sleep(1500);
        await Snap("shots/05-notes");
        HideOverlay(false);

        // 6) 設定・コマンド一覧・履歴パネル
        foreach (var (name, file) in new[] { ("settings", "07-settings"), ("help", "08-help"), ("history", "09-history") })
        {
            OpenPanel(name);
            await Sleep(1500);
            await Snap("shots/" + file);
            HideOverlay(false);
        }

        // 7) リーダーモード（単独）
        var rd = CreateTab(Url("column.html"));
        await WaitLoad(rd);
        await Sleep(600);
        RunCommand("tools.reader");
        await Sleep(3800);
        await Snap("shots/10-reader");
        CloseTab(rd.Id);

        // 8) ページ内検索
        var fd = CreateTab(Url("column.html"));
        await WaitLoad(fd);
        await Sleep(600);
        RunCommand("view.find");
        await Sleep(500);
        await UiJs(chrome, "(() => { const i = document.getElementById('find-input'); i.value = 'タブ'; i.dispatchEvent(new Event('input')); return 1; })()");
        await Sleep(1500);
        await Snap("shots/11-find");
        await UiJs(chrome, "document.getElementById('find-close').click()");
        CloseTab(fd.Id);

        // 9) 要素ブロッカー（消す前と後）
        var bl = CreateTab(Url("column.html"));
        await WaitLoad(bl);
        await Sleep(600);
        await JS(bl, "document.getElementById('promo').scrollIntoView({block:'center'}); 1");
        await Sleep(900);
        await Snap("shots/12-blocker-before");
        RunCommand("tools.picker");
        await Sleep(900);
        var pr = JsonDocument.Parse(await JS(bl, "JSON.stringify(document.getElementById('promo').getBoundingClientRect())")).RootElement;
        await ClickAt(bl.Core, (int)(pr.Num("x") + pr.Num("width") / 2), (int)(pr.Num("y") + pr.Num("height") / 2));
        await Sleep(4300);
        await Snap("shots/13-blocker-after");
        Sites.Hidden.Clear();
        CloseTab(bl.Id);

        // 10) 縦タブ（折りたたみ）
        ActivateTab(a.Id);
        S.TabLayout = "side";
        S.SidebarCollapsed = true;
        ScheduleState();
        await Sleep(1400);
        await Snap("shots/14-side-collapsed");
        S.TabLayout = "top";
        S.SidebarCollapsed = false;
        ScheduleState();
        await Sleep(800);

        // 11) 集中モード
        StartFocus(25);
        var ft = CreateTab("https://www.youtube.com/");
        await Sleep(3500);
        await Shot(ft.Core, "shots/06-focus-page");
        StopFocus();

        Check("スクリーンショットを撮影した", Directory.GetFiles(Path.Combine(testOut, "shots"), "*.png").Length >= 6);
    }
    // ---------------------------------------------------------------- メモリ計測用（外部から計測する）
    // 新しいタブ + 外部サイト3つを開いて安定させ、準備完了の目印ファイルを置いてしばらく待つ
    async Task TestMem()
    {
        foreach (var u in new[] { "https://example.com/", "https://example.org/", "https://example.net/" })
            CreateTab(u);
        await Sleep(15000);
        File.WriteAllText(Path.Combine(testOut, "mem-ready.txt"), "ready");
        await Sleep(45000);
        Check("メモリ計測の待機が終わった", true);
    }

    // ---------------------------------------------------------------- セッション復元テスト
    async Task TestRestore()
    {
        await Sleep(2000);
        var awake = tabs.Count(t => t.View != null);
        Check("前回のタブが復元される", tabs.Count >= 5, $"tabs={tabs.Count}");
        Check("開いているタブ以外は休止状態で復元される（起動が軽い）", awake == 1, $"awake={awake}");
        Check("アクティブなタブが決まっている", ActiveTab != null && ActiveTab.View != null);
        Check("ワークスペースが復元される", S.Workspaces.Any(w => w.Id == "wtest"));
        Check("ブックマークが保存されている", Bookmarks.Count >= 5, $"count={Bookmarks.Count}");
        Check("履歴が保存されている", History.Count >= 3, $"count={History.Count}");
        Check("サイトごとの設定が保存されている", Sites.Zoom != null && Sites.Dark.Count >= 1);
        Check("追加した拡張機能が、再起動後も入っている", extItems.Any(x => x.Name == "Hizashi Test Ext" && x.Enabled), string.Join(",", extItems.Select(x => x.Name)));
        var ntab = CreateTab(NewtabUrl);
        await WaitLoad(ntab);
        Check("再起動後も、拡張機能のコンテンツスクリプトが動く", await JS(ntab, "document.documentElement.getAttribute('data-hz-ext')") == "1");
        CloseTab(ntab.Id, true);
        var slept = tabs.FirstOrDefault(t => t.View == null && Util.IsHttp(t.Url));
        if (slept != null)
        {
            ActivateTab(slept.Id);
            Check("休止タブを開くと読み込まれる", await WaitLoad(slept, 15000), slept.Url);
        }
    }
}
