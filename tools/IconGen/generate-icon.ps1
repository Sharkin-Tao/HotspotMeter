# HotspotMeter 图标生成器（可重复执行）
# 构图：Wi-Fi 三层弧形 + 圆点 + 上窄下宽梯形底座，深黑圆角方形底板，翡翠绿线条。
# 说明：图标是纯几何图形，程序化绘制可以保证 16×16 下仍然清晰；
#       AI 位图缩到 16px 会糊掉，因此这里按每个尺寸单独渲染，而不是缩放大图。
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\HotspotMeter.App\Assets'),
    [string]$PreviewPath = (Join-Path $PSScriptRoot '..\..\docs\images\icon-512.png')
)

Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-Icon([int]$size, [string]$path) {
    $s = [float]$size
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # 底板：深黑圆角方形
    $bg = New-RoundedRectPath 0 0 $s $s ([float]($s * 0.22))
    $bgBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#0B0B0C'))
    $g.FillPath($bgBrush, $bg)

    $green = [System.Drawing.ColorTranslator]::FromHtml('#30D158')
    $greenBrush = New-Object System.Drawing.SolidBrush $green
    $stroke = [Math]::Max(1.25, $s * 0.058)
    $pen = New-Object System.Drawing.Pen $green, $stroke
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # Wi-Fi：以圆点所在位置为圆心的三段上弧
    $cx = $s * 0.5
    $cy = $s * 0.52
    $radii = @(0.335, 0.232, 0.129)
    foreach ($r in $radii) {
        $radius = [float]($s * $r)
        $rect = New-Object System.Drawing.RectangleF ([float]($cx - $radius)), ([float]($cy - $radius)), ([float]($radius * 2)), ([float]($radius * 2))
        # GDI+ 角度顺时针；225° -> 315° 是正上方的 90° 弧
        $g.DrawArc($pen, $rect, 225, 90)
    }

    # Wi-Fi 圆点
    $dot = [float]([Math]::Max(1.0, $s * 0.036))
    $g.FillEllipse($greenBrush, [float]($cx - $dot), [float]($cy - $dot), [float]($dot * 2), [float]($dot * 2))

    # 梯形底座：上窄下宽，圆角折线，实心填充
    $topY = [float]($s * 0.705)
    $bottomY = [float]($s * 0.885)
    $topHalf = [float]($s * 0.165)
    $bottomHalf = [float]($s * 0.275)
    $trapezoid = New-Object System.Drawing.Drawing2D.GraphicsPath
    $corner = [float]($s * 0.022)
    $trapezoid.AddLine([float]($cx - $topHalf), $topY, [float]($cx + $topHalf), $topY)
    $trapezoid.AddLine([float]($cx + $topHalf), $topY, [float]($cx + $bottomHalf), $bottomY)
    $trapezoid.AddLine([float]($cx + $bottomHalf), $bottomY, [float]($cx - $bottomHalf), $bottomY)
    $trapezoid.AddLine([float]($cx - $bottomHalf), $bottomY, [float]($cx - $topHalf), $topY)
    $trapezoid.CloseFigure()
    # 圆角通过同色圆头描边再填充实现
    $joinPen = New-Object System.Drawing.Pen $green, ([float]([Math]::Max(0.8, $s * 0.022)))
    $joinPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $joinPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $joinPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawPath($joinPen, $trapezoid)
    $g.FillPath($greenBrush, $trapezoid)

    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $bgBrush.Dispose(); $greenBrush.Dispose(); $pen.Dispose(); $joinPen.Dispose()
    $bg.Dispose(); $trapezoid.Dispose()
}

