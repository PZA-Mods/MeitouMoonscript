# MeitouMoonscript: build the code mod and stage its install folder. Author: PZA. Licence: GPLv3 or later.
#
#   .\build.ps1            build Release, stage out\MeitouMoonscript\ (MeitouMoonscript.dll, MoonSharp.Interpreter.dll, meitou.json,
#                          MeitouMoonscript.mod, scripts\, LICENSE, NOTICE.md)
#   .\build.ps1 -Check     also the offline check (tools\check) on the staged folder, the Euler pass (tools\euler\check, every claim of
#                          tools\euler\CLAIMS.md) and the fuzz harness (tests\fuzz). On a copy of the .NET 11 runtime inside the Meitou release;
#                          it never starts the game.
#   .\build.ps1 -Fuzz      only the fuzz harness after the build
#   -MeitouDir <dir>       the unpacked Meitou release (default: MeitouDir in mod\local.props, git-ignored)
#
# It installs nothing (README "Install"). out\ is git-ignored.
param(
    [switch]$Check,
    [switch]$Fuzz,
    [string]$MeitouDir = "",
    [string]$KenshiDir = "C:\Program Files (x86)\Steam\steamapps\common\Kenshi"
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $here
$moonSharpSha = "f767ef39db3ed93b80e6fd232eb3c9d12e045709b94f0a3795e58ef77c5fcdb1"   # MoonSharp 3.0.0-beta.1, lib/netstandard2.0 (NOTICE.md)
$localProps = Join-Path $here "local.props"
if ($MeitouDir -eq "" -and (Test-Path $localProps)) { $MeitouDir = ([xml](Get-Content $localProps -Raw)).Project.PropertyGroup.MeitouDir }
if (-not $MeitouDir) { throw "Set -MeitouDir <unpacked Meitou release> or create mod\local.props" }
$MeitouDir = [System.IO.Path]::GetFullPath($MeitouDir)
if (-not (Test-Path (Join-Path $MeitouDir "MeitouClient.dll"))) { throw "MeitouClient.dll not found in $MeitouDir" }

& dotnet build (Join-Path $here "MeitouMoonscript.csproj") -c Release -nologo "-p:MeitouDir=$MeitouDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
$moonSharp = Join-Path $nuget "moonsharp\3.0.0-beta.1\lib\netstandard2.0\MoonSharp.Interpreter.dll"
$sha = (Get-FileHash $moonSharp -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sha -ne $moonSharpSha) { throw "MoonSharp.Interpreter.dll is not the pinned build ($sha)" }

$stage = Join-Path $here "out\MeitouMoonscript"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage "scripts"), (Join-Path $stage "ui") | Out-Null
Copy-Item (Join-Path $here "bin\Release\net11.0\MeitouMoonscript.dll") $stage -Force
Copy-Item $moonSharp $stage -Force
Copy-Item (Join-Path $here "meitou.json") $stage -Force
Copy-Item (Join-Path $here "MeitouMoonscript.mod") $stage -Force
Copy-Item (Join-Path $here "scripts\*.lua") (Join-Path $stage "scripts") -Force
Copy-Item (Join-Path $here "ui\*.png") (Join-Path $stage "ui") -Force
if (Test-Path (Join-Path $here "packs")) { Copy-Item (Join-Path $here "packs") $stage -Recurse -Force }
Copy-Item (Join-Path $repo "LICENSE"), (Join-Path $repo "NOTICE.md") $stage -Force

Write-Host "staged $stage :"
Get-ChildItem $stage -Recurse -File | ForEach-Object { Write-Host ("  {0,-30} {1,8} bytes" -f $_.FullName.Substring($stage.Length + 1), $_.Length) }

if ($Check) {
    $rt = Join-Path $here "out\checkrt"
    New-Item -ItemType Directory -Force $rt | Out-Null
    # every DLL of the release (as PZA's Meitou UI fuzz kit 1.1): Meitou 0.22.2-nightly.20261122.2's MeitouClient.dll loads Silk.NET.Core in a
    # module initializer, so a list of names failed before the first check (2026-11-22)
    Copy-Item (Join-Path $MeitouDir "*.dll") $rt -Force
    $stagedDll = Join-Path $stage "MeitouMoonscript.dll"
    $stagedMs = Join-Path $stage "MoonSharp.Interpreter.dll"
    & dotnet build (Join-Path $here "tools\check\check.csproj") -c Release -nologo "-p:MeitouDir=$MeitouDir" "-p:ModDll=$stagedDll" "-p:MoonSharpDll=$stagedMs" -o (Join-Path $here "out\checkbin")
    if ($LASTEXITCODE -ne 0) { throw "check tool build failed ($LASTEXITCODE)" }
    Copy-Item (Join-Path $here "out\checkbin\check.dll"), $stagedDll, $stagedMs $rt -Force
    $cfg = '{ "runtimeOptions": { "tfm": "net11.0", "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "11.0.0" } ], "configProperties": { "System.Runtime.TieredCompilation": false } } }'
    Set-Content -Path (Join-Path $rt "check.runtimeconfig.json") -Value $cfg -Encoding ascii
    Push-Location $rt
    try { & dotnet exec --runtimeconfig check.runtimeconfig.json check.dll $stage $KenshiDir } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "check FAILED ($LASTEXITCODE)" }

    $euler = Join-Path $repo "tools\euler\check"
    & dotnet build (Join-Path $euler "check.csproj") -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw "Euler pass build failed ($LASTEXITCODE)" }
    $env:DOTNET_GCHeapHardLimit = "0x40000000"
    & dotnet (Join-Path $euler "bin\Release\net11.0\check.dll") all
    $code = $LASTEXITCODE
    Remove-Item Env:\DOTNET_GCHeapHardLimit
    if ($code -ne 0) { throw "Euler pass FAILED ($code)" }
}

if ($Fuzz -or $Check) {
    $fuzzRunner = Join-Path $here "..\..\PZAMeitouUi\fuzz\run_fuzz.ps1"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $fuzzRunner -Repo $repo -MeitouDir $MeitouDir
    if ($LASTEXITCODE -ne 0) { throw "fuzz FAILED ($LASTEXITCODE): report in tests\out\fuzz\fuzz-report.txt" }
}
