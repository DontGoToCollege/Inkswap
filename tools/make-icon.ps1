# Draws Inkswap's icon (a speech bubble with あ turning into A) and writes src\Inkswap\app.ico.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAlias'
    $k = $s / 256
    $bubble = New-Object Drawing.Drawing2D.GraphicsPath
    $bubble.AddEllipse(12 * $k, 20 * $k, 232 * $k, 180 * $k)
    $bubble.AddPolygon([Drawing.PointF[]]@((New-Object Drawing.PointF (60 * $k), (170 * $k)), (New-Object Drawing.PointF (36 * $k), (244 * $k)), (New-Object Drawing.PointF (110 * $k), (190 * $k))))
    $bubble.FillMode = 'Winding'
    # outline drawn double width and then filled over, so the tail merges into the bubble
    $g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(14, 17, 22)), ([Math]::Max(2, 24 * $k))), $bubble)
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::White)), $bubble)
    $sf = New-Object Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $f1 = New-Object Drawing.Font "Yu Gothic", (88 * $k), ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
    $f2 = New-Object Drawing.Font "Arial Black", (92 * $k), ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
    $g.DrawString("あ", $f1, (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(227, 168, 87))), (New-Object Drawing.RectangleF (16 * $k), (24 * $k), (120 * $k), (170 * $k)), $sf)
    $g.DrawString("A", $f2, (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(79, 156, 249))), (New-Object Drawing.RectangleF (120 * $k), (24 * $k), (120 * $k), (170 * $k)), $sf)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, 'Png'); $bmp.Dispose(); , $ms.ToArray()
}
$out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i] % 256
    $w.Write([byte]$s); $w.Write([byte]$s); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "..\src\Inkswap\app.ico"), $out.ToArray())
"icon written"
