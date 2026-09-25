param([string]$Native, [string]$OutDir, [switch]$Fit)
# Makes Flow Launcher's icon (Flow.Launcher/Resources/app.ico, Images/app.ico, Images/app.png):
#   powershell -File Scripts/search-icon.ps1 -OutDir <folder>
# -Fit -Native <screenshot> refits the shape to a 150% taskbar screenshot with the search icon at 91,75.
#
# Draws the Windows 10 taskbar search magnifier as vectors, measured from a 150% screenshot of the taskbar
# (33 x 33 px glyph): a ring with its centerline radius 11.4 and stroke 3.6, and a 45 degree handle of the same
# stroke with a round end. Coordinates are in a 36 unit box (the taskbar icon size at 150%), the glyph 1.5 in.
Add-Type -AssemblyName System.Drawing, PresentationCore, PresentationFramework, WindowsBase

function Render($g, [int]$size, [double]$offset) {
    $scale = $size / 36.0
    $dv = New-Object Windows.Media.DrawingVisual; $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object Windows.Media.MatrixTransform $scale, 0, 0, $scale, ($offset * $scale), ($offset * $scale)))
    $pen = New-Object Windows.Media.Pen ([Windows.Media.Brushes]::White), $g.stroke
    $dc.DrawEllipse($null, $pen, (New-Object Windows.Point $g.cx, $g.cy), $g.r, $g.r)
    $handle = New-Object Windows.Media.Pen ([Windows.Media.Brushes]::White), $g.stroke
    $handle.StartLineCap = 'Flat'; $handle.EndLineCap = 'Round'
    $sx = $g.cx - $g.r * [Math]::Sqrt(0.5); $sy = $g.cy + $g.r * [Math]::Sqrt(0.5)
    $dc.DrawLine($handle, (New-Object Windows.Point $sx, $sy), (New-Object Windows.Point $g.ex, $g.ey))
    $dc.Pop(); $dc.Close()
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv); $rtb.Freeze()
    return $rtb
}

function Alpha($bmp) { $n = $bmp.PixelWidth; $p = New-Object byte[] ($n * $n * 4); $bmp.CopyPixels($p, $n * 4, 0); $p }

# Fitted to the native pixels (-Fit): 13.5 of the glyph's 312 px of coverage differ
$glyph = @{ cx = 21.1; cy = 14.8; r = 11.6; stroke = 3.6; ex = 3.5; ey = 32.5 }

if (-not $Fit) {
    # An .ico with every size Windows asks for at 100% to 300% scaling (32-bit bitmaps, and PNG for 256), and a
    # 256 px PNG
    $sizes = 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256
    $frames = foreach ($size in $sizes) {
        $bmp = Render $glyph $size 0
        $p = Alpha $bmp
        if ($size -eq 256) {
            $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
            $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
            $ms = New-Object IO.MemoryStream; $enc.Save($ms)
            [IO.File]::WriteAllBytes((Join-Path $OutDir 'app.png'), $ms.ToArray())
            @{ Size = $size; Bytes = $ms.ToArray() }
            continue
        }
        # BITMAPINFOHEADER, bottom-up straight-alpha BGRA (white, so the color is simply white where there is ink),
        # then an empty AND mask
        $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
        $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
        $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $al = $p[($y * $size + $x) * 4 + 3]
                $c = if ($al -gt 0) { 255 } else { 0 }
                $w.Write([byte]$c); $w.Write([byte]$c); $w.Write([byte]$c); $w.Write([byte]$al)
            }
        }
        $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
        $w.Write((New-Object byte[] ($maskRow * $size)))
        $w.Flush()
        @{ Size = $size; Bytes = $ms.ToArray() }
    }

    $ico = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ico
    $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$f.Bytes.Length); $w.Write([int]$offset)
        $offset += $f.Bytes.Length
    }
    foreach ($f in $frames) { $w.Write($f.Bytes) }
    $w.Flush()
    [IO.File]::WriteAllBytes((Join-Path $OutDir 'app.ico'), $ico.ToArray())
    "wrote app.ico ($($frames.Count) sizes) and app.png to $OutDir"
    return
}

if ($Fit) {
    # Compare with the native pixels (33 x 33 at 91,75 over #202020), the 36 box starting 1.5 above-left of that
    $a = [Drawing.Bitmap]::FromFile($Native)
    function Get-GlyphDiff($g) {
        $bmp = Render $g 36 -1.5    # shift so the 33 px glyph box starts at 0,0
        $p = Alpha $bmp; $d = 0.0
        for ($x = 0; $x -lt 33; $x++) { for ($y = 0; $y -lt 33; $y++) {
            $nat = [Math]::Max(0, $a.GetPixel(91 + $x, 75 + $y).R - 0x20) / (255.0 - 0x20)
            $d += [Math]::Abs($p[($y * 36 + $x) * 4 + 3] / 255.0 - $nat) } }
        [Math]::Round($d, 1)
    }
    "start: " + (Get-GlyphDiff $glyph)
    # Small search over each parameter, a few passes
    foreach ($pass in 1..3) {
        foreach ($k in 'cx', 'cy', 'r', 'stroke', 'ex', 'ey') {
            $best = Get-GlyphDiff $glyph; $bestV = $glyph[$k]
            foreach ($delta in -0.4, -0.2, -0.1, 0.1, 0.2, 0.4) {
                $g2 = $glyph.Clone(); $g2[$k] = $glyph[$k] + $delta; $dd = Get-GlyphDiff $g2
                if ($dd -lt $best) { $best = $dd; $bestV = $g2[$k] }
            }
            $glyph[$k] = $bestV
        }
        "pass $pass : " + (Get-GlyphDiff $glyph) + "  " + (($glyph.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$([Math]::Round($_.Value, 2))" }) -join ' ')
    }
    return
}
