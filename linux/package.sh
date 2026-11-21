#!/usr/bin/env bash
# Packs the release tarball (dist/filelabs-<v>-linux-x64.tar.gz, from build.sh) for the three big families, with each
# family's own tool, so run it where that tool is: dpkg-deb (Debian, Ubuntu, Mint) makes the .deb, rpmbuild (Fedora,
# openSUSE) the .rpm, makepkg (Arch, CachyOS, Manjaro) the .pkg.tar.zst from packaging/arch/PKGBUILD, the same file the
# AUR package uses. A family whose tool is missing is skipped and said so.
#
# All three install the same thing: the self-contained build in /opt/filelabs, /usr/bin/filelabs, the desktop entry and
# the icon. The binary is a .NET single-file bundle and must not be stripped (stripping cuts the bundle off).
set -euo pipefail
cd "$(dirname "$0")"
V=$(grep -oP '(?<=<Version>)[^<]+' FileLabs.Linux.csproj)
TAR=dist/filelabs-$V-linux-x64.tar.gz
[ -f "$TAR" ] || { echo "no $TAR: run build.sh first" >&2; exit 1; }
SUMMARY="Two-pane file manager whose copies ask about conflicts once"
URL=https://protagonistlabs.app/filelabs/
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT

# the tree every package installs, under $1
lay_out() {
    install -d "$1/opt/filelabs" "$1/usr/bin" "$1/usr/share/applications" "$1/usr/share/icons/hicolor/256x256/apps"
    tar -xzf "$TAR" -C "$1/opt/filelabs"
    chmod 755 "$1/opt/filelabs/filelabs"
    ln -s /opt/filelabs/filelabs "$1/usr/bin/filelabs"
    install -m 644 "$1/opt/filelabs/filelabs.desktop" "$1/usr/share/applications/filelabs.desktop"
    install -m 644 "$1/opt/filelabs/filelabs.png" "$1/usr/share/icons/hicolor/256x256/apps/filelabs.png"
}

if command -v dpkg-deb >/dev/null; then
    R=$WORK/deb; lay_out "$R"; install -d "$R/DEBIAN"
    cat > "$R/DEBIAN/control" <<EOF
Package: filelabs
Version: $V
Architecture: amd64
Maintainer: Protagonist Labs <hello@protagonistlabs.app>
Installed-Size: $(du -sk "$R" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, libx11-6, libxrandr2, libxi6, libxcursor1, libice6, libsm6, libfontconfig1, libgl1
Recommends: gstreamer1.0-plugins-base, gstreamer1.0-plugins-good, poppler-utils, xdg-utils
Section: utils
Priority: optional
Homepage: $URL
Description: $SUMMARY
 File Labs shows two folders side by side, sizes every folder, filters as you
 type, and copies with one question for all the conflicts, pause, cancel and
 Undo. Video, music, pictures, PDFs and archives preview with Space.
EOF
    dpkg-deb --root-owner-group -Zxz --build "$R" "dist/filelabs_${V}_amd64.deb" >/dev/null && echo "dist/filelabs_${V}_amd64.deb"
else echo "skipped .deb: no dpkg-deb here"; fi

if command -v rpmbuild >/dev/null; then
    R=$WORK/rpmroot; lay_out "$R"
    cat > "$WORK/filelabs.spec" <<EOF
Name:           filelabs
Version:        $V
Release:        1
Summary:        $SUMMARY
License:        Proprietary
URL:            $URL
BuildArch:      x86_64
AutoReqProv:    no
Requires:       libX11.so.6()(64bit), libXrandr.so.2()(64bit), libXi.so.6()(64bit), libXcursor.so.1()(64bit), libICE.so.6()(64bit), libSM.so.6()(64bit), libfontconfig.so.1()(64bit), libGL.so.1()(64bit), libstdc++.so.6()(64bit)
Recommends:     gstreamer1-plugins-good, poppler-utils, xdg-utils
%global debug_package %{nil}
%global __os_install_post %{nil}

%description
File Labs shows two folders side by side, sizes every folder, filters as you type,
and copies with one question for all the conflicts, pause, cancel and Undo. Video,
music, pictures, PDFs and archives preview with Space.

%install
cp -a $R/. %{buildroot}/

%files
/opt/filelabs
/usr/bin/filelabs
/usr/share/applications/filelabs.desktop
/usr/share/icons/hicolor/256x256/apps/filelabs.png
EOF
    rpmbuild -bb --quiet --define "_topdir $WORK/rpmbuild" "$WORK/filelabs.spec"
    cp "$WORK"/rpmbuild/RPMS/x86_64/filelabs-$V-1.x86_64.rpm dist/ && echo "dist/filelabs-$V-1.x86_64.rpm"
else echo "skipped .rpm: no rpmbuild here"; fi

if command -v makepkg >/dev/null; then
    R=$WORK/arch; install -d "$R"
    SUM=$(sha256sum "$TAR" | cut -d' ' -f1)
    sed -e "s/^pkgver=.*/pkgver=$V/" -e "s/^sha256sums=.*/sha256sums=('$SUM')/" packaging/arch/PKGBUILD > "$R/PKGBUILD"
    cp "$TAR" "$R/"   # makepkg takes the local file of the same name instead of downloading it
    (cd "$R" && makepkg -f --nodeps --noconfirm >/dev/null 2>&1) && cp "$R"/filelabs-bin-$V-1-x86_64.pkg.tar.zst dist/ && echo "dist/filelabs-bin-$V-1-x86_64.pkg.tar.zst"
    cp "$R/PKGBUILD" dist/PKGBUILD   # with this version's checksum, for the AUR
else echo "skipped Arch: no makepkg here"; fi
