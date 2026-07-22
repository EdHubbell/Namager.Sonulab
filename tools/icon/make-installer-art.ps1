# Regenerates the installer wizard art from the app icon (studio-warm palette, matching
# Styles/SonulabTheme.axaml):
#   src/ToneManager.Installer/Assets/banner.bmp  (493x58  - WixUIBannerBmp, top strip)
#   src/ToneManager.Installer/Assets/dialog.bmp  (493x312 - WixUIDialogBmp, welcome/finish)
# Requires ImageMagick 7+ on PATH (dev machine only - the .bmp files are committed).
# MSI dialogs need classic 24-bit BMPs: compose with alpha, then strip it (BMP3 + -alpha off).
$assets = Join-Path $PSScriptRoot "..\..\src\ToneManager.App\Assets"
$out = Join-Path $PSScriptRoot "..\..\src\ToneManager.Installer\Assets"
New-Item -ItemType Directory -Force $out | Out-Null
$ico = Join-Path $assets "app-icon.ico"   # frame 0 = 256px

# Banner: near-white, icon at the right, 2px accent baseline. The dialog title text is drawn
# by the installer over the left area (dark text on near-white).
magick -size 493x58 xc:'#FFFDF9' `
    '(' -size 493x2 xc:'#D9820F' ')' -geometry +0+56 -composite `
    '(' "$ico[0]" -resize 40x40 ')' -geometry +441+9 -composite `
    -alpha off "BMP3:$(Join-Path $out 'banner.bmp')"

# Welcome/finish background: dark studio-warm side panel (left 164px) with the icon and a 3px
# accent rule; the rest near-white (the installer draws its text from ~x180px rightward).
magick -size 493x312 xc:'#FFFDF9' `
    '(' -size 164x312 xc:'#26221E' ')' -geometry +0+0 -composite `
    '(' -size 3x312 xc:'#D9820F' ')' -geometry +164+0 -composite `
    '(' "$ico[0]" -resize 96x96 ')' -geometry +34+54 -composite `
    -alpha off "BMP3:$(Join-Path $out 'dialog.bmp')"

Write-Host "Wrote $(Join-Path $out 'banner.bmp') and $(Join-Path $out 'dialog.bmp')"
