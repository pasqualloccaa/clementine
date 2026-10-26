# Rigenera src\Clementine.App\Assets\Clementine.ico (16–256 px) da clementine.svg, con Magick.NET della build dei test.
# Serve solo se si cambia il disegno; l'.ico è nel repository.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$bin = "$root\.build\b"   # dotnet build src/Clementine.Tests -o .build/b
$env:PATH = "$bin\runtimes\win-x64\native;$env:PATH"
Add-Type -Path "$bin\Magick.NET.Core.dll"; Add-Type -Path "$bin\Magick.NET-Q8-AnyCPU.dll"
$assets = "$root\src\Clementine.App\Assets"
$settings = [ImageMagick.MagickReadSettings]::new(); $settings.BackgroundColor = [ImageMagick.MagickColors]::Transparent; $settings.Density = [ImageMagick.Density]::new(96)
$frames = [ImageMagick.MagickImageCollection]::new()
foreach ($size in 256, 64, 48, 32, 24, 16) {
    $image = [ImageMagick.MagickImage]::new("$assets\clementine.svg", $settings)
    $image.Resize([uint32]$size, [uint32]$size)
    $frames.Add($image)
}
$frames.Write("$assets\Clementine.ico")
Get-Item "$assets\Clementine.ico"
