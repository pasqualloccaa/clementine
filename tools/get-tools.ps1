# Scarica i binari portabili in tools\ (ffmpeg, ffprobe). Rieseguibile: salta ciò che c'è già.
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot

$ffmpegDir = Join-Path $tools 'ffmpeg'
if (-not (Test-Path (Join-Path $ffmpegDir 'ffmpeg.exe')) -or -not (Test-Path (Join-Path $ffmpegDir 'LICENSE'))) {
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
        # LICENSE: la GPL chiede che il testo viaggi con i binari
        Get-ChildItem (Join-Path $tmp 'x') -Recurse -Include ffmpeg.exe, ffprobe.exe, LICENSE | Copy-Item -Destination $ffmpegDir
    }
    finally { Remove-Item $tmp -Recurse -Force }
}
& (Join-Path $ffmpegDir 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1

$exifDir = Join-Path $tools 'exiftool'
if (-not (Test-Path (Join-Path $exifDir 'exiftool.exe'))) {
    $ver = (Invoke-WebRequest 'https://exiftool.org/ver.txt').Content.ToString().Trim()
    $name = "exiftool-${ver}_64.zip"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "clementine-exiftool-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        $zip = Join-Path $tmp $name
        Write-Host "Scarico $name"
        Invoke-WebRequest "https://sourceforge.net/projects/exiftool/files/$name/download" -OutFile $zip -UserAgent 'Wget'
        $sums = (Invoke-WebRequest 'https://exiftool.org/checksums.txt').Content.ToString()
        $expected = [regex]::Match($sums, "SHA2-256\($([regex]::Escape($name))\)= *([0-9a-f]{64})").Groups[1].Value
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
        if (-not $expected -or $actual -ne $expected) { throw "SHA256 non corrisponde: atteso '$expected', ottenuto $actual" }
        Expand-Archive $zip (Join-Path $tmp 'x')
        $src = Get-ChildItem (Join-Path $tmp 'x') -Directory | Select-Object -First 1
        New-Item -ItemType Directory -Force $exifDir | Out-Null
        Copy-Item (Join-Path $src.FullName 'exiftool_files') $exifDir -Recurse
        # "(-k)" nel nome farebbe attendere un tasto a fine esecuzione
        Copy-Item (Join-Path $src.FullName 'exiftool(-k).exe') (Join-Path $exifDir 'exiftool.exe')
    }
    finally { Remove-Item $tmp -Recurse -Force }
}
"exiftool $(& (Join-Path $exifDir 'exiftool.exe') -ver)"
