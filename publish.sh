#!/usr/bin/env bash
# Builds shareable releases into ./dist
#   ./publish.sh         -> macOS Apple Silicon .dmg + Windows Setup.exe + Windows portable .zip
#   ./publish.sh mac     -> macOS only
#   ./publish.sh win     -> Windows only
# Signing: set CODESIGN_IDENTITY="Developer ID Application: ..." to sign the Mac app (ad-hoc otherwise).
set -euo pipefail
cd "$(dirname "$0")"
TARGET="${1:-all}"
APPPROJ="TrafficScope.App/TrafficScope.App.csproj"
SETUPPROJ="TrafficScope.Setup/TrafficScope.Setup.csproj"
VERSION="$(sed -nE 's/.*<Version>(.*)<\/Version>.*/\1/p' Directory.Build.props | head -1)"
COMMON=(-c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false)
mkdir -p dist

publish_win() {
  echo "==> Windows x64 $VERSION"
  rm -rf dist/win-x64 dist/win-setup
  dotnet publish "$APPPROJ" "${COMMON[@]}" -r win-x64 -o dist/win-x64
  local payload; payload="$(pwd)/dist/win-x64/TrafficScope.exe"
  dotnet publish "$SETUPPROJ" "${COMMON[@]}" -r win-x64 -o dist/win-setup -p:PayloadPath="$payload"
  cp dist/win-setup/TrafficScope-Setup.exe "dist/TrafficScope-${VERSION}-Windows-Setup.exe"
  rm -f "dist/TrafficScope-${VERSION}-Windows-Portable.zip"
  (cd dist/win-x64 && zip -q -9 "../TrafficScope-${VERSION}-Windows-Portable.zip" TrafficScope.exe)
  echo "    dist/TrafficScope-${VERSION}-Windows-Setup.exe"
  echo "    dist/TrafficScope-${VERSION}-Windows-Portable.zip"
}

publish_mac() {
  echo "==> macOS arm64 (Apple Silicon) $VERSION"
  rm -rf dist/osx-arm64 dist/TrafficScope.app
  dotnet publish "$APPPROJ" "${COMMON[@]}" -r osx-arm64 -o dist/osx-arm64
  local APP="dist/TrafficScope.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp dist/osx-arm64/TrafficScope "$APP/Contents/MacOS/TrafficScope"
  chmod +x "$APP/Contents/MacOS/TrafficScope"
  cp dist/osx-arm64/*.dylib "$APP/Contents/MacOS/" 2>/dev/null || true
  cp TrafficScope.App/Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
  cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>TrafficScope</string>
  <key>CFBundleDisplayName</key><string>TrafficScope</string>
  <key>CFBundleIdentifier</key><string>com.lifetrenz.trafficscope</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleExecutable</key><string>TrafficScope</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>© 2026 Lifetrenz</string>
</dict></plist>
PLIST
  local SIGN_ID="${CODESIGN_IDENTITY:--}"
  codesign --force --deep --sign "$SIGN_ID" "$APP"
  codesign --verify --deep --strict "$APP" && echo "    signature OK (identity: $SIGN_ID)"

  local DMG="dist/TrafficScope-${VERSION}-macOS-AppleSilicon.dmg"
  rm -f "$DMG"
  local STAGE; STAGE="$(mktemp -d)"
  cp -R "$APP" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  cat > "$STAGE/How to open TrafficScope.txt" <<TXT
TrafficScope ${VERSION} for Mac (Apple Silicon: M1, M2, M3, M4 and later)

1. Drag TrafficScope into the Applications folder.
2. Open Applications and double-click TrafficScope.
   macOS will say it cannot verify the developer, because this build is not yet
   signed with an Apple Developer ID. Click Done (not Move to Bin), then:
     System Settings > Privacy & Security > scroll down > "TrafficScope was blocked"
     > Open Anyway > enter your password > Open.
   You only need to do this once.
3. In TrafficScope press "Install certificate" and approve the macOS prompt.
4. Switch on the apps to inspect, press Record, use the app, press Stop.

Tips
- Optional: Tools > Settings > Proxy permission stops the password prompt on Record/Stop.
- If TrafficScope ever quits while recording, open it again and press "Fix now"
  on the red bar to switch your proxy off.
TXT
  hdiutil create -quiet -volname "TrafficScope ${VERSION}" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
  rm -rf "$STAGE"
  echo "    $DMG"
}

case "$TARGET" in
  win) publish_win ;;
  mac) publish_mac ;;
  all) publish_mac; publish_win ;;
  *) echo "usage: $0 [mac|win|all]"; exit 1 ;;
esac

(cd dist && shasum -a 256 TrafficScope-"${VERSION}"-* > "SHA256SUMS-${VERSION}.txt" && echo "==> checksums: dist/SHA256SUMS-${VERSION}.txt")
