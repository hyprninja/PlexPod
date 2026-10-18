# Builds PlexPod-<version>-x64.msi in installer\out: a self-contained release publish (no .NET needed
# on the target PC), then the WiX 5 package. Run from anywhere: pwsh installer\build.ps1
# Debug info is left out and source paths are mapped to /src, so no local paths end up in the binaries.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out'
$publish = Join-Path $out 'publish'

[xml]$project = Get-Content (Join-Path $repo 'src\PlexPod.App\PlexPod.App.csproj')
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in PlexPod.App.csproj.' }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $repo 'src\PlexPod.App') -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:DebugSymbols=false "-p:PathMap=$repo=/src" -p:ContinuousIntegrationBuild=true `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Push-Location $repo
try {
    dotnet tool restore | Out-Null
    $msi = Join-Path $out "PlexPod-$version-x64.msi"
    dotnet tool run wix build (Join-Path $PSScriptRoot 'PlexPod.wxs') -arch x64 `
        -d "Version=$version" -d "PublishDir=$publish" -d "RepoDir=$repo" -o $msi
    if ($LASTEXITCODE -ne 0) { throw 'WiX build failed.' }
}
finally {
    Pop-Location
}

Write-Host "Built $msi"
