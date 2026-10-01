#!/bin/zsh
# Builds Strayta.app and a .dmg for macOS: publishes the editor self-contained, assembles the app bundle, signs it,
# and (with a Developer ID certificate) notarizes and staples it.
#
#   tools/package-mac.sh [--arch arm64|x64] [--no-models] [--out dir]
#
# Signing is automatic:
#   - With a "Developer ID Application" certificate in the keychain (or STRAYTA_SIGN_IDENTITY naming one), the app is
#     signed with the hardened runtime and packaging/macos/Strayta.entitlements, ready for notarization.
#   - Without one, it is signed ad hoc: it runs on this Mac, and elsewhere after right-click › Open (Gatekeeper).
# Notarization runs when a notarytool keychain profile exists (STRAYTA_NOTARY_PROFILE, default "strayta-notary"):
#   xcrun notarytool store-credentials strayta-notary --apple-id <id> --team-id <TEAMID> --password <app-specific>
# STRAYTA_BUNDLE_ID overrides the bundle identifier (default ai.wundur.strayta). No secrets live in this script.
set -euo pipefail

arch=arm64
models=1
root=${0:A:h:h}
out="$HOME/Strayta-builds"   # on the internal (APFS) disk: codesign rejects the ._ files exFAT drives create
while (( $# )); do
  case $1 in
    --arch) arch=$2; shift 2 ;;
    --no-models) models=0; shift ;;
    --out) out=$2; shift 2 ;;
    *) print -u2 "unknown option: $1"; exit 2 ;;
  esac
done
[[ $arch == arm64 || $arch == x64 ]] || { print -u2 "--arch must be arm64 or x64"; exit 2; }

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -1)
build=$(git -C "$root" rev-list --count HEAD 2>/dev/null || echo 1)
bundle_id=${STRAYTA_BUNDLE_ID:-ai.wundur.strayta}
publish="$out/publish-$arch"
app="$out/Strayta.app"
dmg="$out/Strayta-$version-$arch.dmg"

print "• Publishing Strayta $version (build $build) for osx-$arch"
rm -rf "$publish" "$app"
dotnet publish "$root/apps/Strayta.Editor/Strayta.Editor.csproj" -c Release -r "osx-$arch" --self-contained true \
  -p:UseAppHost=true -p:DebugType=none -o "$publish" -v quiet -nologo
if (( ! models )); then rm -rf "$publish/models"; fi

print "• Assembling $app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish/" "$app/Contents/MacOS/"
cp "$root/packaging/macos/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"
sed -e "s/__BUNDLE_ID__/$bundle_id/" -e "s/__VERSION__/$version/" -e "s/__BUILD__/$build/" \
  "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist" >/dev/null

identity=${STRAYTA_SIGN_IDENTITY:-}
if [[ -z $identity ]]; then
  identity=$(security find-identity -v -p codesigning 2>/dev/null | sed -n 's/.*"\(Developer ID Application:.*\)"/\1/p' | head -1)
fi
entitlements="$root/packaging/macos/Strayta.entitlements"

if [[ -n $identity ]]; then
  print "• Signing with: $identity"
  # Inside out: every file in Contents/MacOS (codesign treats all of them as code: native libraries, .NET
  # assemblies, models; non-Mach-O files get their signature in extended attributes), then the executable, then the
  # app. --deep is deprecated for signing.
  # Files are independent, so they are signed 16 at a time (each signature asks Apple's timestamp server; many
  # more at once risks it throttling).
  export STRAYTA_SIGN_ID=$identity STRAYTA_SIGN_ENT=$entitlements
  find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/Strayta" -print0 |
    xargs -0 -P 16 -n 1 /bin/zsh -c '
      if file -b "$1" | grep -q Mach-O; then
        codesign --force --timestamp --options runtime --entitlements "$STRAYTA_SIGN_ENT" --sign "$STRAYTA_SIGN_ID" "$1" 2>&1 | grep -v "replacing existing signature"
      else
        codesign --force --timestamp --sign "$STRAYTA_SIGN_ID" "$1" 2>&1 | grep -v "replacing existing signature"
      fi
      exit 0' _
  # Every file must have come out signed (a failure inside xargs would otherwise go unnoticed).
  find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/Strayta" -print0 |
    xargs -0 -P 16 -n 1 /bin/zsh -c 'codesign --verify "$1" 2>/dev/null || { print -u2 "not signed: $1"; exit 255; }' _
  codesign --force --timestamp --options runtime --entitlements "$entitlements" --sign "$identity" "$app/Contents/MacOS/Strayta"
  codesign --force --timestamp --options runtime --entitlements "$entitlements" --sign "$identity" "$app"
  codesign --verify --strict --deep "$app"
else
  print "• No Developer ID certificate found: signing ad hoc (runs here; elsewhere right-click › Open)"
  codesign --force --deep --sign - "$app"
fi

print "• Building $dmg"
stage="$out/dmg-stage"
rm -rf "$stage" "$dmg"
mkdir -p "$stage"
cp -R "$app" "$stage/"
ln -s /Applications "$stage/Applications"
hdiutil create -volname "Strayta $version" -srcfolder "$stage" -ov -format UDZO -quiet "$dmg"
rm -rf "$stage"
[[ -n $identity ]] && codesign --force --timestamp --sign "$identity" "$dmg"

profile=${STRAYTA_NOTARY_PROFILE:-strayta-notary}
if [[ -n $identity ]] && xcrun notarytool history --keychain-profile "$profile" >/dev/null 2>&1; then
  print "• Notarizing with keychain profile '$profile' (this waits for Apple)"
  xcrun notarytool submit "$dmg" --keychain-profile "$profile" --wait
  xcrun stapler staple "$dmg"
  xcrun stapler staple "$app"
  spctl --assess --type open --context context:primary-signature -v "$dmg" || true
elif [[ -n $identity ]]; then
  print "• Signed but not notarized: no notarytool profile '$profile' (see the comment at the top of this script)"
fi

print "\nDone:\n  $app\n  $dmg ($(du -h "$dmg" | cut -f1))"
