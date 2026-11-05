# Gera os ícones/splash obrigatórios do pacote UWP (PNG simples, texto sobre fundo escuro).
# Usa System.Drawing do Windows PowerShell 5.1; não precisa instalar nada.
#   powershell -ExecutionPolicy Bypass -File xbox-app\tools\gerar-assets.ps1
Add-Type -AssemblyName System.Drawing

$dest = Join-Path $PSScriptRoot "..\xISOBurn\Assets"
New-Item -ItemType Directory -Force $dest | Out-Null

$items = @(
    @{ Name = "Square44x44Logo.png";   W = 44;  H = 44;  Text = "xI" },
    @{ Name = "StoreLogo.png";         W = 50;  H = 50;  Text = "xI" },
    @{ Name = "Square150x150Logo.png"; W = 150; H = 150; Text = "xISO" },
    @{ Name = "Wide310x150Logo.png";   W = 310; H = 150; Text = "xISOBurn" },
    @{ Name = "SplashScreen.png";      W = 620; H = 300; Text = "xISOBurn" }
)

foreach ($i in $items) {
    $bmp = New-Object System.Drawing.Bitmap $i.W, $i.H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $g.TextRenderingHint = "AntiAliasGridFit"
    $g.Clear([System.Drawing.Color]::FromArgb(30, 30, 30))
    $size = [Math]::Max(8, [Math]::Min($i.H * 0.45, $i.W / ($i.Text.Length * 0.62)))
    $font = New-Object System.Drawing.Font "Segoe UI", $size, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(16, 124, 16))
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = "Center"; $fmt.LineAlignment = "Center"
    $g.DrawString($i.Text, $font, $brush, (New-Object System.Drawing.RectangleF 0, 0, $i.W, $i.H), $fmt)
    $bmp.Save((Join-Path $dest $i.Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
Get-ChildItem $dest | Select-Object Name, Length
