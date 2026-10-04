# Third-party notices

## .NET / WPF

Microsoft .NET 10 runtime and Windows Desktop / WPF runtime are included through self-contained publishing. Their primary license is MIT. Some runtime components carry additional notices.

- License: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- Runtime third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
- WPF source and license: https://github.com/dotnet/wpf
- Runtime source: https://github.com/dotnet/runtime

The portable bundle contains the installed distribution license and additional notice files when available.

## FFmpeg and FFprobe

Bundled executables are unmodified copies of the locally installed Gyan.dev FFmpeg 8.1.1 full build. This distribution enables GPL and version3 components and carries the GNU GPL version 3 license. FFmpeg is run as a separate process; it is not embedded into the application as a linked library.

- Distribution and build details: https://www.gyan.dev/ffmpeg/builds/
- FFmpeg source release: https://ffmpeg.org/releases/ffmpeg-8.1.1.tar.xz
- FFmpeg source repository: https://git.ffmpeg.org/ffmpeg.git
- FFmpeg licensing information: https://ffmpeg.org/legal.html
- Build infrastructure for external libraries: https://github.com/m-ab-s/media-autobuild_suite

The original distribution LICENSE and README are copied into `licenses`. The package also records the exact build configuration and version output in `FFmpeg-build.txt`, and includes the matching FFmpeg source archive. Individual codec libraries and external components carry their own upstream notices described by the distribution and build infrastructure.

These notices apply to third-party components, independently of the VideoArchive source license.
