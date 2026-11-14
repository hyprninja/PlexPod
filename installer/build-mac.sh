#!/bin/bash
# Builds PlexPod.app and PlexPod-<version>-arm64.dmg in installer/out-mac: a self-contained release
# publish for Apple Silicon (no .NET needed on the Mac), wrapped in an app bundle and ad-hoc signed.
# Not notarised (that needs an Apple Developer ID): the first launch needs right-click → Open, or
# System Settings → Privacy & Security → Open Anyway. Run from anywhere: installer/build-mac.sh
# Debug info is left out and source paths are mapped to /src, so no local paths end up in the binaries.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$repo/installer/out-mac"
publish="$out/publish"
project="$repo/src/PlexPod.Mac/PlexPod.Mac.csproj"

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project" | head -1)"
[ -n "$version" ] || { echo "No <Version> in PlexPod.Mac.csproj." >&2; exit 1; }

command -v dotnet >/dev/null || export PATH="$HOME/.dotnet:$PATH"

rm -rf "$out"
dotnet publish "$project" -c Release -r osx-arm64 --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false "-p:PathMap=$repo=/src" -p:ContinuousIntegrationBuild=true \
    -p:UseAppHost=true -o "$publish"

app="$out/PlexPod.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish/." "$app/Contents/MacOS/"

# Icon: the app's PNG at every size iconutil expects.
iconset="$out/PlexPod.iconset"
mkdir -p "$iconset"
png="$repo/src/PlexPod.Mac/Assets/PlexPod.png"
for size in 16 32 128 256; do
    sips -z $size $size "$png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z $double $double "$png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/PlexPod.icns"
rm -rf "$iconset"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>PlexPod</string>
    <key>CFBundleDisplayName</key><string>PlexPod</string>
    <key>CFBundleIdentifier</key><string>com.hyprninja.plexpod</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>PlexPod</string>
    <key>CFBundleIconFile</key><string>PlexPod.icns</string>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.music</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$app"

dmg="$out/PlexPod-$version-arm64.dmg"
staging="$out/dmg"
mkdir -p "$staging"
cp -R "$app" "$staging/"
ln -s /Applications "$staging/Applications"
hdiutil create -volname "PlexPod $version" -srcfolder "$staging" -ov -format UDZO "$dmg" >/dev/null
rm -rf "$staging" "$publish"

echo "Built $app"
echo "Built $dmg"
