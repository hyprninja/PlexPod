#!/bin/bash
# Builds the ffmpeg that ships inside PlexPod.app (decision D19): Apple Silicon, macOS 13 or later,
# LGPL only (no GPL or non-free parts), with soxr linked in statically. The result depends on nothing
# but macOS itself. Output in installer/ffmpeg-mac/build/dist:
#   ffmpeg          the program
#   NOTICE.txt      versions, licences, configure line, where the source is
#   sources/        the exact source archives used (ship them with each release: LGPL §6)
# Sources come from Homebrew's cache when present, else from the projects' own sites, and are always
# checked against the SHA-256 below. Re-running reuses a finished build. Needs Xcode's command line
# tools and cmake (brew install cmake).
set -euo pipefail

FFMPEG_VERSION="9.0.2"
FFMPEG_URL="https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz"
FFMPEG_SHA256="8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e"
SOXR_VERSION="0.1.3"
SOXR_URL="https://downloads.sourceforge.net/project/soxr/soxr-$SOXR_VERSION-Source.tar.xz"
SOXR_SHA256="b111c15fdc8c029989330ff559184198c161100a59312f5dc19ddeb9b5a15889"
MACOS_MIN="13.0"

here="$(cd "$(dirname "$0")" && pwd)"
dist="$here/build/dist"
# Compiled in a fixed folder without spaces: soxr's CMake files and ffmpeg's configure break on
# them, and a fixed path keeps the configuration line ffmpeg reports the same on every Mac.
build="/tmp/plexpod-ffmpeg-build"
stamp="ffmpeg $FFMPEG_VERSION + soxr $SOXR_VERSION, macOS $MACOS_MIN arm64"

if [ -x "$dist/ffmpeg" ] && [ "$(cat "$dist/.stamp" 2>/dev/null)" = "$stamp" ]; then
    echo "Bundled ffmpeg is up to date: $dist/ffmpeg"
    exit 0
fi

command -v cmake >/dev/null || { echo "cmake is needed: brew install cmake" >&2; exit 1; }
rm -rf "$build" "$dist"
mkdir -p "$build/src" "$build/prefix" "$dist/sources"

# fetch <url> <sha256> <file name>: Homebrew's cache first (already verified by Homebrew), then the web.
fetch() {
    local url="$1" sha="$2" name="$3" target="$dist/sources/$3" cached=""
    if command -v brew >/dev/null; then
        cached="$(ls "$(brew --cache)"/downloads/*--"$name" 2>/dev/null | head -1 || true)"
    fi
    if [ -n "$cached" ]; then
        cp "$cached" "$target"
    else
        curl -fsSL --retry 3 -o "$target" "$url"
    fi
    echo "$sha  $target" | shasum -a 256 -c - >/dev/null || { echo "Checksum mismatch for $name" >&2; exit 1; }
}

fetch "$FFMPEG_URL" "$FFMPEG_SHA256" "ffmpeg-$FFMPEG_VERSION.tar.xz"
fetch "$SOXR_URL" "$SOXR_SHA256" "soxr-$SOXR_VERSION-Source.tar.xz"

jobs="$(sysctl -n hw.ncpu)"
export MACOSX_DEPLOYMENT_TARGET="$MACOS_MIN"

# soxr: static, no OpenMP (so nothing else to ship), no tests or libsamplerate bindings.
tar -xf "$dist/sources/soxr-$SOXR_VERSION-Source.tar.xz" -C "$build/src"
cmake -S "$build/src/soxr-$SOXR_VERSION-Source" -B "$build/soxr" \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$build/prefix" \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET="$MACOS_MIN" \
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DBUILD_SHARED_LIBS=OFF -DWITH_OPENMP=OFF -DBUILD_TESTS=OFF -DWITH_LSR_BINDINGS=OFF -DBUILD_EXAMPLES=OFF \
    >"$build/soxr-configure.log"
cmake --build "$build/soxr" -j "$jobs" >"$build/soxr-build.log"
cmake --install "$build/soxr" >/dev/null

# ffmpeg: every native decoder, encoder and filter (Plex can serve any audio format), LGPL only,
# nothing detected automatically (so no Homebrew library sneaks in), system zlib for PNG art.
tar -xf "$dist/sources/ffmpeg-$FFMPEG_VERSION.tar.xz" -C "$build/src"
configure_flags=(
    --prefix="$build/prefix"
    --arch=arm64 --target-os=darwin --cc=clang
    --disable-autodetect --enable-zlib --enable-libsoxr
    --disable-network --disable-doc --disable-ffplay --disable-ffprobe --disable-debug
    --enable-static --disable-shared
    --extra-cflags="-I$build/prefix/include -mmacosx-version-min=$MACOS_MIN"
    --extra-ldflags="-L$build/prefix/lib -mmacosx-version-min=$MACOS_MIN"
)
(
    cd "$build/src/ffmpeg-$FFMPEG_VERSION"
    ./configure "${configure_flags[@]}" >"$build/ffmpeg-configure.log"
    make -j "$jobs" ffmpeg >"$build/ffmpeg-build.log" 2>&1
)
cp "$build/src/ffmpeg-$FFMPEG_VERSION/ffmpeg" "$dist/ffmpeg"
strip "$dist/ffmpeg"

# Must stand alone: only system libraries and frameworks.
if otool -L "$dist/ffmpeg" | tail -n +2 | grep -v -E '^\s+(/usr/lib/|/System/Library/)'; then
    echo "The bundled ffmpeg links something outside macOS (above)." >&2
    exit 1
fi

version="$("$dist/ffmpeg" -hide_banner -version)"
grep -q -- '--enable-libsoxr' <<<"$version" || { echo "soxr is missing from the build." >&2; exit 1; }
grep -q -- '--enable-gpl\|--enable-nonfree' <<<"$version" && { echo "The build must stay LGPL." >&2; exit 1; }

cat >"$dist/NOTICE.txt" <<NOTICE
PlexPod for Mac includes ffmpeg, built by PlexPod's own script (installer/ffmpeg-mac/build-ffmpeg.sh).

FFmpeg $FFMPEG_VERSION  https://ffmpeg.org  licensed under the GNU Lesser General Public License,
version 2.1 or later (LGPL-2.1-or-later). No GPL or non-free components are enabled.
libsoxr $SOXR_VERSION  https://sourceforge.net/projects/soxr/  licensed under the GNU Lesser General
Public License, version 2.1 or later. Linked into ffmpeg statically.

The exact source code used is published with every PlexPod release (ffmpeg-$FFMPEG_VERSION.tar.xz,
soxr-$SOXR_VERSION-Source.tar.xz), unmodified, together with the build script. You may rebuild ffmpeg
from it and replace Contents/MacOS/ffmpeg in PlexPod.app with your own build.

SHA-256
  ffmpeg-$FFMPEG_VERSION.tar.xz      $FFMPEG_SHA256
  soxr-$SOXR_VERSION-Source.tar.xz  $SOXR_SHA256

Configuration
$(grep -m1 '^configuration:' <<<"$version")

Full licence texts: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
NOTICE
cp "$build/src/ffmpeg-$FFMPEG_VERSION/COPYING.LGPLv2.1" "$dist/COPYING.LGPLv2.1"

echo "$stamp" >"$dist/.stamp"
echo "Built $dist/ffmpeg ($(du -h "$dist/ffmpeg" | cut -f1))"
