# Renders designer.svg (repository root, the editable artwork) to designer.png with Microsoft Edge in
# headless mode, keeping the transparent background. Then run make-assets.ps1 to build the package
# images from designer.png.
#   powershell -ExecutionPolicy Bypass -File xbox-app\tools\render-designer.ps1
$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$svg  = Join-Path $root "designer.svg"
$png  = Join-Path $root "designer.png"

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge not found." }

# The SVG goes inline in a page with no margins, so the screenshot is exactly the 1536x1024 canvas.
$work = Join-Path $env:TEMP ("xisoburn-render-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null
$html = Join-Path $work "designer.html"
$out  = Join-Path $work "designer.png"
$page = "<!DOCTYPE html><html><head><meta charset=""utf-8""><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}svg{display:block}</style></head><body>" +
        [IO.File]::ReadAllText($svg).Substring([IO.File]::ReadAllText($svg).IndexOf("<svg")) + "</body></html>"
[IO.File]::WriteAllText($html, $page, (New-Object Text.UTF8Encoding $false))

$args = @(
    "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
    "--default-background-color=00000000", "--window-size=1536,1024",
    "--user-data-dir=$work\profile", "--screenshot=$out", ([Uri]$html).AbsoluteUri
)
$p = Start-Process -FilePath $edge -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
if (-not (Test-Path $out)) { throw "Edge did not write the screenshot (exit code $($p.ExitCode))." }

Copy-Item $out $png -Force
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Get-Item $png | Select-Object Name, Length
