# 公式サイト(docs/)を生成する。  実行: pwsh tools/build-site.ps1
#   site/src/*.html (本文)  +  site/layout.html (共通の枠)  +  site/assets/*  →  docs/
# ショートカット表・検索ショートカット・更新履歴・バージョンなどは、ソースコードや CHANGELOG.md から読み取って埋め込む
# （実装とずれないようにするため）。最後にリンク切れを検査する。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$site = Join-Path $root 'site'
$docs = Join-Path $root 'docs'
$base = 'https://kaikomziu.github.io/hizashi/'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Esc([string]$s) { [System.Net.WebUtility]::HtmlEncode($s) }
function Read([string]$p) { [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8) }
function Inline([string]$s) {   # `code` と **強調** だけを変換する簡易マークダウン
    $t = Esc $s
    $t = [regex]::Replace($t, '`([^`]+)`', '<code>$1</code>')
    $t = [regex]::Replace($t, '\*\*([^*]+)\*\*', '<strong>$1</strong>')
    return $t
}

# ---------------------------------------------------------------- 値の読み取り
$version = [regex]::Match((Read "$root\Hizashi.csproj"), '<Version>([^<]+)</Version>').Groups[1].Value
# Android 版（別リポジトリ kaikomziu/hizashi-android）の最新版。新しい版を出したらここを更新する
$androidVersion = '0.1.0'
# 最新版の公開日は CHANGELOG の見出し「## [x.y.z] - YYYY-MM-DD」から読む
$dm = [regex]::Match((Read "$root\CHANGELOG.md"), '(?m)^## \[[^\]]+\] - (\d{4})-(\d{2})-(\d{2})')
$releaseDate = if ($dm.Success) { '{0}年{1}月{2}日' -f $dm.Groups[1].Value, [int]$dm.Groups[2].Value, [int]$dm.Groups[3].Value } else { '' }

