<#
  Sonulab StompStation — read-only protocol probe (Phase 0, Step A)

  SAFE: sends only `read` / `browse` commands. No writes, no dwrite. Cannot change the device.

  Usage:  close VoidX-Control first, pedal on USB, then:
            pwsh -File tools\probe.ps1            # auto-detect COM port + baud
            pwsh -File tools\probe.ps1 -Port COM6 -Baud 115200
  Output: console + docs\probe-output.txt
#>
[CmdletBinding()]
param(
  [string]$Port,
  [int]$Baud,
  [int]$ReadWindowMs = 700
)

$ErrorActionPreference = 'Stop'
$logPath = Join-Path $PSScriptRoot '..\docs\probe-output.txt'
$log = New-Object System.Collections.Generic.List[string]
function Note($s){ $log.Add($s); Write-Host $s }

# Commands to probe (all read-only). Add more freely.
$probes = @(
  'read root\sys\_name', 'read root\sys\_id',
  'browse root', 'browse root\sys', 'browse root\app',
  'browse root\presets', 'read root\presets',
  'browse root\amp',     'read root\amp',
  'browse root\ir',      'read root\ir',
  'read root\app\preset', 'browse root\app\amp', 'browse root\app\ir'
)

# Candidate ports / bauds
$ports = if ($Port) { @($Port) } else { [System.IO.Ports.SerialPort]::GetPortNames() }
$bauds = if ($Baud) { @($Baud) } else { @(115200, 921600, 460800, 230400, 57600, 1500000, 2000000, 9600) }

function Send-Cmd($sp, [string]$cmd) {
  $sp.DiscardInBuffer()
  $bytes = [System.Text.Encoding]::ASCII.GetBytes($cmd)
  $sp.Write($bytes, 0, $bytes.Length)
  $sp.Write([byte[]]@(0), 0, 1)            # NUL terminator
}

function Read-Window($sp, [int]$ms) {
  $sb = New-Object System.Text.StringBuilder
  $deadline = [Environment]::TickCount + $ms
  while ([Environment]::TickCount -lt $deadline) {
    if ($sp.BytesToRead -gt 0) {
      $chunk = New-Object byte[] $sp.BytesToRead
      $n = $sp.Read($chunk, 0, $chunk.Length)
      [void]$sb.Append([System.Text.Encoding]::ASCII.GetString($chunk, 0, $n))
      $deadline = [Environment]::TickCount + 120   # extend while data flows
    } else { Start-Sleep -Milliseconds 15 }
  }
  $sb.ToString()
}

function Filter-Meters([string]$raw) {
  # responses are CRLF-separated path:{...} records; drop the meter/status spam + NULs
  ($raw -replace "`0", '') -split "`r?`n" |
    Where-Object { $_.Trim() -ne '' -and $_ -notmatch 'root\\sys\\_meters\\' -and $_ -notmatch 'root\\usb\\_status' }
}

$connected = $false
foreach ($pn in $ports) {
  foreach ($b in $bauds) {
    $sp = $null
    try {
      $sp = New-Object System.IO.Ports.SerialPort $pn, $b, ([System.IO.Ports.Parity]::None), 8, ([System.IO.Ports.StopBits]::One)
      $sp.ReadTimeout = 500; $sp.WriteTimeout = 500
      $sp.Open()
      Send-Cmd $sp 'read root\sys\_name'
      $resp = Read-Window $sp 600
      if ($resp -match 'root\\sys\\_name') {
        Note "=== CONNECTED on $pn @ $b baud ==="
        $connected = $true
        foreach ($cmd in $probes) {
          Send-Cmd $sp $cmd
          $out = Filter-Meters (Read-Window $sp $ReadWindowMs)
          Note ""
          Note ("> {0}" -f $cmd)
          if ($out.Count -eq 0) { Note "  (no non-meter response)" }
          else { $out | ForEach-Object { Note ("  {0}" -f $_) } }
        }
        $sp.Close(); break
      }
      $sp.Close()
    } catch {
      Note ("  [skip] {0}@{1}: {2}" -f $pn, $b, $_.Exception.Message)
    } finally {
      if ($sp -and $sp.IsOpen) { $sp.Close() }
      if ($sp) { $sp.Dispose() }
    }
  }
  if ($connected) { break }
}

if (-not $connected) {
  Note "!! No device responded. Is VoidX-Control still open (port busy)? Is the pedal on USB? Try -Port/-Baud."
}

$log | Set-Content -LiteralPath $logPath -Encoding UTF8
Write-Host "`nSaved -> $logPath"
