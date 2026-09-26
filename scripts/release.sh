#!/bin/bash
# Mindtalk — bygg, signera med Developer ID, notarisera och packa en DMG i dist/.
#
# Notariseringen använder ett app-specifikt lösenord:
#   security add-generic-password -a "admin@mindact.ai" -s "Mindtalk-Notarization" -w "LÖSENORD" -U
# Kör med NOTARIZE=0 för att hoppa över notariseringen (DMG:n fungerar då bara på din egen Mac).
set -euo pipefail

APP=Mindtalk
IDENTITY="Developer ID Application: Mindact Solutions AB (679J7H9973)"
TEAM_ID=679J7H9973
APPLE_ID=admin@mindact.ai
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
  OTHER_CODE_SIGN_FLAGS="--timestamp" clean build | tail -3
APP_PATH="$BUILD/Build/Products/Release/$APP.app"
codesign --verify --deep --strict "$APP_PATH"

echo "▸ Packar DMG"
mkdir -p "$DIST"
STAGE=$(mktemp -d)
cp -R "$APP_PATH" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
hdiutil create -volname "$APP" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"
codesign --sign "$IDENTITY" --timestamp "$DMG"

if [ "${NOTARIZE:-1}" = "1" ]; then
  PASSWORD=$(security find-generic-password -a "$APPLE_ID" -s "Mindtalk-Notarization" -w 2>/dev/null || true)
  if [ -z "$PASSWORD" ]; then
    echo "✗ Hittar inget notariseringslösenord i Nyckelringen (se toppen av skriptet)."; exit 1
  fi
  echo "▸ Notariserar (brukar ta 1–5 min)"
  xcrun notarytool submit "$DMG" --apple-id "$APPLE_ID" --team-id $TEAM_ID --password "$PASSWORD" --wait
  xcrun stapler staple "$DMG"
  spctl -a -t open --context context:primary-signature -v "$DMG"
fi

echo "✓ Klar: $DMG"
