param([string]$Destination = (Join-Path $PSScriptRoot '..\media-tools'))
$ErrorActionPreference = 'Stop'
$version = '9.0.2'
$expected = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$uri = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-$version-essentials_build.zip"
$cache = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.artifacts\media-tools'))
[IO.Directory]::CreateDirectory($cache) | Out-Null
$archive = Join-Path $cache "ffmpeg-$version.zip"
if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
    Invoke-WebRequest -Uri $uri -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'El SHA256 de FFmpeg no coincide.' }
$expanded = Join-Path $cache "ffmpeg-$version"
if (!(Test-Path -LiteralPath $expanded)) { Expand-Archive -LiteralPath $archive -DestinationPath $expanded }
$package = Get-ChildItem -LiteralPath $expanded -Directory | Select-Object -First 1
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Destination)) | Out-Null
foreach ($file in @('ffmpeg.exe', 'ffprobe.exe')) {
    Copy-Item -LiteralPath (Join-Path $package.FullName "bin\$file") -Destination (Join-Path $Destination $file) -Force
}
Copy-Item -LiteralPath (Join-Path $package.FullName 'LICENSE') -Destination (Join-Path $Destination 'LICENSE-FFmpeg.txt') -Force
@{Version=$version; Archive=$uri; SHA256=$expected; Source='https://github.com/FFmpeg/FFmpeg/commit/946fcce07b'; BuildInformation='https://www.gyan.dev/ffmpeg/builds/'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'provenance.json') -Encoding utf8
Write-Output "FFmpeg $version verificado y disponible en $([IO.Path]::GetFullPath($Destination))"
