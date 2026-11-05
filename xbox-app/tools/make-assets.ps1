# Builds the UWP package images from the artwork in designer.png (repository root).
# designer.png holds two pieces on a transparent background:
#   - left:  the app icon (rounded square)      -> tiles, store logo, app list icon
#   - right: the logo (disc + "xISOBurn" + tagline) -> wide tile, splash screen, in-app banner
# Uses System.Drawing from Windows PowerShell 5.1; nothing to install.
#   powershell -ExecutionPolicy Bypass -File xbox-app\tools\make-assets.ps1
Add-Type -AssemblyName System.Drawing

$root   = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$source = Join-Path $root "designer.png"
$dest   = Join-Path $PSScriptRoot "..\xISOBurn\Assets"
New-Item -ItemType Directory -Force $dest | Out-Null

$art = [System.Drawing.Bitmap]::FromFile($source)

# Crop rectangles found from the alpha channel (opaque area plus a margin for the green glow).
$iconRect = New-Object System.Drawing.Rectangle 38, 218, 508, 528
$logoRect = New-Object System.Drawing.Rectangle 560, 286, 964, 392

function Crop([System.Drawing.Rectangle]$r) {
    $bmp = New-Object System.Drawing.Bitmap $r.Width, $r.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.DrawImage($art, (New-Object System.Drawing.Rectangle 0, 0, $r.Width, $r.Height), $r, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    return $bmp
}

# Draws $img scaled to fit inside a W x H canvas (keeping proportions), centered, with a margin.
# Transparent background: the tile/splash color comes from BackgroundColor in the manifest.
function Render($img, [int]$w, [int]$h, [double]$margin, [string]$file) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $availW = $w * (1 - 2 * $margin); $availH = $h * (1 - 2 * $margin)
    $scale = [Math]::Min($availW / $img.Width, $availH / $img.Height)
    $dw = [int][Math]::Round($img.Width * $scale); $dh = [int][Math]::Round($img.Height * $scale)
    $dx = [int](($w - $dw) / 2); $dy = [int](($h - $dh) / 2)
    $attr = New-Object System.Drawing.Imaging.ImageAttributes
    $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY) # no dark fringe at the edges
    $g.DrawImage($img, (New-Object System.Drawing.Rectangle $dx, $dy, $dw, $dh), 0, 0, $img.Width, $img.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
    $g.Dispose()
    $bmp.Save((Join-Path $dest $file), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$icon = Crop $iconRect
$logo = Crop $logoRect

# Old single-scale files are replaced by scale-qualified ones (MRT picks the right one).
Get-ChildItem $dest -Filter *.png | Remove-Item

# name, base width, base height, artwork, margin. Each one gets scale-100 and scale-200
# (the Xbox runs UWP apps at 200% on a 1080p screen, so scale-200 is the one it uses).
$items = @(
    @{ Name = "Square44x44Logo";   W = 44;  H = 44;  Art = $icon; Margin = 0.0  },
    @{ Name = "StoreLogo";         W = 50;  H = 50;  Art = $icon; Margin = 0.0  },
    @{ Name = "Square150x150Logo"; W = 150; H = 150; Art = $icon; Margin = 0.04 },
    @{ Name = "Wide310x150Logo";   W = 310; H = 150; Art = $logo; Margin = 0.06 },
    @{ Name = "SplashScreen";      W = 620; H = 300; Art = $logo; Margin = 0.08 }
)
foreach ($i in $items) {
    foreach ($s in 100, 200) {
        Render $i.Art ([int]($i.W * $s / 100)) ([int]($i.H * $s / 100)) $i.Margin ("{0}.scale-{1}.png" -f $i.Name, $s)
    }
}

# Unplated icons for the app list (no tile plate behind them).
foreach ($t in 16, 24, 32, 48, 256) {
    Render $icon $t $t 0.0 ("Square44x44Logo.targetsize-{0}_altform-unplated.png" -f $t)
}

# In-app banner shown at the top of the main page (full resolution of the logo).
$logo.Save((Join-Path $dest "Banner.png"), [System.Drawing.Imaging.ImageFormat]::Png)

$icon.Dispose(); $logo.Dispose(); $art.Dispose()
Get-ChildItem $dest | Select-Object Name, Length
