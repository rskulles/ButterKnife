#!/usr/bin/env bash
# Assemble ButterKnife.app from a `dotnet publish -r osx-*` output folder.
#
#   tools/make-macos-app.sh <publish-dir> <version> <out-dir> [arm64|x86_64]
#
# Layout: Contents/MacOS holds ButterKnifeMenu (the menu bar helper, compiled here from packaging/macos/ButterKnifeMenu
# with swiftc; it is the bundle's main executable) and ButterKnife (the server it launches); wwwroot, appsettings.json
# and the static-assets manifest go in Contents/Resources (DesktopLauncher treats that folder as the content root when
# it finds itself inside a bundle). Signing and notarization are done by the caller (see
# .github/workflows/release.yml); this script only builds the folder structure and ad-hoc signs it so it runs locally.
set -euo pipefail

PUBLISH=${1:?publish dir}; VERSION=${2:?version}; OUT=${3:?out dir}; ARCH=${4:-$(uname -m)}
HERE=$(cd "$(dirname "$0")/.." && pwd)
APP="$OUT/ButterKnife.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
swiftc -O -target "$ARCH-apple-macos12.0" -framework AppKit -o "$APP/Contents/MacOS/ButterKnifeMenu" "$HERE/packaging/macos/ButterKnifeMenu/main.swift"
cp "$PUBLISH/ButterKnife" "$APP/Contents/MacOS/ButterKnife"
chmod +x "$APP/Contents/MacOS/ButterKnife"
# Native libraries published beside the executable (SQLite) go next to it inside the bundle.
cp "$PUBLISH"/*.dylib "$APP/Contents/MacOS/" 2>/dev/null || true
cp -R "$PUBLISH/wwwroot" "$APP/Contents/Resources/wwwroot"
cp "$PUBLISH/appsettings.json" "$PUBLISH/ButterKnife.staticwebassets.endpoints.json" "$APP/Contents/Resources/"
cp "$HERE/packaging/macos/ButterKnife.icns" "$HERE/packaging/macos/MenuIcon.png" "$HERE/packaging/macos/MenuIcon@2x.png" "$APP/Contents/Resources/"
cp "$PUBLISH/LICENSE.md" "$PUBLISH/THIRD-PARTY-NOTICES.md" "$APP/Contents/Resources/"
sed "s/__VERSION__/$VERSION/g" "$HERE/packaging/macos/Info.plist" > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

# Ad-hoc signatures so the bundle is at least internally consistent; the release workflow re-signs with Developer ID.
for lib in "$APP"/Contents/MacOS/*.dylib; do [ -e "$lib" ] && codesign --force --sign - "$lib"; done
codesign --force --sign - --entitlements "$HERE/packaging/macos/entitlements.plist" "$APP/Contents/MacOS/ButterKnife"
codesign --force --sign - "$APP/Contents/MacOS/ButterKnifeMenu"
codesign --force --sign - "$APP"
echo "built $APP"
