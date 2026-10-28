# GitHub / SNS で表示される紹介画像(docs/social-preview.png, 1280x640)を生成する。 実行: pwsh tools/make-social.ps1
# docs/logo.png と docs/screenshots/split-reader.png を使う。
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$W = 1280; $H = 640
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.InterpolationMode = 'HighQualityBicubic'
$g.TextRenderingHint = 'AntiAliasGridFit'

# 背景（深い緑のグラデーション + 光の粒）
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new($W, $H)), ([System.Drawing.Color]::FromArgb(255, 17, 38, 30)), ([System.Drawing.Color]::FromArgb(255, 46, 94, 60))
$g.FillRectangle($bg, 0, 0, $W, $H)
foreach ($s in @(@(1080, 90, 330, 120), @(120, 560, 190, 70), @(640, 40, 120, 60))) {
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gp.AddEllipse($s[0] - $s[2], $s[1] - $s[2], $s[2] * 2, $s[2] * 2)
    $pb = New-Object System.Drawing.Drawing2D.PathGradientBrush $gp
    $pb.CenterColor = [System.Drawing.Color]::FromArgb($s[3], 255, 244, 176)
    $pb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 244, 176))
    $g.FillPath($pb, $gp)
}

# ロゴ
$logo = [System.Drawing.Image]::FromFile((Join-Path $root 'docs\logo.png'))
$g.DrawImage($logo, 70, 120, 150, 150)

# 文字
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 245, 248, 240))
$dim = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 196, 214, 190))
$fTitle = New-Object System.Drawing.Font 'Segoe UI Semibold', 78, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$fSub = New-Object System.Drawing.Font 'Yu Gothic UI Semibold', 30, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$fSmall = New-Object System.Drawing.Font 'Yu Gothic UI', 22, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('Hizashi', $fTitle, $white, 66, 288)
$g.DrawString('日本語ネイティブの、多機能ブラウザ', $fSub, $white, 72, 388)
$g.DrawString('コマンドパレット ・ ワークスペース ・ 縦タブ ・ 分割表示', $fSmall, $dim, 72, 446)
$g.DrawString('集中モード ・ リーダーモード ・ 広告ブロック', $fSmall, $dim, 72, 478)
$g.DrawString('Windows  /  WebView2  /  MIT', $fSmall, $dim, 72, 548)

# スクリーンショット（角丸 + 影）
$shot = [System.Drawing.Image]::FromFile((Join-Path $root 'docs\screenshots\split-reader.png'))
$sw = 640; $sh = [int]($shot.Height * $sw / $shot.Width)
$sx = 590; $sy = 170
$shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 0, 0, 0))
$g.FillRectangle($shadow, $sx + 10, $sy + 14, $sw, $sh)
$r = 14
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($sx, $sy, $r * 2, $r * 2, 180, 90)
$path.AddArc($sx + $sw - $r * 2, $sy, $r * 2, $r * 2, 270, 90)
$path.AddArc($sx + $sw - $r * 2, $sy + $sh - $r * 2, $r * 2, $r * 2, 0, 90)
$path.AddArc($sx, $sy + $sh - $r * 2, $r * 2, $r * 2, 90, 90)
$path.CloseFigure()
$g.SetClip($path)
$g.DrawImage($shot, $sx, $sy, $sw, $sh)
$g.ResetClip()

$g.Dispose()
$bmp.Save((Join-Path $root 'docs\social-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
"docs/social-preview.png を生成しました"
