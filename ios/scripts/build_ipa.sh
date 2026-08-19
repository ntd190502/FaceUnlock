#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
command -v xcodegen >/dev/null || { echo "Install xcodegen first"; exit 1; }
xcodegen generate
rm -rf build archive.xcarchive
xcodebuild -project FaceUnlock.xcodeproj -scheme FaceUnlock -configuration Release -destination 'generic/platform=iOS' -archivePath archive.xcarchive archive
cat > ExportOptions.plist <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>method</key><string>development</string>
<key>signingStyle</key><string>automatic</string>
</dict></plist>
EOF
xcodebuild -exportArchive -archivePath archive.xcarchive -exportPath build -exportOptionsPlist ExportOptions.plist
find build -name '*.ipa' -maxdepth 2 -print
