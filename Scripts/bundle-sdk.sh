#!/bin/bash
# Bundle RGB SDK for plugin deployment
# Run this before building the plugin for production

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SDK_DIR="$SCRIPT_DIR/rgb-sdk"

echo "Bundling RGB SDK into Scripts/rgb-sdk..."

# Check if rgb-sdk source exists
if [ -d "$SCRIPT_DIR/../../rgb-sdk" ]; then
    SDK_SOURCE="$SCRIPT_DIR/../../rgb-sdk"
elif [ -d "$SCRIPT_DIR/../../../rgb-sdk" ]; then
    SDK_SOURCE="$SCRIPT_DIR/../../../rgb-sdk"
else
    echo "Error: rgb-sdk source not found. Please ensure rgb-sdk is in the project root."
    exit 1
fi

# Build SDK if needed
if [ ! -f "$SDK_SOURCE/dist/index.cjs" ]; then
    echo "Building RGB SDK..."
    cd "$SDK_SOURCE"
    npm install
    npm run build
    cd "$SCRIPT_DIR"
fi

# Create output directory
mkdir -p "$SDK_DIR"

# Copy bundled SDK
cp "$SDK_SOURCE/dist/index.cjs" "$SDK_DIR/"
cp "$SDK_SOURCE/dist/index.cjs.map" "$SDK_DIR/" 2>/dev/null || true

echo "RGB SDK bundled successfully!"
echo "  Source: $SDK_SOURCE/dist/"
echo "  Target: $SDK_DIR/"

