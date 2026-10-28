# Generates the README banner: the logo next to the name, on a transparent background, in two
# colour variants so it reads on GitHub's light and dark themes:
#   docs/images/banner-light.png  dark lettering, for light pages
#   docs/images/banner-dark.png   light lettering, for dark pages
# Drawn at twice the displayed size so it stays sharp on high-DPI screens.
# Run with Windows PowerShell:  powershell -File assets/make-banner.ps1
. (Join-Path $PSScriptRoot 'logo.ps1')

$outDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\images'
New-Item -ItemType Directory -Force $outDir | Out-Null

$width = 1440; $height = 360
$logoSize = 300

function Pick-Font([string[]]$families, [float]$size, [System.Drawing.FontStyle]$style) {
    $installed = (New-Object System.Drawing.Text.InstalledFontCollection).Families.Name
    foreach ($f in $families) {
        if ($installed -contains $f) { return New-Object System.Drawing.Font $f, $size, $style, ([System.Drawing.GraphicsUnit]::Pixel) }
    }
    return New-Object System.Drawing.Font 'Arial', $size, $style, ([System.Drawing.GraphicsUnit]::Pixel)
}

function Draw-Banner([string]$path, $nameColor, $accentColor, $taglineColor) {
    $bmp = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'

    # Logo on the left, vertically centred.
    $state = $g.Save()
    $g.TranslateTransform(20, ($height - $logoSize) / 2)
    $g.ScaleTransform($logoSize / 256.0, $logoSize / 256.0)
    Draw-Logo $g
    $g.Restore($state)

    # The name in two lines: "WINDOWS" spaced out in the accent colour above a large
    # "Partition Manager", and a short tagline below.
    $x = 20 + $logoSize + 44
    $small = Pick-Font @('Segoe UI Variable Display Semib', 'Segoe UI Semibold', 'Segoe UI') 44 ([System.Drawing.FontStyle]::Regular)
    $large = Pick-Font @('Segoe UI Variable Display', 'Segoe UI') 124 ([System.Drawing.FontStyle]::Bold)
    $tagline = Pick-Font @('Segoe UI Variable Text', 'Segoe UI') 38 ([System.Drawing.FontStyle]::Regular)
    $format = [System.Drawing.StringFormat]::GenericTypographic

    $letterX = $x + 4
    foreach ($ch in 'WINDOWS'.ToCharArray()) {
        $g.DrawString([string]$ch, $small, (New-Object System.Drawing.SolidBrush $accentColor), $letterX, 52, $format)
        $letterX += $g.MeasureString([string]$ch, $small, 1000, $format).Width + 14
    }
    $g.DrawString('Partition Manager', $large, (New-Object System.Drawing.SolidBrush $nameColor), $x - 4, 98, $format)
    $g.DrawString('Free, open-source and safe partitioning for Windows 10 and 11', $tagline, (New-Object System.Drawing.SolidBrush $taglineColor), $x, 262, $format)

    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "Wrote $path"
}

Draw-Banner (Join-Path $outDir 'banner-light.png') (C 0x17 0x24 0x38) (C 0x1D 0x4E 0xD8) (C 0x4B 0x55 0x63)
Draw-Banner (Join-Path $outDir 'banner-dark.png') (C 0xF3 0xF6 0xFA) (C 0x6F 0xB1 0xFF) (C 0xAE 0xB8 0xC6)
