# Recovery for this patch's two-value journal only. No secrets/config files are read.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$state = Join-Path $env:LOCALAPPDATA 'CodexChannelLauncher\msix-launch-state'
$journalFile = Join-Path $state 'environment-journal.json'
if (-not (Test-Path -LiteralPath $journalFile -PathType Leaf)) {
    Write-Host 'No pending MSIX user-environment recovery record.'
    return
}
if (@(Get-Process -Name CodexChannelLauncher -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Exit the launcher first. The script will not interrupt an active environment transaction.'
}
foreach ($path in @($state, $journalFile, (Join-Path $state 'environment.lock'))) {
    if ((Test-Path -LiteralPath $path) -and
        (((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'Recovery path contains a reparse point.'
    }
}
$gate = [IO.File]::Open((Join-Path $state 'environment.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $journal = Get-Content -LiteralPath $journalFile -Raw | ConvertFrom-Json
    $names = @($journal.Entries | ForEach-Object { $_.Name } | Sort-Object)
    if ($journal.Version -ne 1 -or $names.Count -ne 2 -or ($names -join ',') -ne 'CODEX_HOME,CODEX_SQLITE_HOME') {
        throw 'Unexpected recovery record. No automatic edits made.'
    }
    foreach ($entry in $journal.Entries) {
        foreach ($v in @($entry.Before, $entry.Applied)) {
            if ($v.Exists -and ($v.Kind -notin @(1,2) -or $null -eq $v.Value -or $v.Value.Length -gt 32767)) {
                throw 'Invalid saved environment value.'
            }
            if (-not $v.Exists -and ($null -ne $v.Value -or $v.Kind -ne -1)) { throw 'Invalid absent-value record.' }
        }
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment', $true)
    $conflicts = @()
    try {
        foreach ($entry in $journal.Entries) {
            $exists = @($key.GetValueNames()) -contains $entry.Name
            $kind = -1
            $value = $null
            if ($exists) {
                $kind = [int]$key.GetValueKind($entry.Name)
                $value = $key.GetValue($entry.Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            }
            $before = $entry.Before
            $applied = $entry.Applied
            $sameBefore = ($exists -eq $before.Exists -and $kind -eq $before.Kind -and $value -ceq $before.Value)
            $sameApplied = ($exists -eq $applied.Exists -and $kind -eq $applied.Kind -and $value -ceq $applied.Value)
            if ($sameBefore) { continue }
            if (-not $sameApplied) { $conflicts += $entry.Name; continue }
            if ($before.Exists) { $key.SetValue($entry.Name, [string]$before.Value, [Microsoft.Win32.RegistryValueKind]$before.Kind) }
            else { $key.DeleteValue($entry.Name, $false) }
        }
        $key.Flush()
    } finally { $key.Dispose() }
    if (-not ('CodexMsixPatch.EnvironmentNotice' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace CodexMsixPatch {
 public static class EnvironmentNotice {
  [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern IntPtr SendMessageTimeout(IntPtr w,uint m,UIntPtr p,string s,uint f,uint t,out UIntPtr r);
  public static bool Broadcast() { UIntPtr r; return SendMessageTimeout(new IntPtr(0xffff),0x1A,UIntPtr.Zero,"Environment",2,3000,out r)!=IntPtr.Zero; }
 }
}
'@
    }
    if (-not [CodexMsixPatch.EnvironmentNotice]::Broadcast()) { throw 'Broadcast incomplete. Keeping the journal for retry.' }
    if ($conflicts.Count -gt 0) { throw ('External changes were preserved; review these variable names: ' + ($conflicts -join ', ') + '. Journal retained.') }
    Remove-Item -LiteralPath $journalFile
    Write-Host 'Original user CODEX_HOME / CODEX_SQLITE_HOME values restored; journal removed.'
} finally { $gate.Dispose() }
