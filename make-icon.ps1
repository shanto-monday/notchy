# Draws the Notchy icon (screen with a notch) and saves src\app.ico
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$b = New-Object System.Drawing.Bitmap 256,256
$g = [System.Drawing.Graphics]::FromImage($b); $g.SmoothingMode = 'AntiAlias'
function RR($x,$y,$w,$h,$r){ $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d=$r*2
  $p.AddArc($x,$y,$d,$d,180,90); $p.AddArc($x+$w-$d,$y,$d,$d,270,90); $p.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90); $p.AddArc($x,$y+$h-$d,$d,$d,90,90); $p.CloseFigure(); $p }
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0,0),(New-Object System.Drawing.Point 256,256),([System.Drawing.Color]::FromArgb(255,58,120,255)),([System.Drawing.Color]::FromArgb(255,20,40,110))
$g.FillPath($bg,(RR 8 8 240 240 56))
$notch = New-Object System.Drawing.Drawing2D.GraphicsPath
$notch.AddLine(68,8,188,8); $notch.AddArc(140,-40,48,96,0,90); $notch.AddLine(164,56,92,56); $notch.AddArc(68,-40,48,96,90,90); $notch.CloseFigure()
$g.SetClip((RR 8 8 240 240 56)); $g.FillPath([System.Drawing.Brushes]::Black,$notch); $g.ResetClip()
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235,255,255,255))
$g.FillEllipse($white,150,24,14,14)
$g.FillPath($white,(RR 56 120 144 18 9)); $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150,255,255,255))),(RR 56 156 100 18 9))
$g.Dispose()
$ms = New-Object System.IO.MemoryStream; $b.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png); $png=$ms.ToArray()
$b.Save("$here\src\icon.png")
$ico = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]1); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$png.Length); $w.Write([uint32]22); $w.Write($png); $w.Flush()
[IO.File]::WriteAllBytes("$here\src\app.ico",$ico.ToArray())
