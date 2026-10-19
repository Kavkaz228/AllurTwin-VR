param([string]$OutputPath)
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap 64,64
$g = [Drawing.Graphics]::FromImage($bitmap)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([Drawing.Color]::FromArgb(13,20,34))
$blue = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(60,140,255))
$white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
$pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(110,190,255)),4
$g.FillRectangle($blue,8,20,48,30)
$g.FillEllipse($white,15,27,13,13)
$g.FillEllipse($white,36,27,13,13)
$g.DrawArc($pen,14,7,36,27,185,170)
$icon = [Drawing.Icon]::FromHandle($bitmap.GetHicon())
$stream = [IO.File]::Create($OutputPath)
try { $icon.Save($stream) } finally { $stream.Dispose(); $icon.Dispose(); $g.Dispose(); $bitmap.Dispose(); $blue.Dispose(); $white.Dispose(); $pen.Dispose() }
