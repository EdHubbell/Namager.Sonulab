<#  Experiment: load Pano-Verb.pst params into root\app, save-as "Test Write",
    verify slot 25 content + show where it landed. Changes live state. #>
param([string]$Port='COM6',[int]$Baud=115200,[int]$Index=25,[string]$Name='Test Write',
      [string]$PstPath=(Join-Path $PSScriptRoot '..\Pano-Verb.pst'))
$ErrorActionPreference='Stop'
$file=[IO.File]::ReadAllBytes($PstPath)
$txt=[Text.Encoding]::ASCII.GetString($file)
$lines = ($txt -split "`r`n") | Where-Object { $_ -match '^root\\app\\.+:\{"value":' }

$sp=New-Object System.IO.Ports.SerialPort $Port,$Baud,([System.IO.Ports.Parity]::None),8,([System.IO.Ports.StopBits]::One)
$sp.ReadTimeout=500; $sp.Open()
function Send($c){ $sp.DiscardInBuffer(); $b=[Text.Encoding]::ASCII.GetBytes($c); $sp.Write($b,0,$b.Length); $sp.Write([byte[]]@(0),0,1) }
function ReadWin([int]$ms){ $sb=New-Object Text.StringBuilder; $dl=[Environment]::TickCount+$ms; while([Environment]::TickCount -lt $dl){ if($sp.BytesToRead -gt 0){ $c=New-Object byte[] $sp.BytesToRead; $n=$sp.Read($c,0,$c.Length); [void]$sb.Append([Text.Encoding]::ASCII.GetString($c,0,$n)); $dl=[Environment]::TickCount+90 } else { Start-Sleep -Milliseconds 8 } }; $sb.ToString() }
function ChunkHex([string]$raw,[int]$chunk){ foreach($l in ($raw -split "`r?`n")){ if($l -match ('"chunk":{0}\b' -f $chunk) -and $l -match '"value":"([0-9a-fA-F]*)"'){ return $matches[1] } }; return $null }
function ReadSlot([int]$idx){ $bytes=New-Object Collections.Generic.List[byte]; for($c=1;$c -le 64;$c++){ Send ('dread root\presets:{{"index":{0},"chunk":{1}}}' -f $idx,$c); $h=ChunkHex (ReadWin 300) $c; if($h){ for($i=0;$i+1 -lt $h.Length;$i+=2){ $bytes.Add([Convert]::ToByte($h.Substring($i,2),16)) } } }; ,$bytes.ToArray() }

Write-Output ("== loading {0} param lines into root\app ==" -f $lines.Count)
foreach($ln in $lines){ Send ("write " + $ln.Trim()); [void](ReadWin 45) }
Write-Output "== save-as '$Name' =="
Send ('write root\app\preset:{{"value":"{0}","save":"save"}}' -f $Name)
[void](ReadWin 600)

Write-Output "== verify slot $Index content =="
$rb = ReadSlot $Index
$ok = ($rb.Length -eq 8192)
if($ok){ for($i=0;$i -lt 8192;$i++){ if($rb[$i] -ne $file[$i]){ $ok=$false; $firstDiff=$i; break } } }
Write-Output ("slot {0} content vs Pano-Verb.pst: {1}" -f $Index, $(if($ok){'IDENTICAL'}else{"DIFFERS at byte $firstDiff (len=$($rb.Length))"}))
if(-not $ok -and $rb.Length -ge 60){ Write-Output ("  slot{0} head: {1}" -f $Index,([Text.Encoding]::ASCII.GetString($rb,0,60))) }

Write-Output "== where did 'Test Write' land? full preset list =="
Send 'read root\presets'
$names = ((ReadWin 600) -split "`r?`n" | Where-Object { $_ -match 'root\\presets:\{"value":\[' } | Select-Object -First 1)
$sp.Close()
if($names){ $arr=([regex]'"([^"]*)"').Matches(($names -replace '^[^\[]*\[','')) | ForEach-Object { $_.Groups[1].Value }
  for($i=0;$i -lt [Math]::Min(30,$arr.Count);$i++){ if($arr[$i]){ Write-Output ("  idx {0,2} (slot {1,2}): {2}" -f $i,($i+1),$arr[$i]) } } }
