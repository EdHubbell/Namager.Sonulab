<#  READ-ONLY: dread preset slot 12 (Pano-Verb), compare to Pano-Verb.pst, show response format.
    No writes. Confirms blob layout before any guarded write. #>
param([string]$Port='COM6',[int]$Baud=115200,[int]$Index=12)
$ErrorActionPreference='Stop'
$pstPath = Join-Path $PSScriptRoot '..\Pano-Verb.pst'

$sp = New-Object System.IO.Ports.SerialPort $Port,$Baud,([System.IO.Ports.Parity]::None),8,([System.IO.Ports.StopBits]::One)
$sp.ReadTimeout=500; $sp.Open()
function Send($c){ $sp.DiscardInBuffer(); $b=[Text.Encoding]::ASCII.GetBytes($c); $sp.Write($b,0,$b.Length); $sp.Write([byte[]]@(0),0,1) }
function ReadWin([int]$ms){ $sb=New-Object Text.StringBuilder; $dl=[Environment]::TickCount+$ms
  while([Environment]::TickCount -lt $dl){ if($sp.BytesToRead -gt 0){ $c=New-Object byte[] $sp.BytesToRead; $n=$sp.Read($c,0,$c.Length); [void]$sb.Append([Text.Encoding]::ASCII.GetString($c,0,$n)); $dl=[Environment]::TickCount+100 } else { Start-Sleep -Milliseconds 10 } }
  $sb.ToString() }
# extract the "value":"<hex>" from a dread response for a given chunk
function ChunkHex([string]$raw,[int]$chunk){
  foreach($line in ($raw -split "`r?`n")){
    if($line -match ('"chunk":{0},"value":"([0-9a-fA-F]*)"' -f $chunk)){ return $matches[1] }
    if($line -match ('"chunk":{0}\b' -f $chunk) -and $line -match '"value":"([0-9a-fA-F]*)"'){ return $matches[1] }
  }
  return $null
}

# Show the raw response format for chunk 1 (so we see the exact shape)
Send ('dread root\presets:{{"index":{0},"chunk":1}}' -f $Index)
$first = ReadWin 500
Write-Output "=== raw response to dread chunk 1 (meters filtered) ==="
($first -split "`r?`n") | Where-Object { $_ -notmatch 'meters' -and $_ -notmatch 'usb\\_status' -and $_.Trim() } | Select-Object -First 4

# Read name (chunk -1) and content chunks 1..64
Send ('dread root\presets:{{"index":{0},"chunk":-1}}' -f $Index)
$nameRaw = ReadWin 400
$nameHex = ChunkHex $nameRaw -1
$bytes = New-Object System.Collections.Generic.List[byte]
$missing=@()
for($c=1;$c -le 64;$c++){
  Send ('dread root\presets:{{"index":{0},"chunk":{1}}}' -f $Index,$c)
  $h = ChunkHex (ReadWin 350) $c
  if(-not $h){ $missing += $c; continue }
  for($i=0;$i+1 -lt $h.Length;$i+=2){ $bytes.Add([Convert]::ToByte($h.Substring($i,2),16)) }
}
$sp.Close()

$dev = $bytes.ToArray()
$file = [IO.File]::ReadAllBytes($pstPath)
Write-Output ""
if($nameHex){ Write-Output ("name(chunk -1) hex head: {0}" -f ($nameHex.Substring(0,[Math]::Min(64,$nameHex.Length))))
  $nb=@(); for($i=0;$i+1 -lt $nameHex.Length;$i+=2){ $v=[Convert]::ToByte($nameHex.Substring($i,2),16); if($v -eq 0){break}; $nb+=$v }; Write-Output ("name decoded: '{0}'" -f ([Text.Encoding]::ASCII.GetString([byte[]]$nb))) }
else { Write-Output "name(chunk -1): no hex value parsed (name is read via 'read root\presets' array instead). Raw:"; ($nameRaw -split "`r?`n") | Where-Object { $_ -match '"chunk":-1' } | Select-Object -First 1 }
Write-Output ("device blob bytes: {0}   pst file bytes: {1}   missing chunks: {2}" -f $dev.Length,$file.Length,($missing -join ','))
$min=[Math]::Min($dev.Length,$file.Length); $diff=-1
for($i=0;$i -lt $min;$i++){ if($dev[$i] -ne $file[$i]){ $diff=$i; break } }
if($diff -lt 0 -and $dev.Length -eq $file.Length){ Write-Output "RESULT: IDENTICAL — .pst == device blob. Format confirmed." }
else { Write-Output ("RESULT: DIFFER at byte {0} (dev={1} file={2}). Showing first 160 bytes each:" -f $diff, $(if($diff -ge 0){$dev[$diff]}else{'-'}), $(if($diff -ge 0){$file[$diff]}else{'-'}))
  Write-Output ("dev : {0}" -f ([Text.Encoding]::ASCII.GetString($dev,0,[Math]::Min(160,$dev.Length))))
  Write-Output ("file: {0}" -f ([Text.Encoding]::ASCII.GetString($file,0,[Math]::Min(160,$file.Length)))) }
