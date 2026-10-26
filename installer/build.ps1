# Pubblica Clementine (autonoma: non serve .NET installato) in .build\Clementine, con "Installa Clementine.cmd".
# Niente Setup.exe: Clementine si installa da sé (Shell\SelfInstall.cs), perché Smart App Control blocca
# l'eseguibile temporaneo non firmato di Inno Setup.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" } # l'SDK sta qui su questo PC
$out = "$root\.build\Clementine"

& "$root\tools\get-tools.ps1" | Out-Null # ffmpeg ed ExifTool finiscono nel pacchetto: chi clona il repo non li ha ancora

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
# un solo exe con dentro tutte le nostre DLL: Smart App Control blocca le DLL appena compilate, l'exe no.
# Le DLL native di terze parti (Magick, PDFium, WPF) restano file a parte: sono note e firmate.
& $dotnet publish "$root\src\Clementine.App" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:Deterministic=false -p:DebugType=none -o $out -nologo -v q
if ($LASTEXITCODE) { throw 'publish fallito' }
Set-Content "$out\Installa Clementine.cmd" '@start "" "%~dp0Clementine.App.exe" --install' -Encoding ascii
# il LICENSE che c'era già nella cartella è quello di PDFium (da Docnet): il nostro va accanto con un altro nome
Copy-Item "$root\LICENSE" "$out\LICENSE-Clementine.txt"
Copy-Item "$root\THIRD-PARTY-NOTICES.md" $out

# lo zip da allegare alla release su GitHub (lo scarica anche install.ps1)
$zip = "$root\.build\Clementine-win-x64.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive $out $zip -CompressionLevel Optimal
Get-Item $zip | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB) } }
