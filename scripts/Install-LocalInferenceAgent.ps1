param(
    [string]$ServerUrl = 'https://mibitacora.ar',
    [switch]$NoStartup
)

$ErrorActionPreference = 'Stop'
$origin = [uri]$ServerUrl
if ($origin.Scheme -ne 'https' -or $origin.AbsolutePath -ne '/' -or $origin.UserInfo -or $origin.Query -or $origin.Fragment) {
    throw 'Indicá únicamente el origen HTTPS del sitio, sin rutas ni credenciales.'
}
$repository = Split-Path $PSScriptRoot -Parent
$agentRoot = Join-Path $env:LOCALAPPDATA 'MiBitacora\inference-agent'
New-Item -ItemType Directory -Path $agentRoot -Force | Out-Null

# Create credentials only inside a directory restricted to this Windows account and SYSTEM.
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
$security = [Security.AccessControl.DirectorySecurity]::new()
$security.SetOwner($owner)
$security.SetAccessRuleProtection($true, $false)
$inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
foreach ($sid in @($owner, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, 'None', 'Allow'))
}
[IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($agentRoot), $security)
$configPath = Join-Path $agentRoot 'agent.settings.json'
if (Test-Path -LiteralPath $configPath) {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if ($config.Token -notmatch '^[A-Za-z0-9_-]{43}$' -or ([uri]$config.ServerUrl).AbsoluteUri -ne $origin.AbsoluteUri) {
        throw 'Existe una configuración privada para otro destino o inválida; se conserva sin cambios.'
    }
} else {
    $tokenBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $config = [ordered]@{
        ServerUrl = $origin.AbsoluteUri
        Token = [Convert]::ToBase64String($tokenBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        StartOllama = $true
        OllamaExe = (Get-Command ollama -ErrorAction Stop).Source
        ModelsPath = [Environment]::GetEnvironmentVariable('OLLAMA_MODELS', 'User')
    }
    $config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
}
$configSecurity = Get-Acl -LiteralPath $configPath
if ($configSecurity.AreAccessRulesProtected) { throw 'El archivo de credenciales no hereda los permisos privados previstos.' }
$unexpected = @($configSecurity.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
    Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin @($owner.Value,'S-1-5-18') })
if ($unexpected.Count -ne 0) { throw 'El archivo de credenciales tiene permisos inesperados.' }

$buildId = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $buildId -notmatch '^[0-9a-f]{40}$') { throw 'No se pudo identificar la versión del agente.' }
$binaryRoot = Join-Path $agentRoot ('versions\' + $buildId)
$project = Join-Path $repository 'tools\MiBitacora.LocalInferenceAgent\MiBitacora.LocalInferenceAgent.csproj'
$running = @(Get-Process -Name 'MiBitacora.LocalInferenceAgent' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($agentRoot + '\', [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -ne 0) { throw 'El agente ya está ejecutándose. Detenelo antes de instalar otra versión.' }
& dotnet publish $project -c Release -o $binaryRoot --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'No se pudo publicar el agente.' }
$exe = Join-Path $binaryRoot 'MiBitacora.LocalInferenceAgent.exe'
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($config.Token)))
@{ LocalInferenceBridge = @{ Enabled = $true; TokenHash = $hash } } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $agentRoot 'server-settings.json') -Encoding utf8

if (-not $NoStartup) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runName = 'MiBitacoraLocalInference'
    $existing = (Get-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue).$runName
    if ($existing -and -not $existing.Contains($agentRoot)) { throw 'La entrada de inicio ya pertenece a otra instalación.' }
    New-ItemProperty -LiteralPath $runKey -Name $runName -PropertyType String -Value ('"' + $exe + '" --config "' + $configPath + '"') -Force | Out-Null
}
Start-Process -FilePath $exe -ArgumentList @('--config', ('"' + $configPath + '"')) -WindowStyle Hidden
Write-Output ('Agente instalado en ' + $agentRoot + '. Credencial privada protegida; fragmento de configuración del servidor preparado.')
if (-not $NoStartup) { Write-Output 'Se iniciará al entrar a esta cuenta de Windows. Ollama permanece en loopback.' }
