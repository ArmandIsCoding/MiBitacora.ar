param(
    [switch]$DownloadModel,
    [string]$ModelsPath = [Environment]::GetEnvironmentVariable('OLLAMA_MODELS', 'User')
)

$ErrorActionPreference = 'Stop'
$ollamaExe = (Get-Command ollama -ErrorAction Stop).Source
$appExe = Join-Path (Split-Path $ollamaExe) 'ollama app.exe'
$settings = [ordered]@{
    OLLAMA_HOST = '127.0.0.1:11434'
    OLLAMA_NO_CLOUD = '1'
    OLLAMA_CONTEXT_LENGTH = '8192'
    OLLAMA_NUM_PARALLEL = '1'
    OLLAMA_MAX_LOADED_MODELS = '2'
    OLLAMA_MAX_QUEUE = '8'
    OLLAMA_VULKAN = '1'
    OLLAMA_IGPU_ENABLE = '1'
    OLLAMA_DEBUG_LOG_REQUESTS = 'false'
}
if (-not [string]::IsNullOrWhiteSpace($ModelsPath)) {
    $modelDirectory = [IO.Path]::GetFullPath($ModelsPath)
    if (-not (Test-Path -LiteralPath $modelDirectory -PathType Container)) { throw 'No existe la carpeta indicada para los modelos.' }
    $settings.OLLAMA_MODELS = $modelDirectory
}

# Refuse to interrupt another application's active model.
$running = $null
try { $running = Invoke-RestMethod 'http://127.0.0.1:11434/api/ps' -TimeoutSec 5 } catch { }
if ($running -and $running.models.Count -gt 0) {
    throw 'Hay modelos cargados en Ollama. Terminá las consultas y descargalos con ollama stop <modelo> antes de configurar.'
}

$stateDir = Join-Path $env:LOCALAPPDATA 'MiBitacora\ollama'
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
$backupPath = Join-Path $stateDir 'environment-before-setup.json'
if (-not (Test-Path -LiteralPath $backupPath)) {
    $previous = [ordered]@{}
    foreach ($key in $settings.Keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'User') }
    $previous | ConvertTo-Json | Set-Content -LiteralPath $backupPath -Encoding UTF8
}
foreach ($key in $settings.Keys) {
    [Environment]::SetEnvironmentVariable($key, $settings[$key], 'User')
    [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
}

# Restart only the current user's Ollama processes from this installation.
$ownerSession = (Get-Process -Id $PID).SessionId
foreach ($processName in @('ollama app', 'ollama')) {
    Get-Process -Name $processName -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $ownerSession -and $_.Path -in @($appExe, $ollamaExe) } |
        Stop-Process
}
# The desktop app can override OLLAMA_HOST and model limits with its own preferences.
# Run the supported server command directly so the configuration above is authoritative.
Start-Process -FilePath $ollamaExe -ArgumentList 'serve' -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $stateDir 'server.out.log') `
    -RedirectStandardError (Join-Path $stateDir 'server.err.log')

$ready = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    Start-Sleep -Milliseconds 500
    try {
        $version = Invoke-RestMethod 'http://127.0.0.1:11434/api/version' -TimeoutSec 2
        $ready = $true
        break
    } catch { }
}
if (-not $ready) { throw 'Ollama no inició. Revisá sus registros en %LOCALAPPDATA%\Ollama.' }
$listeners = Get-NetTCPConnection -State Listen -LocalPort 11434
if ($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') }) {
    throw 'La app de Ollama está sobrescribiendo OLLAMA_HOST. Desactivá Expose Ollama to the network en sus ajustes y volvé a ejecutar este script.'
}
Write-Output "Ollama $($version.version), configurado en http://127.0.0.1:11434 con funciones cloud desactivadas."

if ($DownloadModel) {
    & $ollamaExe pull gemma4:12b
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo descargar gemma4:12b.' }
    & $ollamaExe pull embeddinggemma
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo descargar embeddinggemma.' }
}
