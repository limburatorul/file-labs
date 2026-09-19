<#
    Draws app.ico — variant 4.4: dark tile, a neon folder outline running pink → orange,
    "labs" in the same gradient. Every size is drawn natively (not scaled from 256) so
    small sizes stay crisp. Run it again after changing anything here; the build picks up app.ico.
#>
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\app.ico'

$tileTop = '#1E1628'; $tileBottom = '#0F0B16'; $pink = '#FF4FD8'; $orange = '#FFB23F'

function C($hex, $a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($a, $c) }
function LG($x, $y, $w, $h, $c1, $c2, $angle) { New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF $x, $y, $w, $h), $c1, $c2, $angle }
function RR([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}
# one continuous folder silhouette (tab + body), so no edge is stroked twice
function Silhouette($ox, $oy, $s) {
    $r = 10 * $s; $rt = 6 * $s; $W = 120 * $s; $H = 94 * $s; $top = 10 * $s
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($ox, $oy + $H - 2*$r, 2*$r, 2*$r, 90, 90)
    $p.AddArc($ox, $oy, 2*$rt, 2*$rt, 180, 90)
    $p.AddLine($ox + 40*$s, $oy, $ox + 50*$s, $oy + $top)
    $p.AddArc($ox + $W - 2*$r, $oy + $top, 2*$r, 2*$r, 270, 90)
    $p.AddArc($ox + $W - 2*$r, $oy + $H - 2*$r, 2*$r, 2*$r, 0, 90)
    $p.CloseFigure(); $p
}
function FlapEdge($ox, $oy, $s) {
    $r = 10 * $s; $W = 120 * $s; $y = $oy + 30 * $s
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($ox, $y, 2*$r, 2*$r, 180, 90); $p.AddArc($ox + $W - 2*$r, $y, 2*$r, 2*$r, 270, 90); $p
}

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $u = $size / 128.0
    $g.FillPath((LG (4*$u) (4*$u) (120*$u) (120*$u) (C $tileTop) (C $tileBottom) 90), (RR (4*$u) (4*$u) (120*$u) (120*$u) (26*$u)))

    $s = 0.72 * $u; $ox = 20.8*$u; $oy = 30*$u
    $grad = { param($a) LG $ox $oy (120*$s) (94*$s) (C $pink $a) (C $orange $a) 35 }
    # glow only where there are pixels for it; at 16/20 px it just smears
    $glow = if ($size -ge 32) { @(10, 7, 4.5) } else { @() }
    $main = [Math]::Max(2.8 * $u, 1.1)
    foreach ($path in (Silhouette $ox $oy $s), (FlapEdge $ox $oy $s)) {
        foreach ($w in $glow) { $pen = New-Object System.Drawing.Pen (& $grad 34), ($w * $u); $pen.LineJoin = 'Round'; $g.DrawPath($pen, $path) }
        $pen = New-Object System.Drawing.Pen (& $grad 255), $main; $pen.LineJoin = 'Round'; $pen.StartCap = $pen.EndCap = 'Round'; $g.DrawPath($pen, $path)
    }

    $text = New-Object System.Drawing.Drawing2D.GraphicsPath
    $text.AddString('labs', (New-Object System.Drawing.FontFamily 'Segoe UI Black'), 0, 38 * $s, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
    $b = $text.GetBounds(); $cx = $ox + 60*$s; $cy = $oy + 62*$s
    $m = New-Object System.Drawing.Drawing2D.Matrix; $m.Translate($cx - $b.X - $b.Width / 2, $cy - $b.Y - $b.Height / 2); $text.Transform($m)
    $g.FillPath((LG ($ox + 10*$s) ($cy - 12*$s) (100*$s) (24*$s) (C $pink) (C $orange) 0), $text)
    $g.Dispose(); $bmp
}

# 256 as PNG; smaller sizes as classic 32-bit DIBs, which every consumer reads (resource
# compiler, Inno Setup, older shell code) — PNG entries below 256 are not universally supported.
function Dib([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width; $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
    $bw.Write((New-Object byte[] ([int]([Math]::Ceiling($s / 32.0) * 4) * $s)))   # AND mask: alpha does the work
    $bw.Flush(); , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = Draw $s
    if ($s -ge 256) { $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray() } else { Dib $bmp }
}

$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "wrote $out"
