param([string]$InputDirectory = (Join-Path $PSScriptRoot '../../docs/game-preview/d2d-0.6.0'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
function Color([long]$Value) {
    [Drawing.Color]::FromArgb([int](($Value -shr 24) -band 255),[int](($Value -shr 16) -band 255),[int](($Value -shr 8) -band 255),[int]($Value -band 255))
}
function Rounded([Drawing.Graphics]$Graphics,[Drawing.Brush]$Brush,[float]$X,[float]$Y,[float]$W,[float]$H,[float]$R) {
    if ($W -le 0 -or $H -le 0) { return }
    $R=[Math]::Min($R,[Math]::Min($W,$H)/2)
    if ($R -le 0) { $Graphics.FillRectangle($Brush,$X,$Y,$W,$H); return }
    $path=[Drawing.Drawing2D.GraphicsPath]::new()
    try {
        $d=$R*2
        $path.AddArc($X,$Y,$d,$d,180,90)
        $path.AddArc(($X+$W-$d),$Y,$d,$d,270,90)
        $path.AddArc(($X+$W-$d),($Y+$H-$d),$d,$d,0,90)
        $path.AddArc($X,($Y+$H-$d),$d,$d,90,90)
        $path.CloseFigure()
        $Graphics.FillPath($Brush,$path)
    } finally { $path.Dispose() }
}
foreach ($name in @('actions','editor','precache','time','parameters','hidden','xy','settings','deletion','resized','local-motion','files','file-browser','file-save','file-browser-small','clipboard-entry','clipboard-attribute')) {
    if (-not (Test-Path -LiteralPath (Join-Path $InputDirectory ($name+'.json')))) { continue }
    $layout=Get-Content -LiteralPath (Join-Path $InputDirectory ($name+'.json')) -Raw -Encoding utf8 | ConvertFrom-Json
    $bitmap=[Drawing.Bitmap]::new([int]$layout.width,[int]$layout.height)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $fonts=@{}
    try {
        $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint=[Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([Drawing.Color]::FromArgb(7,10,18))
        foreach ($c in $layout.commands) {
            if ($c.kind -eq 'image') {
                $asset=Join-Path $project ('game-plugin/images/'+$c.path)
                if (Test-Path -LiteralPath $asset) {
                    $image=[Drawing.Image]::FromFile($asset)
                    try { $graphics.DrawImage($image,[float]$c.x,[float]$c.y,[float]$c.w,[float]$c.h) } finally { $image.Dispose() }
                }
                continue
            }
            $brush=[Drawing.SolidBrush]::new((Color $c.color))
            try {
                if ($c.kind -eq 'rect') { Rounded $graphics $brush $c.x $c.y $c.w $c.h $c.r }
                elseif ($c.kind -eq 'line') {
                    $pen=[Drawing.Pen]::new((Color $c.color),1.5)
                    try { $graphics.DrawLine($pen,[float]$c.x,[float]$c.y,[float]$c.x2,[float]$c.y2) } finally { $pen.Dispose() }
                }
                elseif ($c.kind -eq 'text') {
                    $size=[Math]::Round($c.size)
                    if (-not $fonts.ContainsKey($size)) { $fonts[$size]=[Drawing.Font]::new('Microsoft YaHei UI',[float]$size,[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel) }
                    $format=[Drawing.StringFormat]::GenericTypographic.Clone()
                    try {
                        $format.Trimming=[Drawing.StringTrimming]::EllipsisCharacter
                        $format.FormatFlags=[Drawing.StringFormatFlags]::NoWrap
                        $width=if ($null -ne $c.maxWidth) {[float]$c.maxWidth} else {[float]($layout.width-$c.x)}
                        if ($width -gt 4) {
                            $rect=[Drawing.RectangleF]::new([float]$c.x,[float]$c.y,$width,[float]([Math]::Max(40,$c.size*3)))
                            $graphics.DrawString([string]$c.text,$fonts[$size],$brush,$rect,$format)
                        }
                    } finally { $format.Dispose() }
                }
            } finally { $brush.Dispose() }
        }
        $font=[Drawing.Font]::new('Microsoft YaHei UI',11,[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel)
        $brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(110,125,150))
        try { $graphics.DrawString('离线界面预览 · 使用工作台代码生成布局，待游戏内验收',$font,$brush,[float]24,[float]($layout.height-24)) } finally { $font.Dispose();$brush.Dispose() }
        $target=Join-Path $InputDirectory ($name+'.png')
        $bitmap.Save($target,[Drawing.Imaging.ImageFormat]::Png)
        Write-Output $target
    } finally {
        foreach ($font in $fonts.Values) { $font.Dispose() }
        $graphics.Dispose();$bitmap.Dispose()
    }
}