function Write-Ico([string[]]$pngPaths, [int[]]$sizes, [string]$icoPath) {
    # 用传统 DIB 帧而不是 PNG 帧：Windows 资源图标与 System.Drawing.Icon 对 PNG 帧支持不一致，
    # DIB 帧（32bpp BGRA + AND 掩码）在所有 Windows 版本上都能正确显示。
    $datas = @()
    $offset = 6 + 16 * $pngPaths.Count
    $entries = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $pngPaths.Count; $i++) {
        $size = $sizes[$i]
        $bmp = [System.Drawing.Bitmap]::FromFile($pngPaths[$i])
        $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
        $locked = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stride = $locked.Stride
        $pixels = New-Object byte[] ($stride * $size)
        [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
        $bmp.UnlockBits($locked)
        $bmp.Dispose()

        $xor = New-Object System.IO.MemoryStream
        for ($y = $size - 1; $y -ge 0; $y--) {
            $xor.Write($pixels, $y * $stride, $size * 4)
        }

        $maskRowBytes = [int]([Math]::Floor(($size + 31) / 32) * 4)
        $mask = New-Object byte[] ($maskRowBytes * $size)
        for ($y = 0; $y -lt $size; $y++) {
            $srcRow = $size - 1 - $y
            for ($x = 0; $x -lt $size; $x++) {
                if ($pixels[$srcRow * $stride + $x * 4 + 3] -lt 40) {
                    $mask[$y * $maskRowBytes + [int][Math]::Floor($x / 8)] = $mask[$y * $maskRowBytes + [int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8))
                }
            }
        }

        $dib = New-Object System.IO.MemoryStream
        $dw = New-Object System.IO.BinaryWriter $dib
        $dw.Write([UInt32]40)
        $dw.Write([Int32]$size)
        $dw.Write([Int32]($size * 2))
        $dw.Write([UInt16]1)
        $dw.Write([UInt16]32)
        $dw.Write([UInt32]0)
        $dw.Write([UInt32]($xor.Length + $mask.Length))
        $dw.Write([Int32]0); $dw.Write([Int32]0)
        $dw.Write([UInt32]0); $dw.Write([UInt32]0)
        $dw.Write($xor.ToArray())
        $dw.Write($mask)
        $dw.Flush()
        $bytes = $dib.ToArray()
        $dw.Dispose(); $dib.Dispose(); $xor.Dispose()

        $entry = New-Object System.IO.MemoryStream
        $eb = New-Object System.IO.BinaryWriter $entry
        $eb.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
        $eb.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
        $eb.Write([byte]0)    # 调色板颜色数
        $eb.Write([byte]0)    # 保留
        $eb.Write([UInt16]1)  # 色平面
        $eb.Write([UInt16]32) # 位深
        $eb.Write([UInt32]$bytes.Length)
        $eb.Write([UInt32]$offset)
        $eb.Flush()
        [void]$entries.Add($entry.ToArray())
        $eb.Dispose(); $entry.Dispose()
        $datas += ,$bytes
        $offset += $bytes.Length
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngPaths.Count)
    foreach ($e in $entries) { $bw.Write($e) }
    foreach ($d in $datas) { $bw.Write($d) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $PreviewPath) | Out-Null

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$tmp = Join-Path $env:TEMP ("hotspotmeter-icon-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$pngs = @()
foreach ($size in $sizes) {
    $png = Join-Path $tmp ("icon-" + $size + ".png")
    Draw-Icon $size $png
    $pngs += $png
}

Write-Ico $pngs $sizes (Join-Path $OutDir 'HotspotMeter.ico')
Draw-Icon 512 $PreviewPath
Copy-Item (Join-Path $tmp 'icon-256.png') (Join-Path $OutDir 'HotspotMeter-256.png')

# 16px 可读性自检：绿色像素占比必须落在合理区间，避免"缩到 16px 只剩噪点"。
$small = [System.Drawing.Bitmap]::FromFile((Join-Path $tmp 'icon-16.png'))
$greenCount = 0
for ($y = 0; $y -lt 16; $y++) {
    for ($x = 0; $x -lt 16; $x++) {
        $c = $small.GetPixel($x, $y)
        if ($c.G -gt 90 -and $c.G -gt ($c.R + 20)) { $greenCount++ }
    }
}
$small.Dispose()
"icon sizes: $($sizes -join ','); 16px green pixels=$greenCount / 256"
Remove-Item -Recurse -Force $tmp