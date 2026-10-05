[CmdletBinding()]
param(
    [ValidateSet('Current', 'TieredNoPGO', 'TieredPGO')]
    [string]$Mode = 'Current',
    [string]$GamePath = 'C:\Games\CloneHero',
    [string[]]$GameArguments = @(),
    [string]$RecordPath,
    [switch]$Wait
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $GamePath 'Clone Hero.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Clone Hero executable missing: $exe" }
$runtimeConfig = Join-Path $GamePath 'MelonLoader\net6\MelonLoader.runtimeconfig.json'
if (!(Test-Path -LiteralPath $runtimeConfig -PathType Leaf)) { throw "MelonLoader runtime configuration missing: $runtimeConfig" }

function ConvertTo-WindowsArgument([string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
    finally { $hash.Dispose(); $stream.Dispose() }
}

$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $exe
$start.WorkingDirectory = (Resolve-Path -LiteralPath $GamePath).Path
$start.UseShellExecute = $false
$start.Arguments = ($GameArguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' '
$keys = @('TieredCompilation', 'TC_QuickJit', 'TC_QuickJitForLoops', 'TieredPGO')
$inherited = [ordered]@{}
foreach ($key in $start.EnvironmentVariables.Keys) {
    if ($key -match '^(DOTNET_|COMPlus_)') { $inherited[$key] = $start.EnvironmentVariables[$key] }
}
$overrides = [ordered]@{}
if ($Mode -ne 'Current') {
    $config = Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json
    foreach ($key in @('System.Runtime.TieredCompilation','System.Runtime.TieredCompilation.QuickJit','System.Runtime.TieredCompilation.QuickJitForLoops')) {
        $property = $config.runtimeOptions.configProperties.PSObject.Properties[$key]
        if ($null -ne $property -and $property.Value -ne $true) {
            throw "Host setting $key=$($property.Value) conflicts with controlled tiering. Host configuration was not changed."
        }
    }
    foreach ($key in $keys) {
        $start.EnvironmentVariables.Remove('DOTNET_' + $key)
        $start.EnvironmentVariables.Remove('COMPlus_' + $key)
        $value = if ($key -eq 'TieredPGO' -and $Mode -eq 'TieredNoPGO') { '0' } else { '1' }
        $start.EnvironmentVariables['DOTNET_' + $key] = $value
        $overrides['DOTNET_' + $key] = $value
    }
}
$record = [ordered]@{
    Mode = $Mode
    Executable = $exe
    WorkingDirectory = $start.WorkingDirectory
    Arguments = $GameArguments
    InheritedRuntimeEnvironment = $inherited
    ChildOverrides = $overrides
    HostConfigSha256 = Get-Sha256 $runtimeConfig
    ModSha256 = Get-Sha256 (Join-Path $GamePath 'Mods\ClonZones.dll')
    StartedUtc = [DateTime]::UtcNow.ToString('O')
}
$process = [System.Diagnostics.Process]::Start($start)
$record.ProcessId = $process.Id
if ($RecordPath) {
    $fullRecord = [IO.Path]::GetFullPath($RecordPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullRecord)) | Out-Null
    [IO.File]::WriteAllText($fullRecord, ($record | ConvertTo-Json -Depth 8))
}
Write-Host "Clone Hero pid=$($process.Id), mode=$Mode. Startup settings requested; verify effective tiering from runtime diagnostics."
if ($Wait) {
    $process.WaitForExit()
    $result = $process.ExitCode
    $process.Dispose()
    exit $result
}
$process
