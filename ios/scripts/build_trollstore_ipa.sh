#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

echo "======================================"
echo " FaceUnlock TrollStore IPA Builder"
echo "======================================"

command -v xcodegen >/dev/null 2>&1 || {
    echo "ERROR: xcodegen not found"
    exit 1
}

rm -rf \
    build \
    dist \
    FaceUnlock.xcodeproj

echo "[1/5] Generate Xcode project"

xcodegen generate

test -d FaceUnlock.xcodeproj || {
    echo "ERROR: FaceUnlock.xcodeproj was not generated"
    exit 2
}

echo "[2/5] Build unsigned iOS app"

xcodebuild \
    -project FaceUnlock.xcodeproj \
    -scheme FaceUnlock \
    -configuration Release \
    -sdk iphoneos \
    -destination 'generic/platform=iOS' \
    -derivedDataPath build \
    CODE_SIGNING_ALLOWED=NO \
    CODE_SIGNING_REQUIRED=NO \
    CODE_SIGN_IDENTITY="" \
    PRODUCT_BUNDLE_IDENTIFIER="${BUNDLE_ID:-io.faceunlock.app}" \
    build

echo "[3/5] Find FaceUnlock.app"

APP_PATH="$(find build/Build/Products \
    -type d \
    -name 'FaceUnlock.app' \
    -print \
    -quit)"

if [ -z "${APP_PATH:-}" ] || [ ! -d "$APP_PATH" ]; then
    echo "ERROR: FaceUnlock.app not found"

    find build/Build/Products \
        -maxdepth 5 \
        -type d \
        -name '*.app' \
        -print || true

    exit 3
fi

echo "APP: $APP_PATH"

echo "[4/5] Create TrollStore IPA"

rm -rf dist

mkdir -p dist/Payload

cp -R "$APP_PATH" \
    dist/Payload/FaceUnlock.app

cd dist

/usr/bin/zip \
    -qry \
    FaceUnlock.ipa \
    Payload

test -f FaceUnlock.ipa || {
    echo "ERROR: IPA creation failed"
    exit 4
}

echo "[5/5] Verify IPA"

/usr/bin/unzip -t FaceUnlock.ipa

test -f Payload/FaceUnlock.app/Info.plist

echo
echo "======================================"
echo " TROLLSTORE IPA BUILD SUCCESS"
echo "======================================"

ls -lh FaceUnlock.ipa

/usr/bin/shasum -a 256 FaceUnlock.ipa
