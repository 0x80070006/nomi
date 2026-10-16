# Renders Nomi's head (simplified from mascot.svg so it stays readable at 16 px) into a multi-size .ico.
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function New-Mark([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $ink = C '#263342'; $cream = C '#E8DED0'; $orange = C '#F5A623'
    $small = $s -le 24
    $line = [Math]::Max(1.0, $s * 0.045)

    # Antenna (only where there is room for it)
    $top = 0.0
    if (-not $small) {
        $top = 0.13 * $s
        $pen = New-Object System.Drawing.Pen($ink, [Math]::Max(1.5, $s * 0.045))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawLine($pen, 0.5 * $s, 0.22 * $s, 0.53 * $s, 0.1 * $s)
        $r = 0.065 * $s
        $g.FillEllipse((New-Object System.Drawing.SolidBrush($orange)), 0.535 * $s - $r, 0.075 * $s - $r, 2 * $r, 2 * $r)
        $g.DrawEllipse((New-Object System.Drawing.Pen($ink, [Math]::Max(1, $s * 0.025))), 0.535 * $s - $r, 0.075 * $s - $r, 2 * $r, 2 * $r)
    }

    # Head shell
    $hx = $line / 2 + 0.02 * $s; $hy = $top + $line / 2 + ($(if ($small) { 0.06 * $s } else { 0.06 * $s }))
    $hw = $s - 2 * $hx; $hh = $s - $hy - $line / 2 - 0.04 * $s
    $head = New-RoundedRect $hx $hy $hw $hh (0.3 * $hw)
    $g.FillPath((New-Object System.Drawing.SolidBrush($cream)), $head)
    $g.DrawPath((New-Object System.Drawing.Pen($ink, $line)), $head)

    # Face screen
    $fx = $hx + 0.13 * $hw; $fy = $hy + 0.2 * $hh; $fw = 0.74 * $hw; $fh = 0.6 * $hh
    $g.FillPath((New-Object System.Drawing.SolidBrush($ink)), (New-RoundedRect $fx $fy $fw $fh (0.38 * $fh)))

    # Eyes
    $ew = 0.15 * $fw; $eh = 0.42 * $fh
    $ey = $fy + 0.25 * $fh
    $orangeBrush = New-Object System.Drawing.SolidBrush($orange)
    $g.FillEllipse($orangeBrush, $fx + 0.27 * $fw - $ew / 2, $ey, $ew, $eh)
    $g.FillEllipse($orangeBrush, $fx + 0.73 * $fw - $ew / 2, $ey, $ew, $eh)

    # Smile (not at the smallest sizes)
    if ($s -ge 32) {
        $mp = New-Object System.Drawing.Pen($orange, [Math]::Max(1, $s * 0.022))
        $mp.StartCap = 'Round'; $mp.EndCap = 'Round'
        $g.DrawArc($mp, $fx + 0.42 * $fw, $fy + 0.62 * $fh, 0.16 * $fw, 0.14 * $fh, 20, 140)
    }
    $g.Dispose()
    $bmp
}

# Classic DIB entry (BITMAPINFOHEADER + bottom-up BGRA + AND mask), readable everywhere.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush()
    $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Mark $s
    if ($s -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    } else {
        , (Get-DibBytes $bmp)
    }
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$data.Length); $w.Write([int]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write([byte[]]$data) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Path, $out.ToArray())

# PNG previews next to the icon, to check the small sizes by eye.
foreach ($s in 16, 32, 256) {
    $preview = New-Mark $s
    $preview.Save([System.IO.Path]::ChangeExtension($Path, ".$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $preview.Dispose()
}
