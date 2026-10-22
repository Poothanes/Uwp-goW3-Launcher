$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$output = Join-Path (Split-Path $PSScriptRoot -Parent) 'artwork'
New-Item -ItemType Directory -Path $output -Force | Out-Null

foreach ($item in @(
    @('Logo.png', 150, 150),
    @('SmallLogo.png', 44, 44),
    @('StoreLogo.png', 50, 50),
    @('SplashScreen.png', 620, 300)
)) {
    $name, $width, $height = $item
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $background = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(26, 14, 14))
    $crimson = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(184, 41, 41))
    $goldPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(229, 169, 60), 3)
    try {
        $graphics.FillRectangle($background, 0, 0, $width, $height)
        $side = [Math]::Min($width, $height) * 0.66
        $left = ($width - $side) / 2
        $top = ($height - $side) / 2
        $graphics.FillRectangle($crimson, [single]$left, [single]$top, [single]$side, [single]$side)
        $graphics.DrawRectangle($goldPen, [single]$left, [single]$top, [single]$side, [single]$side)

        # Draw Stylized Omega / III emblem
        $barW = [Math]::Max(2, [int]($side * 0.11))
        $barH = [int]($side * 0.52)
        $barTop = [int]($top + ($side - $barH) / 2)
        for ($i = -1; $i -le 1; $i++) {
            $bx = [int]($width / 2 + $i * ($barW * 1.65) - $barW / 2)
            $graphics.FillRectangle([System.Drawing.Brushes]::White, $bx, $barTop, $barW, $barH)
        }
        $bitmap.Save((Join-Path $output $name), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $goldPen.Dispose()
        $crimson.Dispose()
        $background.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}
Write-Output "Generated God of War III Remastered UWP icons in $output"
