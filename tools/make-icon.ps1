# Generates app.ico — a 2x2 "video wall" icon matching MpvGrid's 4-feed layout.
# Dark rounded panel with four rounded cells (orange/blue accents) on a near-black ground.
# Emits a multi-resolution ICO (16/20/24/32/40/48/64/128/256) with PNG-compressed entries.
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x,        $y,        $d, $d, 180, 90)
    $p.AddArc($x+$w-$d,  $y,        $d, $d, 270, 90)
    $p.AddArc($x+$w-$d,  $y+$h-$d,  $d, $d, 0,   90)
    $p.AddArc($x,        $y+$h-$d,  $d, $d, 90,  90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    # Outer rounded panel
    $pad = [Math]::Max(1.0, $s * 0.06)
    $outerR = $s * 0.18
    $panel = New-RoundedPath $pad $pad ($s - 2*$pad) ($s - 2*$pad) $outerR
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 24, 26, 31))
    $g.FillPath($bg, $panel)

    # Four cells (2x2). Bright accents so it reads even at 16px.
    $gap = [Math]::Max(1.0, $s * 0.07)
    $inset = $pad + [Math]::Max(1.0, $s * 0.10)
    $area = $s - 2*$inset
    $cell = ($area - $gap) / 2.0
    $cellR = [Math]::Max(1.0, $cell * 0.22)

    $colors = @(
        [System.Drawing.Color]::FromArgb(255, 255, 138, 0),   # orange
        [System.Drawing.Color]::FromArgb(255, 64, 156, 255),  # blue
        [System.Drawing.Color]::FromArgb(255, 80, 200, 120),  # green
        [System.Drawing.Color]::FromArgb(255, 255, 92, 92)    # red
    )
    $xs = @($inset, ($inset+$cell+$gap), $inset, ($inset+$cell+$gap))
    $ys = @($inset, $inset, ($inset+$cell+$gap), ($inset+$cell+$gap))
    for ($i = 0; $i -lt 4; $i++) {
        $px = [float]$xs[$i]
        $py = [float]$ys[$i]
        $path = New-RoundedPath $px $py $cell $cell $cellR
        $br = New-Object System.Drawing.SolidBrush $colors[$i]
        $g.FillPath($br, $path)
        $br.Dispose()
        $path.Dispose()
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16,20,24,32,40,48,64,128,256
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,($ms.ToArray())
    $bmp.Dispose()
    $ms.Dispose()
}

# Assemble ICO: 6-byte header + 16-byte dir entries + PNG payloads.
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)            # reserved
$bw.Write([UInt16]1)            # type = icon
$bw.Write([UInt16]$sizes.Count) # image count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([Byte]$dim)       # width  (0 => 256)
    $bw.Write([Byte]$dim)       # height (0 => 256)
    $bw.Write([Byte]0)          # palette
    $bw.Write([Byte]0)          # reserved
    $bw.Write([UInt16]1)        # color planes
    $bw.Write([UInt16]32)       # bits per pixel
    $bw.Write([UInt32]$pngs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Flush()

$target = Join-Path $PSScriptRoot '..\app.ico'
[System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath (Split-Path $target)).Path + '\app.ico', $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host "Wrote app.ico ($($sizes.Count) sizes, $([Math]::Round((Get-Item (Join-Path (Split-Path $target) 'app.ico')).Length/1kb,1)) KB)"
