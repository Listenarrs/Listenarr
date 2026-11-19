ffprobe requirement
===================

This project uses `ffprobe` (part of the FFmpeg project) to extract audio file metadata (duration, sample rate, bitrate, channels, embedded tags).

Where it comes from
-------------------

ffprobe is a NuGet dependency, `Openur.FFprobeStatic`, the same package Sonarr references. Its version is pinned in `Directory.Packages.props` like every other package. The package carries a static build per runtime identifier under `runtimes/<rid>/native/`, covering linux-x64, linux-arm64, linux-arm, linux-musl-x64, linux-musl-arm64, osx-x64, osx-arm64, win-x64, win-arm64, win-x86, freebsd-x64 and freebsd-arm64.

- A RID-specific publish (`dotnet publish -r linux-x64`, which is how release artifacts and the CI Docker images are built) places the binary next to the application.
- A RID-agnostic build (`dotnet run`, the tests, the root `Dockerfile`) keeps the `runtimes/<rid>/native/` tree, and `FfprobeBinaryLocator` looks there for the current runtime only.

Listenarr no longer downloads ffprobe at startup.

Fallback
--------

If no packaged binary matches the running platform, Listenarr uses `ffprobe` (or `ffprobe.exe`) from the `ffmpeg` directory under its config directory if one has been placed there. With neither, audio metadata extraction is disabled and the startup log says so. A binary that an older release downloaded into that directory is ignored while a packaged one exists, and is left on disk.

Version check
-------------

Before it uses a binary, Listenarr runs `ffprobe -version` and accepts it only if that exits 0 within five seconds and the first line starts with `ffprobe version`. A packaged binary that fails is reported as unavailable; the copy in the config directory is not used in its place. The startup log and the licence notice name the version this check reports, so a package bump needs no code change.

Permissions
-----------

NuGet does not preserve Unix file modes reliably, so the binary can arrive without execute permission for every user. The Docker images set it before the binary reaches an image layer (`docker/runtime/prepare-ffprobe.sh`, run in the root `Dockerfile`'s build stage and by `finalize-app.sh`; CI sets it on the runtime image's input), and on startup Listenarr sets it itself when it owns the file.

Licensing
---------

The licence texts and a notice for the packaged binary are in `licenses/ffprobe/` at the repository root, and every build and publish output carries a copy under `licenses/ffprobe/`. The notice names the FFmpeg release, configure options, patches and bundled dav1d version, and where their source is. Every packaged platform is built with `--enable-version3` and without `--enable-gpl` or `--enable-nonfree`, which FFmpeg's own licence file places under the LGPL version 3 or later. Update the notice when a package bump changes any of these; https://ffmpeg.org/legal.html covers FFmpeg's redistribution terms.
