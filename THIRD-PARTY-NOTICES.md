# Third-party notices

Clementine's own code is MIT-licensed (see [LICENSE](LICENSE)). The release package also ships the
following components, each under its own license.

## Bundled programs

These are separate executables that Clementine runs as child processes. They are not part of this
repository: `tools/get-tools.ps1` downloads them, and the release zip includes them in `tools\`.

| Program | License | Source |
|---|---|---|
| FFmpeg and FFprobe ("release essentials" build by gyan.dev, includes x264) | GNU GPL v3 (full text in `tools\ffmpeg\LICENSE`) | <https://ffmpeg.org/download.html>, build scripts at <https://www.gyan.dev/ffmpeg/builds/> |
| ExifTool by Phil Harvey (Windows package, with an embedded Perl) | Same terms as Perl: Artistic License or GNU GPL | <https://exiftool.org> |

## Libraries (NuGet)

| Library | License |
|---|---|
| Magick.NET (ImageMagick) | Apache 2.0 (ImageMagick License) |
| Docnet.Core (PDFium) | MIT (PDFium: BSD-3-Clause) |
| UglyToad.PdfPig | Apache 2.0 |
| SharpCompress | MIT |
| ZXing.Net | Apache 2.0 |
| Microsoft.Toolkit.Uwp.Notifications | MIT |
| Serilog, Serilog.Sinks.File | Apache 2.0 |
| .NET runtime and WPF | MIT |

## Optional

LibreOffice (MPL 2.0) is used for Office documents only if it is already installed. It is not bundled.
