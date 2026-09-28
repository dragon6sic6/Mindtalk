#!/bin/bash
# Mindtalk — bygg, signera med Developer ID, notarisera och packa en DMG i dist/.
#
# Notariseringen använder en notarytool-profil i Nyckelringen. Skapa den en gång
# (du skriver själv in det app-specifika lösenordet från appleid.apple.com):
#   xcrun notarytool store-credentials Mindtalk --apple-id admin@mindact.ai --team-id 679J7H9973
# Kör med NOTARIZE=0 för att hoppa över notariseringen (DMG:n fungerar då bara på din egen Mac).
# LOCAL=1 bygger och signerar med Developer ID och installerar i /Applications – ingen DMG,
# ingen notarisering. Samma signatur som de publicerade versionerna, så macOS behåller
# behörigheterna (Hjälpmedel, mikrofon) mellan dina egna byggen och riktiga uppdateringar.
set -euo pipefail

APP=Mindtalk
IDENTITY="Developer ID Application: Mindact Solutions AB (679J7H9973)"
TEAM_ID=679J7H9973
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD="$ROOT/build/release"
DIST="$ROOT/dist"
VERSION=$(grep -m1 'MARKETING_VERSION:' "$ROOT/project.yml" | sed 's/.*: *//')
BUILD_NUMBER=$(grep -m1 'CURRENT_PROJECT_VERSION:' "$ROOT/project.yml" | sed 's/.*: *//')
DMG="$DIST/$APP-$VERSION.dmg"

cd "$ROOT"

# ── Safety checks before anything is built ──────────────────────────────────
# What ships is exactly what's committed.
if [ "${LOCAL:-0}" != "1" ] && [ -n "$(git status --porcelain)" ] && [ "${ALLOW_DIRTY:-0}" != "1" ]; then
  echo "✗ Osparade ändringar i git. Spara (commit) först, eller kör med ALLOW_DIRTY=1 för ett test."; exit 1
fi
# Sparkle only offers an update with a higher build number than the published one.
if [ "${LOCAL:-0}" != "1" ] && LAST=$(gh release download --repo dragon6sic6/Mindtalk --pattern appcast.xml --output - 2>/dev/null); then
  PUBLISHED=$(printf '%s' "$LAST" | sed -n 's:.*<sparkle\:version>\([0-9]*\)</sparkle\:version>.*:\1:p' | sort -n | tail -1)
  if [ -n "$PUBLISHED" ] && [ "$BUILD_NUMBER" -le "$PUBLISHED" ]; then
    echo "✗ Byggnummer $BUILD_NUMBER är inte högre än publicerade $PUBLISHED – höj CURRENT_PROJECT_VERSION i project.yml."; exit 1
  fi
fi
# An old DMG lying around is easy to upload by mistake.
rm -f "$DIST/$APP.dmg"
xcodegen generate --quiet

# The licence texts that ship in the app, fresh from the resolved FluidAudio.
xcodebuild -project $APP.xcodeproj -scheme $APP -derivedDataPath "$BUILD" -resolvePackageDependencies >/dev/null
python3 "$ROOT/scripts/acknowledgements.py"

echo "▸ Bygger $APP $VERSION (Release, Apple Silicon)"
xcodebuild -project $APP.xcodeproj -scheme $APP -configuration Release -derivedDataPath "$BUILD" \
  CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY="$IDENTITY" DEVELOPMENT_TEAM=$TEAM_ID \
  OTHER_CODE_SIGN_FLAGS="--timestamp" CODE_SIGN_INJECT_BASE_ENTITLEMENTS=NO \
  DEPLOYMENT_POSTPROCESSING=YES STRIP_INSTALLED_PRODUCT=YES clean build | tail -3
APP_PATH="$BUILD/Build/Products/Release/$APP.app"

