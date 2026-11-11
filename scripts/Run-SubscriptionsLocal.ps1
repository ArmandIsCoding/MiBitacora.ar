param([ValidateRange(1024, 65535)][int]$Port = 5264)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localArtifacts = Join-Path $projectRoot '.artifacts'
New-Item -ItemType Directory -Path $localArtifacts -Force | Out-Null
$origin = "http://127.0.0.1:$Port"
$settings = @{
    ASPNETCORE_ENVIRONMENT = 'Development'
    DOTNET_ENVIRONMENT = 'Development'
    Subscriptions__Provider = 'Simulated'
    Subscriptions__AllowLiveRequests = 'false'
    Subscriptions__MonthlyPriceArs = '8000'
    Subscriptions__PublicBaseUrl = $origin
    ConnectionStrings__DefaultConnection = "Data Source=$(Join-Path $localArtifacts 'local-subscriptions.db');Default Timeout=15"
    MSBuildEnableWorkloadResolver = 'false'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
}
$previous = @{}
foreach ($name in $settings.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
}
Push-Location $projectRoot
try {
    Write-Host "Prueba local: $origin/Account/Manage/Subscription"
    Write-Host 'Mercado Pago simulado. ARS 8000 es un precio de prueba. No se mueve dinero.'
    Write-Host 'La base de prueba se conserva en .artifacts/local-subscriptions.db. Ctrl+C para cerrar.'
    dotnet run --project MiBitacora.csproj --no-launch-profile --urls $origin
    if ($LASTEXITCODE -ne 0) { throw "La aplicación finalizó con código $LASTEXITCODE." }
}
finally {
    Pop-Location
    foreach ($name in $settings.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
