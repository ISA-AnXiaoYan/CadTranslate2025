#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$CacheFile,
    [Parameter(Mandatory)][string]$OutputPng,
    [ValidateRange(300,4000)][int]$Width=1600
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing.Common
$source=(Resolve-Path -LiteralPath $CacheFile).Path
$output=[IO.Path]::GetFullPath($OutputPng)
if([IO.Path]::GetExtension($output) -ne '.png' -or (Test-Path -LiteralPath $output)) {
    throw 'Specify a new PNG path'
}
$data=[IO.File]::ReadAllBytes($source)
if($data.Length -le 40) { throw 'Unsupported presentation stream' }
$stream=[IO.MemoryStream]::new($data,40,$data.Length-40)
try {
    $image=[Drawing.Image]::FromStream($stream)
    try {
        $scale=[Math]::Min($Width/[double]$image.Width,10000.0/$image.Height)
        $bitmap=[Drawing.Bitmap]::new([Math]::Max(1,[int]($image.Width*$scale)),[Math]::Max(1,[int]($image.Height*$scale)))
        try {
            $graphics=[Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::White)
                $graphics.DrawImage($image,0,0,$bitmap.Width,$bitmap.Height)
                $bitmap.Save($output,[Drawing.Imaging.ImageFormat]::Png)
            } finally { $graphics.Dispose() }
        } finally { $bitmap.Dispose() }
    } finally { $image.Dispose() }
} finally { $stream.Dispose() }
$output
