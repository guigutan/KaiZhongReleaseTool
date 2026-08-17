param(
    [string]$Version = '1.0.0',
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts\Server"
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishDirectory = Join-Path $OutputDirectory 'KaiZhongReleaseToolServerFiles'
$updaterDirectory = Join-Path $OutputDirectory 'Updater'

Remove-Item -LiteralPath $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $updaterDirectory -Force | Out-Null

dotnet publish (Join-Path $repositoryRoot 'KaiZhongReleaseTool.Server\KaiZhongReleaseTool.Server.csproj') -c Release -o $publishDirectory -p:Version=$Version --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed. Package creation stopped.' }

dotnet publish (Join-Path $repositoryRoot 'KaiZhongReleaseTool.Server.Updater\KaiZhongReleaseTool.Server.Updater.csproj') -c Release -o $updaterDirectory -p:Version=$Version --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed. Package creation stopped.' }

Copy-Item -Path (Join-Path $updaterDirectory '*') -Destination $publishDirectory -Recurse -Force
Copy-Item -Path (Join-Path $PSScriptRoot '*.bat') -Destination $publishDirectory -Force
Set-Content -LiteralPath (Join-Path $publishDirectory 'server-version.txt') -Value $Version -Encoding UTF8 -NoNewline

$packagePath = Join-Path $OutputDirectory "KaiZhongReleaseTool.Server-$Version.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $packagePath -CompressionLevel Optimal
Write-Host "服务端安装目录：$publishDirectory"
Write-Host "后续自动升级包：$packagePath"
