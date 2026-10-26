# Clementine

**Zero-click file converter for Windows 10 and 11.** Drag a file onto a small floating island, drop it on a
slice of the wheel that opens, and the converted copy appears next to the original. Everything runs offline.

Clementine is inspired by Tangerine: File Converter for macOS. It is an
independent project written from scratch, and it is not affiliated with LEAP STUDIO.

> The interface is in English and Italian. It follows the Windows display language, and you can change it in Settings.

## How it works

There are three ways in, and all of them use the same engine:

1. **The island.** A small round button floats on your screen, always on top. It docks to a screen edge and slides
   out of the way. Click it to see the last files you converted, convert what is on the clipboard, or open the settings.
2. **The wheel.** Drag one or more files onto the island and a radial wheel opens under the cursor. Drop on a slice to
   run that operation. Hold a key while dragging to switch ring:
   - no key: **Convert** (only the formats that make sense for those files);
   - `Ctrl`: **Tools** (compress, resize, crop, annotate, trim, merge PDFs…);
   - `Alt`: **Actions** (copy to, move to, share, open a terminal here…).

   Without a keyboard, rest on the centre of the wheel for 1.5 s to cycle the rings.
3. **Right-click in File Explorer.** A "Clementine" entry lists the same operations. On Windows 11 it is under
   "Show more options".

Results are saved next to the original, for example `photo.heic` → `photo.jpg` or `video.mp4` → `video (compresso).mp4`.
**Clementine never overwrites the original.** If the name is taken, it adds ` (2)`.

## Formats

| Type | Input | Output |
|---|---|---|
| Images | jpg png webp heic tiff avif bmp svg gif | jpg png webp heic\* tiff avif bmp |
| Audio | mp3 m4a wav flac ogg opus aiff wma | same |
| Video | mp4 mov mkv webm avi wmv gif | same, plus gif |
| PDF | pdf | docx txt md jpg png |
| Documents\*\* | docx doc odt rtf txt md pages | docx odt pdf txt md epub |
| Presentations\*\* | pptx ppt odp | pptx odp pdf jpg png |
| Spreadsheets\*\* | xlsx xls ods csv tsv | xlsx ods csv tsv pdf |
| Subtitles | srt vtt | srt vtt |
| Archives | zip tar gz rar (read only) | zip tar tar.gz |

\* HEIC output needs the free "HEIF Image Extensions" and "HEVC Video Extensions" from the Microsoft Store.
\*\* Office formats need [LibreOffice](https://www.libreoffice.org) installed. Clementine finds it on its own.

**Tools** (`Ctrl` ring):
- Images: compress, compress to a size, resize, rotate, crop, annotate (arrows, boxes, text, black-out), web preset,
  images → PDF, read QR codes, edit or remove metadata.
- Video: compress, trim, speed, web preset, extract audio, extract frames.
- Audio: normalize (EBU R128), trim, mono.
- PDF: merge, split, pick and reorder pages, extract text.

The tray menu can also **record the screen** with FFmpeg, with optional microphone audio.

## Install

You need Windows 10 or 11, 64-bit. You don't need to install .NET.

### From a terminal

Open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/pasqualloccaa/clementine/main/install.ps1 | iex
```

The script downloads the latest release, checks its SHA-256 and starts the installer.

### From the download

1. Download `Clementine-win-x64.zip` from the [latest release](https://github.com/pasqualloccaa/clementine/releases/latest).
2. Extract it anywhere.
3. Double-click **Installa Clementine.cmd** ("Install Clementine").

Clementine copies itself to `%LOCALAPPDATA%\Programs\Clementine`. It adds a Start menu entry and an entry under
Settings → Apps, and can optionally start with Windows. It needs no administrator rights.

The release already includes FFmpeg and ExifTool.

> **Windows may warn you.** Clementine is not code-signed. If SmartScreen says "Windows protected your PC", choose
> **More info → Run anyway**. Smart App Control, when it is on, blocks unsigned apps entirely.

### Uninstall

Go to **Settings → Apps → Installed apps → Clementine → Uninstall**. Your settings and logs in
`%APPDATA%\Clementine` are kept.

## Privacy

Clementine has no account, no telemetry and no uploads. Every conversion runs on your PC with local tools.

## Build from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and PowerShell 7.

```powershell
git clone https://github.com/pasqualloccaa/clementine.git
cd clementine
pwsh installer/build.ps1
```

`build.ps1` does three things:
- downloads FFmpeg and ExifTool into `tools\` the first time, and checks their checksums;
- publishes a self-contained single-file build to `.build\Clementine`;
- zips the build to `.build\Clementine-win-x64.zip`.

To get only the tools, run `tools/get-tools.ps1`.

To run the tests:

```powershell
dotnet test src/Clementine.Tests
```

- Some tests run the real FFmpeg, ExifTool and LibreOffice. The LibreOffice tests are skipped if it is not installed.
- One test records 2 seconds of the screen into a temporary folder.
- The HEIC speed test runs only with `CLEMENTINE_PERF=1`.

The code is organised in four projects:

| Project | Contents |
|---|---|
| `src/Clementine.Core` | Formats, operations, the wheel rings, the job pipeline |
| `src/Clementine.Engines` | Adapters for ImageMagick, FFmpeg, PDFium/PdfPig, LibreOffice, SharpCompress and ExifTool |
| `src/Clementine.App` | WPF: island, wheel, tray, editors, Explorer menu, single instance, self-install |
| `src/Clementine.Tests` | xUnit tests |

## License

Clementine is released under the [MIT License](LICENSE). The release package includes FFmpeg (GPL v3) and ExifTool
(Artistic/GPL) as separate programs. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
