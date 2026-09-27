#!/bin/bash
# Mindtalk — bygg, signera med Developer ID, notarisera och packa en DMG i dist/.
#
# Notariseringen använder en notarytool-profil i Nyckelringen. Skapa den en gång
# (du skriver själv in det app-specifika lösenordet från appleid.apple.com):
#   xcrun notarytool store-credentials Mindtalk --apple-id admin@mindact.ai --team-id 679J7H9973
# Kör med NOTARIZE=0 för att hoppa över notariseringen (DMG:n fungerar då bara på din egen Mac).
set -euo pipefail

APP=Mindtalk
IDENTITY="Developer ID Application: Mindact Solutions AB (679J7H9973)"
TEAM_ID=679J7H9973
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD="$ROOT/build/release"
DIST="$ROOT/dist"
VERSION=$(grep -m1 'MARKETING_VERSION:' "$ROOT/project.yml" | sed 's/.*: *//')
DMG="$DIST/$APP-$VERSION.dmg"

cd "$ROOT"
xcodegen generate --quiet

echo "▸ Bygger $APP $VERSION (Release, Apple Silicon)"
xcodebuild -project $APP.xcodeproj -scheme $APP -configuration Release -derivedDataPath "$BUILD" \
  CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY="$IDENTITY" DEVELOPMENT_TEAM=$TEAM_ID \
  OTHER_CODE_SIGN_FLAGS="--timestamp" CODE_SIGN_INJECT_BASE_ENTITLEMENTS=NO clean build | tail -3
APP_PATH="$BUILD/Build/Products/Release/$APP.app"
codesign --verify --deep --strict "$APP_PATH"
# Notarization refuses debug entitlements; catch them here rather than after the upload.
if codesign -d --entitlements - "$APP_PATH" 2>/dev/null | grep -q get-task-allow; then
  echo "✗ Appen har get-task-allow – notariseringen skulle nekas."; exit 1
fi

echo "▸ Packar DMG"
mkdir -p "$DIST"
# dmgbuild lays out the window (background, icon positions) without scripting Finder.
DMGENV="$ROOT/build/dmgenv"
if [ ! -x "$DMGENV/bin/dmgbuild" ]; then
  python3 -m venv "$DMGENV" && "$DMGENV/bin/pip" install -q dmgbuild
fi
BACKGROUND="$BUILD/dmg-background.tiff"
tiffutil -cathidpicheck "$ROOT/scripts/dmg/background.png" "$ROOT/scripts/dmg/background@2x.png" -out "$BACKGROUND" 2>/dev/null
rm -f "$DMG"
"$DMGENV/bin/dmgbuild" -s "$ROOT/scripts/dmg/settings.py" -D app="$APP_PATH" -D background="$BACKGROUND" "$APP" "$DMG" >/dev/null
codesign --sign "$IDENTITY" --timestamp "$DMG"

if [ "${NOTARIZE:-1}" = "1" ]; then
  echo "▸ Notariserar (brukar ta 1–5 min)"
  if ! xcrun notarytool submit "$DMG" --keychain-profile Mindtalk --wait; then
    echo "✗ Notariseringen misslyckades. Finns profilen? Se toppen av skriptet."; exit 1
  fi
  xcrun stapler staple "$DMG"
  spctl -a -t open --context context:primary-signature -v "$DMG"
fi

echo "✓ Klar: $DMG"
