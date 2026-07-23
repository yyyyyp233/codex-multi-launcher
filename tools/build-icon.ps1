param(
    [string]$ProjectRoot = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [single]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-SparkPath {
    param(
        [single]$CenterX,
        [single]$CenterY,
        [single]$OuterRadius,
        [single]$InnerRadius
    )

    $points = [System.Drawing.PointF[]]::new(8)
    for ($index = 0; $index -lt 8; $index++) {
        $radius = if (($index % 2) -eq 0) { $OuterRadius } else { $InnerRadius }
        $angle = (-90 + ($index * 45)) * [Math]::PI / 180
        $points[$index] = [System.Drawing.PointF]::new(
            [single]($CenterX + [Math]::Cos($angle) * $radius),
            [single]($CenterY + [Math]::Sin($angle) * $radius))
    }

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddPolygon($points)
    $path.CloseFigure()
    return $path
}

function New-LauncherMasterIcon {
    $size = 1024
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $tileRectangle = [System.Drawing.RectangleF]::new(42, 42, 940, 940)
        $tilePath = New-RoundedRectanglePath $tileRectangle 220
        try {
            $tileBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                $tileRectangle,
                [System.Drawing.ColorTranslator]::FromHtml('#23262B'),
                [System.Drawing.ColorTranslator]::FromHtml('#090A0C'),
                [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
            try {
                $graphics.FillPath($tileBrush, $tilePath)
            }
            finally {
                $tileBrush.Dispose()
            }

            $graphics.SetClip($tilePath)
            $glowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(74, 245, 138, 50))
            try {
                $graphics.FillEllipse($glowBrush, -180, -260, 1040, 850)
            }
            finally {
                $glowBrush.Dispose()
                $graphics.ResetClip()
            }

            $borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(220, 245, 138, 50), 20)
            try {
                $borderPen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset
                $graphics.DrawPath($borderPen, $tilePath)
            }
            finally {
                $borderPen.Dispose()
            }
        }
        finally {
            $tilePath.Dispose()
        }

        $shadowPath = New-SparkPath 514 536 284 76
        $mainPath = New-SparkPath 512 516 284 76
        try {
            $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(115, 0, 0, 0))
            $mainRectangle = [System.Drawing.RectangleF]::new(228, 232, 568, 568)
            $mainBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                $mainRectangle,
                [System.Drawing.ColorTranslator]::FromHtml('#FFA14F'),
                [System.Drawing.ColorTranslator]::FromHtml('#D96A1B'),
                [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
            try {
                $graphics.FillPath($shadowBrush, $shadowPath)
                $graphics.FillPath($mainBrush, $mainPath)
            }
            finally {
                $shadowBrush.Dispose()
                $mainBrush.Dispose()
            }
        }
        finally {
            $shadowPath.Dispose()
            $mainPath.Dispose()
        }

        $smallSpark = New-SparkPath 750 278 74 20
        try {
            $smallBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F4F4F2'))
            try {
                $graphics.FillPath($smallBrush, $smallSpark)
            }
            finally {
                $smallBrush.Dispose()
            }
        }
        finally {
            $smallSpark.Dispose()
        }

        return $bitmap
    }
    finally {
        $graphics.Dispose()
    }
}

function Assert-BlackOrangeIcon {
    param([System.Drawing.Bitmap]$Bitmap)

    $orangePixels = 0
    $purplePixels = 0
    for ($y = 0; $y -lt $Bitmap.Height; $y += 4) {
        for ($x = 0; $x -lt $Bitmap.Width; $x += 4) {
            $pixel = $Bitmap.GetPixel($x, $y)
            if ($pixel.A -lt 32) {
                continue
            }

            $hue = $pixel.GetHue()
            $saturation = $pixel.GetSaturation()
            $brightness = $pixel.GetBrightness()
            if ($hue -ge 250 -and $hue -le 330 -and
                $saturation -ge 0.20 -and $brightness -ge 0.16) {
                $purplePixels++
            }

            if ($hue -ge 10 -and $hue -le 55 -and
                $saturation -ge 0.45 -and $brightness -ge 0.25) {
                $orangePixels++
            }
        }
    }

    if ($purplePixels -gt 0) {
        throw "Generated icon contains $purplePixels purple-spectrum sample pixels."
    }

    if ($orangePixels -lt 3000) {
        throw "Generated icon does not contain enough orange pixels."
    }
}

function Convert-BitmapToPngBytes {
    param(
        [System.Drawing.Bitmap]$Master,
        [int]$Size
    )

    $scaled = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($scaled)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($Master, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))

        $stream = [System.IO.MemoryStream]::new()
        try {
            $scaled.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $scaled.Dispose()
    }
}

function Write-MultiSizeIcon {
    param(
        [System.Drawing.Bitmap]$Master,
        [string]$Destination
    )

    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = @($sizes | ForEach-Object { Convert-BitmapToPngBytes $Master $_ })
    $stream = [System.IO.FileStream]::new($Destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$images.Count)
        $offset = 6 + (16 * $images.Count)
        for ($index = 0; $index -lt $images.Count; $index++) {
            $size = $sizes[$index]
            $encodedSize = if ($size -eq 256) { 0 } else { $size }
            $writer.Write([byte]$encodedSize)
            $writer.Write([byte]$encodedSize)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$index].Length
        }

        foreach ($image in $images) {
            $writer.Write($image)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$assetDirectory = Join-Path $ProjectRoot 'Assets'
$artifactDirectory = Join-Path $ProjectRoot 'artifacts'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null

$master = New-LauncherMasterIcon
try {
    Assert-BlackOrangeIcon $master
    $iconPath = Join-Path $assetDirectory 'CodexChannelLauncher.ico'
    Write-MultiSizeIcon $master $iconPath
    $master.Save((Join-Path $artifactDirectory 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    foreach ($previewSize in @(16, 32, 64)) {
        [System.IO.File]::WriteAllBytes(
            (Join-Path $artifactDirectory "icon-preview-$previewSize.png"),
            (Convert-BitmapToPngBytes $master $previewSize))
    }
    Write-Host "Icon generated: $iconPath"
}
finally {
    $master.Dispose()
}