# Sparkle's helpers arrive ad-hoc signed; notarization wants our Developer ID on
# every executable. Inside out, as Sparkle's documentation describes, then the app.
SPARKLE="$APP_PATH/Contents/Frameworks/Sparkle.framework/Versions/B"
[ -d "$SPARKLE" ] || { echo "✗ Hittar inte Sparkle i appen – har ramverkets struktur ändrats?"; exit 1; }
if [ -d "$SPARKLE" ]; then
  sign() { codesign -f -s "$IDENTITY" -o runtime --timestamp "$@"; }
  sign "$SPARKLE/XPCServices/Installer.xpc"
  sign --preserve-metadata=entitlements "$SPARKLE/XPCServices/Downloader.xpc"
  sign "$SPARKLE/Autoupdate"
  sign "$SPARKLE/Updater.app"
  sign "$APP_PATH/Contents/Frameworks/Sparkle.framework"
  sign --entitlements "$ROOT/Mindtalk/Mindtalk.entitlements" "$APP_PATH"
fi
codesign --verify --deep --strict "$APP_PATH"
# Notarization refuses debug entitlements; catch them here rather than after the upload.
while IFS= read -r -d '' part; do
  if codesign -d --entitlements - "$part" 2>/dev/null | grep -q get-task-allow; then
    echo "✗ $part har get-task-allow – notariseringen skulle nekas."; exit 1
  fi
done < <(find "$APP_PATH" \( -name "*.app" -o -name "*.xpc" -o -name "*.framework" \) -print0)
if [ "${LOCAL:-0}" = "1" ]; then
  pkill -x "$APP" 2>/dev/null || true
  rm -rf "/Applications/$APP.app"
  cp -R "$APP_PATH" "/Applications/$APP.app"
  open -a "/Applications/$APP.app"
  echo "✓ Installerad (Developer ID, inte notariserad): /Applications/$APP.app"
  exit 0
fi
# Symbols for reading crash reports, kept next to the release.
mkdir -p "$DIST"
rm -rf "$DIST/$APP-$VERSION.dSYM"
[ -d "$BUILD/Build/Products/Release/$APP.app.dSYM" ] && cp -R "$BUILD/Build/Products/Release/$APP.app.dSYM" "$DIST/$APP-$VERSION.dSYM"

echo "▸ Packar DMG"
mkdir -p "$DIST"
# dmgbuild lays out the window (background, icon positions) without scripting Finder.
DMGENV="$ROOT/build/dmgenv"
if [ ! -x "$DMGENV/bin/dmgbuild" ]; then
  python3 -m venv "$DMGENV" && "$DMGENV/bin/pip" install -q "dmgbuild==1.6.7"
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

# Sparkle: the appcast next to the DMG, signed with Mindtalk's own EdDSA key
# (keychain account "mindtalk"). Both are uploaded to the GitHub release; the app
# reads releases/latest/download/appcast.xml and downloads the DMG from this tag.
echo "▸ Appcast för automatiska uppdateringar"
SPARKLE_BIN=$(find "$BUILD/SourcePackages/artifacts" -path "*Sparkle/bin" -type d | head -1)
FEED="$DIST/appcast-$VERSION"
rm -rf "$FEED" && mkdir -p "$FEED"
cp "$DMG" "$FEED/$APP.dmg"
[ -f "$DIST/notes-$VERSION.md" ] && cp "$DIST/notes-$VERSION.md" "$FEED/$APP.md"
"$SPARKLE_BIN/generate_appcast" --account mindtalk --embed-release-notes \
  --download-url-prefix "https://github.com/dragon6sic6/Mindtalk/releases/download/v$VERSION/" "$FEED"
# The feed itself must be signed (the app requires it). generate_appcast does it;
# make sure, and verify.
grep -q "sparkle-signatures" "$FEED/appcast.xml" || "$SPARKLE_BIN/sign_update" --account mindtalk "$FEED/appcast.xml"
"$SPARKLE_BIN/sign_update" --account mindtalk --verify "$FEED/appcast.xml" >/dev/null \
  || { echo "✗ Appcasten är inte korrekt signerad."; exit 1; }
echo "  $FEED/appcast.xml (signerad)"

echo "✓ Klar: $DMG"
echo
echo "Publicera (DMG:n och appcasten måste ha exakt dessa namn):"
echo "  gh release create v$VERSION \"$FEED/$APP.dmg\" \"$FEED/appcast.xml\" --latest --title \"Mindtalk $VERSION\" --notes-file dist/notes-$VERSION.md"
