<#
    Publishes File Labs and wraps it in an installer.

    Output: dist\FileLabs-<version>-setup.exe

    The version comes from the csproj alone, so a release is bumped in exactly one place.
#>
[CmdletBinding()]
param(
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'FileExplorer.csproj'
$publishDir = Join-Path $root 'dist\app'

[xml]$csproj = Get-Content $project
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }
Write-Host "File Labs $version" -ForegroundColor Cyan

# Self-contained: the installer must not depend on a .NET runtime being present.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$size = (Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("  published: {0:N0} MB in dist\app" -f $size) -ForegroundColor DarkGray

if ($SkipInstaller) { return }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found - install Inno Setup 6" }

& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\FileLabs.iss') /Q
if ($LASTEXITCODE -ne 0) { throw "installer failed" }

$setup = Join-Path $root "dist\FileLabs-$version-setup.exe"
Write-Host ("  installer: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
