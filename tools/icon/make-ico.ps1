# Regenerates src/Namager.App/Assets/app-icon.ico from app-icon.svg.
# Requires ImageMagick 7+ on PATH (dev machine only - the .ico is committed).
$assets = Join-Path $PSScriptRoot "..\..\src\Namager.App\Assets"
magick -background none (Join-Path $assets "app-icon.svg") `
    -define icon:auto-resize=256,128,64,48,32,24,16 `
    (Join-Path $assets "app-icon.ico")
Write-Host "Wrote $((Join-Path $assets 'app-icon.ico'))"
