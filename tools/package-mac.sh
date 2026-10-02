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
# In CI, notarization uses an App Store Connect API key instead: STRAYTA_NOTARY_KEY (path to the .p8 file),
# STRAYTA_NOTARY_KEY_ID and STRAYTA_NOTARY_ISSUER.
# STRAYTA_VERSION overrides the version (CI passes the tag); STRAYTA_BUNDLE_ID the bundle identifier
# (default ai.wundur.strayta). No secrets live in this script.
set -euo pipefail

# On GitHub Actions, failures become annotations (job logs need a signed-in viewer): the stage that failed and the
# last lines the failing command printed.
stage="Starting"
step() { stage=$1; print "• $1"; }
log="$(mktemp -t strayta-package)"
# Anything that fails outside run() still names its stage and line on CI.
[[ -n ${GITHUB_ACTIONS:-} ]] && trap 'print "::error title=Packaging failed: ${stage}::line $LINENO exited with status $?"' ZERR
run() {
  local code=0
  "$@" >"$log" 2>&1 || code=$?
  cat "$log"
  if (( code )); then
    if [[ -n ${GITHUB_ACTIONS:-} ]]; then
      print "::error title=Packaging failed: ${stage}::$(tail -n 25 "$log" | sed -e 's/%/%25/g' | awk 'BEGIN{ORS="%0A"} {print}')"
    fi
    exit $code
  fi
}

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

version=${STRAYTA_VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -1)}
build=$(git -C "$root" rev-list --count HEAD 2>/dev/null || echo 1)
bundle_id=${STRAYTA_BUNDLE_ID:-ai.wundur.strayta}
publish="$out/publish-$arch"
app="$out/Strayta.app"
dmg="$out/Strayta-$version-$arch.dmg"

step "Publishing Strayta $version (build $build) for osx-$arch"
rm -rf "$publish" "$app"
run dotnet publish "$root/apps/Strayta.Editor/Strayta.Editor.csproj" -c Release -r "osx-$arch" --self-contained true \
  -p:UseAppHost=true -p:DebugType=none -p:Version="$version" -o "$publish" -v quiet -nologo
if (( ! models )); then rm -rf "$publish/models"; fi

step "Assembling $app"
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
  step "Signing with: $identity"
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
  export STRAYTA_APP=$app
  run /bin/zsh -c 'find "$STRAYTA_APP/Contents/MacOS" -type f ! -path "$STRAYTA_APP/Contents/MacOS/Strayta" -print0 |
    xargs -0 -P 16 -n 1 /bin/zsh -c '"'"'codesign --verify "$1" 2>/dev/null || { print "not signed: $1"; codesign --verify "$1" 2>&1 | tail -1; exit 255; }'"'"' _' 
  run codesign --force --timestamp --options runtime --entitlements "$entitlements" --sign "$identity" "$app/Contents/MacOS/Strayta"
  run codesign --force --timestamp --options runtime --entitlements "$entitlements" --sign "$identity" "$app"
  run codesign --verify --strict --deep "$app"
else
  print "• No Developer ID certificate found: signing ad hoc (runs here; elsewhere right-click › Open)"
  codesign --force --deep --sign - "$app"
fi

step "Building $dmg"
# The disk image's window layout (background, icon positions, size) comes from dmgbuild, a small MIT-licensed
# Python tool that writes Finder's layout file without Finder, so it works on CI. It is installed into a private
# virtual environment next to the output; without Python or network it falls back to a plain disk image.
rm -f "$dmg"
dmgbuild=""
venv="$out/.dmgbuild-venv"
if [[ -x "$venv/bin/dmgbuild" ]] || { python3 -m venv "$venv" >/dev/null 2>&1 && "$venv/bin/pip" install -q 'dmgbuild>=1.6,<2' >/dev/null 2>&1; }; then
  dmgbuild="$venv/bin/dmgbuild"
fi
# hdiutil (used by both paths) sometimes reports "Resource busy" on CI machines; a retry clears it.
for attempt in 1 2 3; do
  if [[ -n $dmgbuild ]]; then
    "$dmgbuild" -s "$root/packaging/macos/dmg-settings.py" -D app="$app" -D background="$root/packaging/macos/dmg-background.tiff" \
      "Strayta $version" "$dmg" >"$log" 2>&1 && break
  else
    print "  (dmgbuild unavailable: plain disk image)"
    stage_dir="$out/dmg-stage"
    rm -rf "$stage_dir" && mkdir -p "$stage_dir" && cp -R "$app" "$stage_dir/" && ln -s /Applications "$stage_dir/Applications"
    hdiutil create -volname "Strayta $version" -srcfolder "$stage_dir" -ov -format UDZO -quiet "$dmg" >"$log" 2>&1 && { rm -rf "$stage_dir"; break; }
  fi
  (( attempt == 3 )) && { stage="Building the disk image"; run false; }
  sleep 5
done
[[ -n $identity ]] && run codesign --force --timestamp --sign "$identity" "$dmg"

profile=${STRAYTA_NOTARY_PROFILE:-strayta-notary}
notary=()
if [[ -n ${STRAYTA_NOTARY_KEY:-} ]]; then
  notary=(--key "$STRAYTA_NOTARY_KEY" --key-id "$STRAYTA_NOTARY_KEY_ID" --issuer "$STRAYTA_NOTARY_ISSUER")
elif xcrun notarytool history --keychain-profile "$profile" >/dev/null 2>&1; then
  notary=(--keychain-profile "$profile")
fi
if [[ -n $identity ]] && (( ${#notary} )); then
  step "Notarizing (this waits for Apple)"
  if ! xcrun notarytool submit "$dmg" "${notary[@]}" --wait --output-format plist > "$out/notary.plist"; then
    run false
  fi
  notary_status=$(/usr/libexec/PlistBuddy -c 'Print :status' "$out/notary.plist" 2>/dev/null || echo unknown)
  if [[ $notary_status != Accepted ]]; then
    # Apple's log names each rejected file and why.
    notary_id=$(/usr/libexec/PlistBuddy -c 'Print :id' "$out/notary.plist" 2>/dev/null || echo "")
    [[ -n $notary_id ]] && xcrun notarytool log "$notary_id" "${notary[@]}" > "$log" 2>&1 || true
    stage="Notarization ($notary_status)"
    run false
  fi
  step "Stapling"
  run xcrun stapler staple "$dmg"
  run xcrun stapler staple "$app"
  spctl --assess --type open --context context:primary-signature -v "$dmg" || true
elif [[ -n $identity ]]; then
  print "• Signed but not notarized: no notarytool profile '$profile' or API key (see the comment at the top of this script)"
fi

print "\nDone:\n  $app\n  $dmg ($(du -h "$dmg" | cut -f1))"
