<#  Guarded preset write: backup slot -> write .pst content (chunks 1..64) + name (chunk -1)
    -> read back and verify. Writes ONLY to the given slot. #>
param(
  [string]$Port='COM6',[int]$Baud=115200,
  [int]$Index=25,[string]$Name='Test Write',
  [string]$PstPath=(Join-Path $PSScriptRoot '..\Pano-Verb.pst')
)
$ErrorActionPreference='Stop'
$file=[IO.File]::ReadAllBytes($PstPath)
if($file.Length -ne 8192){ throw "pst is $($file.Length) bytes, expected 8192" }

$sp=New-Object System.IO.Ports.SerialPort $Port,$Baud,([System.IO.Ports.Parity]::None),8,([System.IO.Ports.StopBits]::One)
$sp.ReadTimeout=500; $sp.Open()
function Send($c){ $sp.DiscardInBuffer(); $b=[Text.Encoding]::ASCII.GetBytes($c); $sp.Write($b,0,$b.Length); $sp.Write([byte[]]@(0),0,1) }
function ReadWin([int]$ms){ $sb=New-Object Text.StringBuilder; $dl=[Environment]::TickCount+$ms
  while([Environment]::TickCount -lt $dl){ if($sp.BytesToRead -gt 0){ $c=New-Object byte[] $sp.BytesToRead; $n=$sp.Read($c,0,$c.Length); [void]$sb.Append([Text.Encoding]::ASCII.GetString($c,0,$n)); $dl=[Environment]::TickCount+90 } else { Start-Sleep -Milliseconds 8 } }
  $sb.ToString() }
function ChunkHex([string]$raw,[int]$chunk){ foreach($l in ($raw -split "`r?`n")){ if($l -match ('"chunk":{0}\b' -f $chunk) -and $l -match '"value":"([0-9a-fA-F]*)"'){ return $matches[1] } }; return $null }
function ToHex([byte[]]$b){ ($b | ForEach-Object { $_.ToString('x2') }) -join '' }
function ReadSlot([int]$idx){ $bytes=New-Object Collections.Generic.List[byte]
  for($c=1;$c -le 64;$c++){ Send ('dread root\presets:{{"index":{0},"chunk":{1}}}' -f $idx,$c); $h=ChunkHex (ReadWin 300) $c
    if($h){ for($i=0;$i+1 -lt $h.Length;$i+=2){ $bytes.Add([Convert]::ToByte($h.Substring($i,2),16)) } } }
  ,$bytes.ToArray() }

# 1) BACKUP current slot contents
Write-Output "== backup slot $Index =="
$backup = ReadSlot $Index
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss')
$bkDir = Join-Path $PSScriptRoot '..\docs\backups'; New-Item -ItemType Directory -Force -Path $bkDir | Out-Null
$bkPath = Join-Path $bkDir ("slot{0}-{1}.bin" -f $Index,$stamp)
[IO.File]::WriteAllBytes($bkPath,$backup)
Write-Output ("backup: {0} bytes -> {1}" -f $backup.Length,$bkPath)

# 2) WRITE name chunk -1 FIRST (creates/initializes the slot)
Write-Output "== write name (chunk -1) =="
$nameBytes = New-Object byte[] 128
$na=[Text.Encoding]::ASCII.GetBytes($Name); [Array]::Copy($na,0,$nameBytes,0,[Math]::Min($na.Length,128))
Send ('dwrite root\presets:{{"index":{0},"chunk":-1,"value":"{1}"}}' -f $Index,(ToHex $nameBytes))
[void](ReadWin 250)
# 3) WRITE content chunks 1..64
Write-Output "== write content (64 chunks) =="
for($c=1;$c -le 64;$c++){
  $seg = New-Object byte[] 128; [Array]::Copy($file,($c-1)*128,$seg,0,128)
  Send ('dwrite root\presets:{{"index":{0},"chunk":{1},"value":"{2}"}}' -f $Index,$c,(ToHex $seg))
  [void](ReadWin 70)
}

# 4) VERIFY read-back
Write-Output "== verify =="
$rb = ReadSlot $Index
$ok = ($rb.Length -eq 8192)
if($ok){ for($i=0;$i -lt 8192;$i++){ if($rb[$i] -ne $file[$i]){ $ok=$false; Write-Output "content mismatch at byte $i"; break } } }
Send 'read root\presets'
$names = (ReadWin 500) -split "`r?`n" | Where-Object { $_ -match 'root\\presets:\{"value":\[' } | Select-Object -First 1
$sp.Close()

Write-Output ("content verify: {0}" -f $(if($ok){'IDENTICAL ✓'}else{'MISMATCH ✗'}))
if($names){ $arr = ([regex]'"([^"]*)"').Matches(($names -replace '^.*\[','')) | ForEach-Object { $_.Groups[1].Value }
  Write-Output ("slot {0} name now: '{1}'" -f $Index, $arr[$Index]) }
Write-Output "RESULT: $(if($ok){'PASS — preset content dwrite confirmed. Reorder/copy primitive validated.'}else{'FAIL — see mismatch above; restore from '+$bkPath})"
