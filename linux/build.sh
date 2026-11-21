#!/usr/bin/env bash
# Builds File Labs for Linux on a Linux machine: dist/filelabs-<v>-x86_64.AppImage and dist/filelabs-<v>-linux-x64.tar.gz.
# Needs the .NET 10 SDK; appimagetool is fetched once. Runs the self-test of the published build before packing.
set -euo pipefail
cd "$(dirname "$0")"
V=$(grep -oP '(?<=<Version>)[^<]+' FileLabs.Linux.csproj)
OUT=dist; PUB=$OUT/publish; APP=$OUT/AppDir
rm -rf "$PUB" "$APP"; mkdir -p "$OUT"

dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PUB"
"$PUB/filelabs" --selftest
cp Assets/filelabs.png filelabs.desktop "$PUB/"
tar -C "$PUB" -czf "$OUT/filelabs-$V-linux-x64.tar.gz" .

mkdir -p "$APP/usr/bin" "$APP/usr/share/applications" "$APP/usr/share/icons/hicolor/256x256/apps"
cp -r "$PUB"/. "$APP/usr/bin/"
cp Assets/filelabs.png "$APP/filelabs.png"
cp Assets/filelabs.png "$APP/usr/share/icons/hicolor/256x256/apps/filelabs.png"
cp filelabs.desktop "$APP/"
cp filelabs.desktop "$APP/usr/share/applications/"
printf '#!/bin/sh\nexec "$(dirname "$(readlink -f "$0")")/usr/bin/filelabs" "$@"\n' > "$APP/AppRun"
chmod +x "$APP/AppRun"

TOOL=$OUT/appimagetool
if [ ! -x "$TOOL" ]; then
    curl -fsSL -o "$TOOL" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$TOOL"
fi
ARCH=x86_64 "$TOOL" --appimage-extract-and-run "$APP" "$OUT/filelabs-$V-x86_64.AppImage"
# the .deb, .rpm and Arch package this machine can make (package.sh); the others on their own distros
bash package.sh
(shopt -s nullglob; sha256sum "$OUT"/filelabs-$V-* "$OUT"/filelabs_${V}_* "$OUT"/filelabs-bin-$V-*)
