# Generates assets/app.ico: a disk platter snapped in two like a cookie, with a jagged break,
# the halves pulled apart and a few crumbs below. Drawn once on a 256x256 canvas and scaled to
# every icon size. Run with Windows PowerShell:  powershell -File assets/make-icon.ps1 [-Preview x.png]
param([string]$Preview)

$sizes = 16, 24, 32, 48, 64, 128, 256
$out = Join-Path $PSScriptRoot 'app.ico'
. (Join-Path $PSScriptRoot 'logo.ps1')

$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($s / 256.0, $s / 256.0)
    Draw-Logo $g
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # Large frame: PNG, as Windows expects for 256 px.
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($Preview) { $bmp.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png) }
    }
    else {
        # Small frames: classic 32-bit DIB (bottom-up BGRA + AND mask) so every tool can read them.
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $bw.Write([UInt32]($s * $s * 4)); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskStride = [Math]::Ceiling($s / 32) * 4
        $bw.Write((New-Object byte[] ($maskStride * $s)))
        $bw.Flush()
    }

    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
}

# ICO container.
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$img.Bytes.Length); $w.Write([UInt32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write($img.Bytes) }
$w.Close()
"Wrote $out"
