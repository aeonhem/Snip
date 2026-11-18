# End-to-end test: start Snip, press Print Screen, crop, draw, press Enter, check Downloads.
# It does not move the real mouse: it posts mouse messages straight to the editor window.
$ErrorActionPreference = 'Stop'
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(IntPtr cls, string title);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@
function Pos($x, $y) { [IntPtr](($y -shl 16) -bor $x) }
function Drag($h, $pts) {
  [W]::PostMessage($h, 0x201, [IntPtr]1, (Pos $pts[0][0] $pts[0][1])) | Out-Null
  foreach ($p in $pts) { [W]::PostMessage($h, 0x200, [IntPtr]1, (Pos $p[0] $p[1])) | Out-Null }
  $l = $pts[-1]; [W]::PostMessage($h, 0x202, [IntPtr]0, (Pos $l[0] $l[1])) | Out-Null
  Start-Sleep -Milliseconds 300
}

$exe = Join-Path $PSScriptRoot '..\Snip.exe'
$downloads = (New-Object -ComObject Shell.Application).Namespace('shell:Downloads').Self.Path
$before = Get-Date
$proc = Start-Process $exe -PassThru
Start-Sleep 2

[W]::keybd_event(0x2C, 0, 0, [UIntPtr]::Zero); [W]::keybd_event(0x2C, 0, 2, [UIntPtr]::Zero)   # Print Screen
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 20 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 200; $h = [W]::FindWindow([IntPtr]::Zero, 'Snip editor') }
if ($h -eq [IntPtr]::Zero) { Stop-Process $proc; throw 'FAIL: editor did not open after Print Screen' }
"editor opened"

Drag $h @(@(500, 300), @(900, 500), @(1300, 800))           # crop (default tool), then it switches to pen
$circle = 0..36 | ForEach-Object { $a = $_ * 10 * [Math]::PI / 180; ,@([int](900 + 150 * [Math]::Cos($a)), [int](550 + 100 * [Math]::Sin($a))) }
Drag $h $circle                                              # red circle
Drag $h @(@(600, 400), @(700, 450))                          # extra line, then real Ctrl+Z removes it
[W]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero); [W]::keybd_event(0x5A, 0, 0, [UIntPtr]::Zero)
[W]::keybd_event(0x5A, 0, 2, [UIntPtr]::Zero); [W]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 300
[W]::PostMessage($h, 0x100, [IntPtr]0x0D, [IntPtr]0) | Out-Null  # Enter = Done
Start-Sleep 2

$new = Get-ChildItem $downloads -Filter 'Snip *.png' | Where-Object { $_.LastWriteTime -ge $before }
Stop-Process $proc
if (-not $new) { throw 'FAIL: no PNG in Downloads' }
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($new[0].FullName)
"PASS: $($new[0].FullName)  size $($img.Width)x$($img.Height)"
$img.Dispose()
