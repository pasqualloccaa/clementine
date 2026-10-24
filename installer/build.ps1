# Pubblica Clementine (autonoma: non serve .NET installato) e compila l'installer in .build\installer\Clementine-Setup.exe
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" } # l'SDK sta qui su questo PC
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 non trovato: winget install JRSoftware.InnoSetup --scope user' }

Remove-Item "$root\.build\publish" -Recurse -Force -ErrorAction SilentlyContinue
# Deterministic=false: binari nuovi a ogni build (Smart App Control a volte blocca un hash e non quello dopo)
& $dotnet publish "$root\src\Clementine.App" -c Release -r win-x64 --self-contained true -p:Deterministic=false -o "$root\.build\publish" -nologo -v q
if ($LASTEXITCODE) { throw 'publish fallito' }
& $iscc /Q "$PSScriptRoot\Clementine.iss"
if ($LASTEXITCODE) { throw 'Inno Setup fallito' }
Get-Item "$root\.build\installer\Clementine-Setup.exe"
