# Pubblica Clementine (autonoma: non serve .NET installato) in .build\Clementine, con "Installa Clementine.cmd".
# Niente Setup.exe: Clementine si installa da sé (Shell\SelfInstall.cs), perché Smart App Control blocca
# l'eseguibile temporaneo non firmato di Inno Setup.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" } # l'SDK sta qui su questo PC
$out = "$root\.build\Clementine"

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
# un solo exe con dentro tutte le nostre DLL: Smart App Control blocca le DLL appena compilate, l'exe no.
# Le DLL native di terze parti (Magick, PDFium, WPF) restano file a parte: sono note e firmate.
& $dotnet publish "$root\src\Clementine.App" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:Deterministic=false -o $out -nologo -v q
if ($LASTEXITCODE) { throw 'publish fallito' }
Set-Content "$out\Installa Clementine.cmd" '@start "" "%~dp0Clementine.App.exe" --install' -Encoding ascii
Get-Item "$out\Installa Clementine.cmd"
