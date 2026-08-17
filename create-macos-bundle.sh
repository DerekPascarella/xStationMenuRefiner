#!/bin/bash
# Creates a macOS .app bundle from dotnet publish output.
# Usage: ./create-macos-bundle.sh <publish_output_dir> <version> <output_dir> [arch]
# arch defaults to "x64".
#
# Written by Derek Pascarella (ateam)

set -e

# Non-interactive shells do not source .bashrc, so put ~/.local/bin on PATH here.
export PATH="$HOME/.local/bin:$PATH"

PUBLISH_DIR=$1
VERSION=$2
OUTPUT_DIR=$3
ARCH=${4:-x64}

if [ -z "$PUBLISH_DIR" ] || [ -z "$VERSION" ] || [ -z "$OUTPUT_DIR" ]; then
    echo "Usage: $0 <publish_output_dir> <version> <output_dir> [arch]"
    exit 1
fi

APP_NAME="xStationMenuRefiner"
BUNDLE_NAME="${APP_NAME}.app"
BUNDLE_PATH="${OUTPUT_DIR}/${BUNDLE_NAME}"
SOURCE_DIR="src/xStationMenuRefiner.App"

echo "Creating macOS app bundle: ${BUNDLE_NAME}"
echo "Version: ${VERSION}"
echo "Architecture: ${ARCH}"

mkdir -p "${BUNDLE_PATH}/Contents/MacOS"
mkdir -p "${BUNDLE_PATH}/Contents/Resources"

echo "Copying application files..."
cp -r "${PUBLISH_DIR}"/* "${BUNDLE_PATH}/Contents/MacOS/"

echo "Creating Info.plist..."
if [ -f "${SOURCE_DIR}/Info.plist" ]; then
    cp "${SOURCE_DIR}/Info.plist" "${BUNDLE_PATH}/Contents/Info.plist"
    if [ "$(uname)" == "Darwin" ]; then
        sed -i '' "s/<string>1.0<\/string>/<string>${VERSION}<\/string>/g" "${BUNDLE_PATH}/Contents/Info.plist"
    else
        sed -i "s/<string>1.0<\/string>/<string>${VERSION}<\/string>/g" "${BUNDLE_PATH}/Contents/Info.plist"
    fi
else
    echo "Warning: Info.plist template not found at ${SOURCE_DIR}/Info.plist"
fi

echo "Setting executable permissions..."
chmod +x "${BUNDLE_PATH}/Contents/MacOS/${APP_NAME}"
find "${BUNDLE_PATH}/Contents/MacOS" -name "*.dylib" -exec chmod +x {} \;

if [ -f "${SOURCE_DIR}/Assets/icon.icns" ]; then
    cp "${SOURCE_DIR}/Assets/icon.icns" "${BUNDLE_PATH}/Contents/Resources/"
    echo "Icon file copied."
else
    echo "Warning: No .icns icon file found. The bundle will use the default icon."
fi

echo "Ad-hoc code signing the bundle..."
if command -v rcodesign &> /dev/null; then
    rcodesign sign "${BUNDLE_PATH}" 2>&1 | grep -v "non Mach-O file\|we do not know how\|if the bundle signs" || true
elif command -v codesign &> /dev/null; then
    codesign --force --deep -s - "${BUNDLE_PATH}"
else
    echo "ERROR: No code signing tool found (rcodesign or codesign)."
    echo "Apple Silicon Macs require signed binaries. Install rcodesign:"
    echo "  https://github.com/indygreg/apple-platform-rs"
    exit 1
fi

echo "macOS app bundle created at: ${BUNDLE_PATH}"

echo "Creating tar.gz archive..."
cd "${OUTPUT_DIR}"
tar -czf "${APP_NAME}.v${VERSION}-osx-${ARCH}-AppBundle.tar.gz" "${BUNDLE_NAME}"
cd - > /dev/null

# The archive is the deliverable, so the loose .app goes away.
rm -rf "${BUNDLE_PATH}"

echo "Archive created: ${OUTPUT_DIR}/${APP_NAME}.v${VERSION}-osx-${ARCH}-AppBundle.tar.gz"
echo "Done!"