# コマンド一覧（MainForm.Commands.cs の BuildCommands から）
$cmdSrc = (Read "$root\MainForm.Commands.cs")
$from = $cmdSrc.IndexOf('void BuildCommands()')
$to = $cmdSrc.IndexOf('// ショートカット表を作る', $from)
$block = $cmdSrc.Substring($from, $to - $from)
$groups = [ordered]@{}
$cur = 'その他'
foreach ($line in $block -split "`r?`n") {
    if ($line -match '^\s*//\s*(.+?)\s*$') { $cur = $Matches[1]; continue }
    if ($line -match 'Cmd\("([^"]+)",\s*"([^"]+)",\s*"([^"]*)"') {
        if (-not $groups.Contains($cur)) { $groups[$cur] = New-Object System.Collections.Generic.List[object] }
        $groups[$cur].Add([pscustomobject]@{ Id = $Matches[1]; Label = $Matches[2]; Keys = $Matches[3] })
    }
}
$cmdTotal = ($groups.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
$sb = New-Object System.Text.StringBuilder
foreach ($g in $groups.Keys) {
    [void]$sb.AppendLine("<h3>$(Esc $g)</h3>")
    [void]$sb.AppendLine('<div class="tbl-wrap"><table><thead><tr><th>コマンド</th><th>ショートカット</th></tr></thead><tbody>')
    foreach ($c in $groups[$g]) {
        $k = if ($c.Keys) { (($c.Keys -split '\+') | ForEach-Object { "<kbd>$(Esc $_)</kbd>" }) -join '+' } else { '<span class="note">（パレットから）</span>' }
        [void]$sb.AppendLine("<tr><td>$(Esc $c.Label)</td><td class=`"k`">$k</td></tr>")
    }
    [void]$sb.AppendLine('</tbody></table></div>')
}
$commandsHtml = $sb.ToString()

# 検索ショートカット（Util.cs の Bangs から）
$bangNames = @{ g='Google'; ddg='DuckDuckGo'; bing='Bing'; yt='YouTube'; gh='GitHub'; w='Wikipedia（日本語）'; wen='Wikipedia（英語）'; map='Googleマップ'; img='Google画像検索'; amz='Amazon.co.jp'; nico='ニコニコ動画'; x='X（旧Twitter）'; tr='Google翻訳'; so='Stack Overflow'; mdn='MDN Web Docs'; npm='npm'; r='Reddit' }
$utilSrc = Read "$root\Util.cs"
$bFrom = $utilSrc.IndexOf('Bangs = new()')
$bTo = $utilSrc.IndexOf('};', $bFrom)
$bangs = [regex]::Matches($utilSrc.Substring($bFrom, $bTo - $bFrom), '\["([^"]+)"\]\s*=\s*"([^"]+)"')
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<div class="tbl-wrap"><table><thead><tr><th>キーワード</th><th>検索先</th><th>入力例</th></tr></thead><tbody>')
foreach ($m in $bangs) {
    $key = $m.Groups[1].Value
    if (-not $bangNames.ContainsKey($key)) { Write-Warning "検索ショートカット '$key' の表示名が build-site.ps1 にありません" }
    $name = if ($bangNames.ContainsKey($key)) { $bangNames[$key] } else { $key }
    [void]$sb.AppendLine("<tr><td class=`"k`"><code>$(Esc $key)</code></td><td>$(Esc $name)</td><td><code>$(Esc $key) ねこ</code></td></tr>")
}
[void]$sb.AppendLine('</tbody></table></div>')
$bangsHtml = $sb.ToString()
$bangCount = $bangs.Count

# 広告・トラッカーのドメイン数（Blocklist.cs の AdHosts から）
$blSrc = Read "$root\Blocklist.cs"
$aFrom = $blSrc.IndexOf('AdHosts = new')
$aTo = $blSrc.IndexOf('};', $aFrom)
$hosts = [regex]::Matches($blSrc.Substring($aFrom, $aTo - $aFrom), '"([a-z0-9][a-z0-9.\-]*\.[a-z]{2,})"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$blockCount = '約{0}' -f ([math]::Floor($hosts.Count / 10) * 10)

# 更新履歴（CHANGELOG.md から）
$cl = Read "$root\CHANGELOG.md"
$sb = New-Object System.Text.StringBuilder
foreach ($sec in [regex]::Matches($cl, '(?ms)^## \[([^\]]+)\]\s*-\s*(\S+)\s*\r?\n(.*?)(?=^## \[|\z)')) {
    $ver = $sec.Groups[1].Value; $date = $sec.Groups[2].Value; $body = $sec.Groups[3].Value
    [void]$sb.AppendLine("<section class=`"block`" id=`"v$($ver -replace '\.','-')`"><h2>v$(Esc $ver) <span class=`"note`">$(Esc $date)</span></h2>")
    $items = New-Object System.Collections.Generic.List[string]
    $para = New-Object System.Collections.Generic.List[string]
    foreach ($l in $body -split "`r?`n") {
        if ($l -match '^- (.+)$') { $items.Add($Matches[1]) }
        elseif ($l -match '^\s{2,}(\S.*)$' -and $items.Count -gt 0) { $items[$items.Count - 1] += ' ' + $Matches[1] }
        elseif ($l.Trim()) { $para.Add($l.Trim()) }
    }
    foreach ($p in $para) { [void]$sb.AppendLine("<p>$(Inline $p)</p>") }
    if ($items.Count) { [void]$sb.AppendLine('<ul>'); foreach ($i in $items) { [void]$sb.AppendLine("<li>$(Inline $i)</li>") }; [void]$sb.AppendLine('</ul>') }
    [void]$sb.AppendLine('</section>')
}
$changelogHtml = $sb.ToString()

# ---------------------------------------------------------------- 共通の枠
$layout = Read "$site\layout.html"
$navItems = @(
    @{ id = 'features';  href = 'features.html';  text = '特長' },
    @{ id = 'guide';     href = 'guide.html';     text = '使い方' },
    @{ id = 'shortcuts'; href = 'shortcuts.html'; text = 'ショートカット' },
    @{ id = 'faq';       href = 'faq.html';       text = 'よくある質問' },
    @{ id = 'changelog'; href = 'changelog.html'; text = '更新履歴' },
    @{ id = 'download';  href = 'download.html';  text = 'ダウンロード'; cta = $true }
)
function Build-Nav([string]$active, [string]$rootPrefix) {
    ($navItems | ForEach-Object {
        $cls = if ($_.cta) { ' class="cta"' } else { '' }
        $cur = if ($_.id -eq $active) { ' aria-current="page"' } else { '' }
        "      <a href=`"$rootPrefix$($_.href)`"$cls$cur>$($_.text)</a>"
    }) -join "`n"
}

$jsonLd = @"
<script type="application/ld+json">
{"@context":"https://schema.org","@type":"SoftwareApplication","name":"Hizashi","alternateName":"日差し","applicationCategory":"BrowserApplication","operatingSystem":"Windows 10, Windows 11","softwareVersion":"$version","license":"https://opensource.org/licenses/MIT","isAccessibleForFree":true,"offers":{"@type":"Offer","price":"0","priceCurrency":"JPY"},"downloadUrl":"https://github.com/kaikomziu/hizashi/releases/latest/download/Hizashi.exe","url":"$base","author":{"@type":"Person","name":"kaikomziu"},"inLanguage":"ja"}
</script>
"@

# ---------------------------------------------------------------- ページを生成
New-Item -ItemType Directory -Force "$docs\assets" | Out-Null
Copy-Item "$site\assets\*" "$docs\assets\" -Force
$pages = New-Object System.Collections.Generic.List[string]
$srcRoot = Join-Path $site 'src'
foreach ($f in Get-ChildItem $srcRoot -Recurse -Filter *.html) {
    $rel = $f.FullName.Substring($srcRoot.Length + 1).Replace('\', '/')
    $raw = Read $f.FullName
    $m = [regex]::Match($raw, '^\s*<!--meta\s*(\{.*?\})\s*-->\s*', 'Singleline')
    if (-not $m.Success) { throw "$rel の先頭に <!--meta {...} --> がありません" }
    $meta = $m.Groups[1].Value | ConvertFrom-Json
    $content = $raw.Substring($m.Length)
    $depth = ($rel.Split('/').Count - 1)
    $rootPrefix = ('../' * $depth)
    $lang = if ($meta.lang) { $meta.lang } else { 'ja' }
    $canonical = $base + $(if ($rel -eq 'index.html') { '' } else { $rel })
    $headExtra = if ($meta.jsonld) { $jsonLd } else { '' }
    $noindex = if ($meta.noindex) { '<meta name="robots" content="noindex">' } else { '' }
    $tokens = @{
        '{{COMMANDS}}' = $commandsHtml; '{{BANGS}}' = $bangsHtml; '{{CHANGELOG}}' = $changelogHtml
        '{{VERSION}}' = $version; '{{ANDROID_VERSION}}' = $androidVersion; '{{DATE}}' = $releaseDate;'{{BLOCK_COUNT}}' = $blockCount; '{{CMD_TOTAL}}' = "$cmdTotal"; '{{BANG_COUNT}}' = "$bangCount"
        '{{root}}' = $rootPrefix; '{{YEAR}}' = (Get-Date).Year.ToString()
    }
    $desc = $meta.description
    foreach ($k in $tokens.Keys) { $content = $content.Replace($k, $tokens[$k]); $desc = $desc.Replace($k, $tokens[$k]) }
    $title = if ($rel -eq 'index.html' -or $rel -eq 'en/index.html') { $meta.title } else { "$($meta.title) | Hizashi" }
    $html = $layout
    $vals = [ordered]@{
        '{{content}}' = $content; '{{nav}}' = (Build-Nav $meta.nav $rootPrefix)
        '{{title}}' = (Esc $title); '{{description}}' = (Esc $desc); '{{canonical}}' = $canonical
        '{{lang}}' = $lang; '{{root}}' = $rootPrefix; '{{head_extra}}' = ($headExtra + $noindex); '{{year}}' = (Get-Date).Year.ToString()
    }
    foreach ($k in $vals.Keys) { $html = $html.Replace($k, $vals[$k]) }
    $out = Join-Path $docs $rel
    New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
    [System.IO.File]::WriteAllText($out, $html, $utf8)
    if (-not $meta.noindex) { $pages.Add($rel) }
}

# sitemap.xml / robots.txt
$today = Get-Date -Format 'yyyy-MM-dd'
$urls = ($pages | Sort-Object | ForEach-Object {
    $loc = if ($_ -eq 'index.html') { $base } else { $base + $_ }
    "  <url><loc>$loc</loc><lastmod>$today</lastmod></url>"
}) -join "`n"
[System.IO.File]::WriteAllText("$docs\sitemap.xml", "<?xml version=`"1.0`" encoding=`"UTF-8`"?>`n<urlset xmlns=`"http://www.sitemaps.org/schemas/sitemap/0.9`">`n$urls`n</urlset>`n", $utf8)
[System.IO.File]::WriteAllText("$docs\robots.txt", "User-agent: *`nAllow: /`n`nSitemap: ${base}sitemap.xml`n", $utf8)

"生成: {0} ページ / v{1} / コマンド {2} 件 / 検索ショートカット {3} 件 / 広告ドメイン {4}" -f $pages.Count, $version, $cmdTotal, $bangCount, $blockCount

# ---------------------------------------------------------------- リンク切れの検査
$bad = New-Object System.Collections.Generic.List[string]
$ids = @{}
foreach ($f in Get-ChildItem $docs -Recurse -Filter *.html) {
    $ids[$f.FullName] = [regex]::Matches((Read $f.FullName), '\bid="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
}
foreach ($f in Get-ChildItem $docs -Recurse -Filter *.html) {
    $h = Read $f.FullName
    foreach ($m in [regex]::Matches($h, '(?:href|src)="([^"]+)"')) {
        $u = $m.Groups[1].Value
        if ($u -match '^(https?:|mailto:|data:|javascript:)') { continue }
        $path, $frag = $u -split '#', 2
        # 404 などの絶対パス（/hizashi/...）は docs 直下として扱う
        if ($path.StartsWith('/hizashi/')) { $path = $path.Substring('/hizashi/'.Length); if ($path -eq '') { $path = 'index.html' }; $dir = $docs } else { $dir = $f.DirectoryName }
        $target = if ($path -eq '') { $f.FullName } else { [System.IO.Path]::GetFullPath((Join-Path $dir ($path -split '\?')[0])) }
        if (Test-Path $target -PathType Container) { $target = Join-Path $target 'index.html' }
        if (-not (Test-Path $target)) { $bad.Add("$($f.Name): 見つからない -> $u"); continue }
        if ($frag -and $target.EndsWith('.html') -and $ids[$target] -notcontains $frag) { $bad.Add("$($f.Name): アンカーがない -> $u") }
    }
}
if ($bad.Count) { $bad | ForEach-Object { Write-Warning $_ }; throw "リンク切れが $($bad.Count) 件あります" } else { "リンク検査: OK（切れなし）" }
