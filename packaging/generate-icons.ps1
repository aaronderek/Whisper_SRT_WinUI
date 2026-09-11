<#
  generate-icons.ps1  —  从高分辨率源图生成 MSIX 全套图标资源
  源图：src\WhisperSubtitler\Asset\AppIcon.png (256x256，真实透明背景)
  输出：packaging\Images\ 下的 scale-* / targetsize-* / altform-unplated 变体
  说明：开始菜单/任务栏取 Square44x44Logo 的 targetsize-{16,24,32,48,256} 与
        altform-unplated 变体；缺失时系统只能拉伸 44px 位图导致模糊。
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$PackagingDir = $PSScriptRoot
$RepoRoot  = Split-Path -Parent $PackagingDir
$SourcePng = Join-Path $RepoRoot "src\WhisperSubtitler\Asset\AppIcon.png"
$OutDir    = Join-Path $PackagingDir "Images"

if (-not (Test-Path $SourcePng)) { throw "找不到源图 $SourcePng" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$src = [System.Drawing.Image]::FromFile($SourcePng)

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    return @($bmp, $g)
}

function Save-Square([string]$name, [int]$size) {
    $bmp, $g = New-Canvas $size $size
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# 宽幅/启动画面：透明画布上居中绘制，图标占高度的 75%
function Save-Wide([string]$name, [int]$w, [int]$h) {
    $bmp, $g = New-Canvas $w $h
    $iconSize = [int]($h * 0.75)
    $x = [int](($w - $iconSize) / 2)
    $y = [int](($h - $iconSize) / 2)
    $g.DrawImage($src, $x, $y, $iconSize, $iconSize)
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$scales = @{ 100 = 1.0; 125 = 1.25; 150 = 1.5; 200 = 2.0; 400 = 4.0 }

# ---- Square44x44Logo：scale 变体 + targetsize 变体（开始菜单/任务栏关键资源）----
foreach ($s in $scales.Keys) { Save-Square "Square44x44Logo.scale-$s.png" ([int](44 * $scales[$s])) }
foreach ($t in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    Save-Square "Square44x44Logo.targetsize-$t.png" $t
    Save-Square "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t
    Save-Square "Square44x44Logo.targetsize-${t}_altform-lightunplated.png" $t
}
Save-Square "Square44x44Logo.png" 44

# ---- 磁贴 ----
foreach ($s in $scales.Keys) {
    Save-Square "Square150x150Logo.scale-$s.png" ([int](150 * $scales[$s]))
    Save-Square "Square71x71Logo.scale-$s.png"   ([int](71  * $scales[$s]))
    Save-Square "Square310x310Logo.scale-$s.png" ([int](310 * $scales[$s]))
    Save-Square "StoreLogo.scale-$s.png"         ([int](50  * $scales[$s]))
    Save-Wide   "Wide310x150Logo.scale-$s.png"   ([int](310 * $scales[$s])) ([int](150 * $scales[$s]))
    Save-Wide   "SplashScreen.scale-$s.png"      ([int](620 * $scales[$s])) ([int](300 * $scales[$s]))
}
Save-Square "Square150x150Logo.png" 150
Save-Square "Square71x71Logo.png"   71
Save-Square "Square310x310Logo.png" 310
Save-Square "StoreLogo.png"         50
Save-Wide   "Wide310x150Logo.png"   310 150
Save-Wide   "SplashScreen.png"      620 300

$src.Dispose()
Write-Host "已生成 $((Get-ChildItem $OutDir -Filter *.png).Count) 个图标文件 -> $OutDir"
