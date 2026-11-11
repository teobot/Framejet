# Renders the SVG sources in this folder to Framejet.ico and the logo / social-preview PNGs.
# Needs Windows PowerShell 5.1 and Microsoft Edge (it screenshots each SVG in headless mode).
#   powershell -ExecutionPolicy Bypass -File art\render.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$art = $PSScriptRoot
$repo = Split-Path $art -Parent
$work = Join-Path $env:TEMP 'framejet-art'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null

# 1. Find Edge.
$edge = $null
foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
    if (-not $root) { continue }
    $p = Join-Path $root 'Microsoft\Edge\Application\msedge.exe'
    if (Test-Path $p) { $edge = $p; break }
}
if (-not $edge) { throw 'msedge.exe not found under either Program Files folder.' }

# 2-5. Renders one SVG at W x H and saves a PNG through System.Drawing (no text chunks).
function Render-Svg([string]$svg, [int]$w, [int]$h, [string]$png) {
    $page = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($png) + '.html')
    $uri = ([Uri](Resolve-Path $svg).Path).AbsoluteUri
    $html = '<!doctype html><html><body style="margin:0;background:transparent;overflow:hidden">' +
            '<img src="' + $uri + '" width="' + $w + '" height="' + $h + '" style="position:absolute;left:0;top:0;display:block"></body></html>'
    [IO.File]::WriteAllText($page, $html, (New-Object Text.UTF8Encoding $false))

    $raw = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($png) + '.raw.png')
    $profile = Join-Path $work ('profile-' + [Guid]::NewGuid().ToString('N'))
    $ww = [Math]::Max($w, 300); $wh = [Math]::Max($h, 300)
    $edgeArgs = @('--headless', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
                  '--default-background-color=00000000', "--user-data-dir=$profile",
                  "--window-size=$ww,$wh", "--screenshot=$raw", ([Uri]$page).AbsoluteUri)
    $proc = Start-Process -FilePath $edge -ArgumentList $edgeArgs -PassThru -WindowStyle Hidden
    if (-not $proc.WaitForExit(60000)) { $proc.Kill(); throw "Edge timed out rendering $svg" }
    for ($i = 0; $i -lt 50 -and -not (Test-Path $raw); $i++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path $raw)) { throw "Edge produced no screenshot for $svg" }
    Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue

    $src = New-Object Drawing.Bitmap($raw)
    try {
        $dst = New-Object Drawing.Bitmap($w, $h, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [Drawing.Graphics]::FromImage($dst)
        $g.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.DrawImage($src, (New-Object Drawing.Rectangle(0, 0, $w, $h)), (New-Object Drawing.Rectangle(0, 0, $w, $h)), [Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()
        $dst.Save($png, [Drawing.Imaging.ImageFormat]::Png)
        $dst.Dispose()
    } finally { $src.Dispose() }
    Remove-Item $raw, $page -Force
}

# Icon frames: 16 and 20 from the pixel-drawn icon-16.svg, 24 from the pixel-drawn icon-24.svg; the rest from icon.svg.
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
foreach ($s in $sizes) {
    $svg = if ($s -le 20) { 'icon-16.svg' } elseif ($s -eq 24) { 'icon-24.svg' } else { 'icon.svg' }
    Render-Svg (Join-Path $art $svg) $s $s (Join-Path $work "icon-$s.png")
}
Render-Svg (Join-Path $art 'logo-dark.svg') 642 192 (Join-Path $art 'logo-dark.png')
Render-Svg (Join-Path $art 'logo-light.svg') 642 192 (Join-Path $art 'logo-light.png')
Render-Svg (Join-Path $art 'social-preview.svg') 1280 640 (Join-Path $art 'social-preview.png')
Copy-Item (Join-Path $art 'logo-dark.png'), (Join-Path $art 'logo-light.png'), (Join-Path $art 'social-preview.png') $work -Force

# 6. Write the .ico, entries in ascending size.
function New-DibEntry([string]$png, [int]$n) {
    $bmp = New-Object Drawing.Bitmap($png)
    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$n); $bw.Write([int](2 * $n))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    $maskStride = [int]([Math]::Ceiling($n / 32.0) * 4)
    $mask = New-Object 'byte[]' ($maskStride * $n)
    for ($y = $n - 1; $y -ge 0; $y--) {                       # bottom-up rows
        for ($x = 0; $x -lt $n; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            if ($c.A -eq 0) {
                $row = $n - 1 - $y
                $mask[$row * $maskStride + ($x -shr 3)] = $mask[$row * $maskStride + ($x -shr 3)] -bor (0x80 -shr ($x -band 7))
            }
        }
    }
    $bw.Write($mask)
    $bw.Flush(); $bmp.Dispose()
    return , $ms.ToArray()
}
$entries = @()
foreach ($s in $sizes) {
    $png = Join-Path $work "icon-$s.png"
    $data = if ($s -eq 256) { , [IO.File]::ReadAllBytes($png) } else { New-DibEntry $png $s }
    $entries += , @{ Size = $s; Data = $data }
}
$ico = Join-Path $repo 'src\Framejet\Framejet.ico'
$fs = [IO.File]::Create($ico)
$w = New-Object IO.BinaryWriter($fs)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$e.Data.Length); $w.Write([int]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $w.Write([byte[]]$e.Data) }
$w.Flush(); $w.Close()

# 7.
'Framejet.ico: ' + (($entries | ForEach-Object { $_.Size }) -join ' ')
