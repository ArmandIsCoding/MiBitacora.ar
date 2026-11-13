param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [switch]$MediaCodecs)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testTemp = Join-Path $projectRoot '.artifacts/test-tmp'
New-Item -ItemType Directory -Path $testTemp -Force | Out-Null
$settings = @{ TEMP = $testTemp; TMP = $testTemp; MSBuildEnableWorkloadResolver = 'false'; DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true' }
$previous = @{}
foreach ($name in $settings.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
}
Push-Location $projectRoot
try {
    dotnet build tests/MiBitacora.Checks -c $Configuration --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación.' }
    $checkArguments = if ($MediaCodecs) { @('--media-codecs') } else { @() }
    dotnet "tests/MiBitacora.Checks/bin/$Configuration/net10.0/MiBitacora.Checks.dll" @checkArguments
    if ($LASTEXITCODE -ne 0) { throw 'Fallaron las comprobaciones de .NET.' }
    node --test tests/voice-recorder.test.mjs tests/journal-scroll.test.mjs tests/brisa-motion.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Fallaron las comprobaciones del navegador.' }
}
finally {
    Pop-Location
    foreach ($name in $settings.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
