# Installs the latest Clementine release from GitHub:
#   irm https://raw.githubusercontent.com/GITHUB_USER/clementine/main/install.ps1 | iex
# Downloads the release zip (FFmpeg and ExifTool included), checks its SHA-256 and runs the built-in installer.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest many times slower
$repo = 'GITHUB_USER/clementine'

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$asset = $release.assets | Where-Object name -eq 'Clementine-win-x64.zip' | Select-Object -First 1
if (-not $asset) { throw "Release $($release.tag_name) has no Clementine-win-x64.zip" }

$tmp = Join-Path ([IO.Path]::GetTempPath()) "clementine-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $tmp | Out-Null
try {
    $zip = Join-Path $tmp $asset.name
    Write-Host "Downloading Clementine $($release.tag_name) ($([math]::Round($asset.size / 1MB)) MB)..."
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip
    if ($asset.digest -like 'sha256:*') {
        $expected = $asset.digest.Substring(7)
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "SHA-256 mismatch: expected $expected, got $actual" }
    }
    Expand-Archive $zip $tmp
    $exe = Get-ChildItem $tmp -Recurse -Filter Clementine.App.exe | Select-Object -First 1
    # waits for the installer only: it copies itself to %LOCALAPPDATA%\Programs\Clementine and starts that copy
    (Start-Process $exe.FullName '--install' -PassThru).WaitForExit()
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
