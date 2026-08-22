#!/usr/bin/env bash
#
# Build the macOS .pkg installer for Renzy's Stash Kit Maker.
#
#   packaging/macos/build-pkg.sh [--arch arm64|x64|universal] [--version 0.9.7]
#
# Requires: macOS with the .NET 10 SDK and Xcode command line tools
# (pkgbuild / productbuild / codesign ship with the CLT).
#
# The resulting installer is UNSIGNED and NOT NOTARIZED. To sign it, set
# SIGN_APP / SIGN_PKG to your identity names before running:
#
#   SIGN_APP="Developer ID Application: Your Name (TEAMID)" \
#   SIGN_PKG="Developer ID Installer: Your Name (TEAMID)" \
#   packaging/macos/build-pkg.sh
#
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$REPO/packaging/macos"

ARCH="arm64"
VERSION="$(git -C "$REPO" describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || echo 0.0.0)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch)    ARCH="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

APP_NAME="Renzys Stash Kit Maker"
EXE="RenzysStashKitMaker"
BUNDLE_ID="com.pradarenzy.renzysstashkitmaker"
PKG_ID="${BUNDLE_ID}.pkg"
PROJ="$REPO/src/StashKitMaker.Mac/StashKitMaker.Mac.csproj"

WORK="$REPO/outputs/.pkgbuild"
ROOT="$WORK/root"
APP="$ROOT/$APP_NAME.app"
OUT="$REPO/outputs/${EXE}-${VERSION}-macos-${ARCH}.pkg"

rm -rf "$WORK"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$WORK/scripts"

publish() { # $1 = RID, $2 = destination
  dotnet publish "$PROJ" -c Release -r "$1" --self-contained true \
    -p:DebugType=none -p:Version="$VERSION" -o "$2"
}

case "$ARCH" in
  arm64) publish osx-arm64 "$APP/Contents/MacOS" ;;
  x64)   publish osx-x64   "$APP/Contents/MacOS" ;;
  universal)
    publish osx-arm64 "$WORK/pub-arm64"
    publish osx-x64   "$WORK/pub-x64"
    cp -a "$WORK/pub-arm64/." "$APP/Contents/MacOS/"
    # lipo every Mach-O that differs between the two slices
    ( cd "$WORK/pub-arm64" && find . -type f ) | while read -r f; do
      [[ -f "$WORK/pub-x64/$f" ]] || continue
      if file "$f" 2>/dev/null | grep -q 'Mach-O'; then
        if ! cmp -s "$WORK/pub-arm64/$f" "$WORK/pub-x64/$f"; then
          lipo -create "$WORK/pub-arm64/$f" "$WORK/pub-x64/$f" \
               -output "$APP/Contents/MacOS/$f" 2>/dev/null || true
        fi
      fi
    done
    ;;
  *) echo "unsupported --arch: $ARCH" >&2; exit 2 ;;
esac

rm -f "$APP/Contents/MacOS/"*.pdb

# ---------- bundle metadata ----------
sed "s/__VERSION__/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
cp "$HERE/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
printf 'APPL????' > "$APP/Contents/PkgInfo"

find "$ROOT" -type d -exec chmod 0755 {} +
find "$ROOT" -type f -exec chmod 0644 {} +
chmod 0755 "$APP/Contents/MacOS/$EXE"
[[ -f "$APP/Contents/MacOS/createdump" ]] && chmod 0755 "$APP/Contents/MacOS/createdump"
find "$APP/Contents/MacOS" -name '*.dylib' -exec chmod 0755 {} +

# ---------- sign ----------
# lipo strips signatures, and any Developer ID signature must be applied
# inner-binaries-first. Ad-hoc ("-") is enough to satisfy the arm64 loader.
IDENTITY="${SIGN_APP:--}"
find "$APP/Contents/MacOS" \( -name '*.dylib' -o -name 'createdump' \) \
  -exec codesign --force --timestamp=none -s "$IDENTITY" {} +
codesign --force --options runtime --timestamp=none -s "$IDENTITY" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

# ---------- postinstall ----------
cat > "$WORK/scripts/postinstall" <<SH
#!/bin/bash
/usr/bin/xattr -dr com.apple.quarantine "/Applications/$APP_NAME.app" 2>/dev/null || true
exit 0
SH
chmod 0755 "$WORK/scripts/postinstall"

# ---------- component + product ----------
pkgbuild --root "$ROOT" \
         --scripts "$WORK/scripts" \
         --identifier "$PKG_ID" \
         --version "$VERSION" \
         --install-location /Applications \
         "$WORK/component.pkg"

HOSTARCH="$ARCH"
[[ "$ARCH" == "universal" ]] && HOSTARCH="arm64,x86_64"
[[ "$ARCH" == "x64" ]] && HOSTARCH="x86_64"

sed -e "s/__VERSION__/$VERSION/g" \
    -e "s/__PKG_ID__/$PKG_ID/g" \
    -e "s/__HOST_ARCHS__/$HOSTARCH/g" \
    "$HERE/distribution.xml" > "$WORK/distribution.xml"

# productbuild wants every referenced resource in one directory
mkdir -p "$WORK/resources"
cp "$HERE/resources/"*.html "$WORK/resources/"
cp "$REPO/LICENSE" "$WORK/resources/license.txt"

PRODUCT_SIGN=()
[[ -n "${SIGN_PKG:-}" ]] && PRODUCT_SIGN=(--sign "$SIGN_PKG")

productbuild --distribution "$WORK/distribution.xml" \
             --resources "$WORK/resources" \
             --package-path "$WORK" \
             "${PRODUCT_SIGN[@]}" \
             "$OUT"

rm -rf "$WORK"
echo
echo "built: $OUT"
shasum -a 256 "$OUT"
