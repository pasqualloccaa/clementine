# Scarica i binari portabili in tools\ (ffmpeg, ffprobe). Rieseguibile: salta ciò che c'è già.
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot

$ffmpegDir = Join-Path $tools 'ffmpeg'
if (-not (Test-Path (Join-Path $ffmpegDir 'ffmpeg.exe'))) {
    $url = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "clementine-ffmpeg-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        $zip = Join-Path $tmp 'ffmpeg.zip'
        Write-Host "Scarico $url"
        Invoke-WebRequest $url -OutFile $zip
        $expected = (Invoke-WebRequest "$url.sha256").Content.ToString().Trim().Split()[0]
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "SHA256 non corrisponde: atteso $expected, ottenuto $actual" }
        Expand-Archive $zip (Join-Path $tmp 'x')
        New-Item -ItemType Directory -Force $ffmpegDir | Out-Null
        Get-ChildItem (Join-Path $tmp 'x') -Recurse -Include ffmpeg.exe, ffprobe.exe | Copy-Item -Destination $ffmpegDir
    }
    finally { Remove-Item $tmp -Recurse -Force }
}
& (Join-Path $ffmpegDir 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1
