#!/bin/bash
set -e

APP_NAME="Everywhere"
APP_ID="com.Sylinko.Everywhere"
BUILD_DIR="build_appimage"
APPDIR="$BUILD_DIR/Everywhere.AppDir"
RUNTIME_FILE="$BUILD_DIR/runtime-x86_64"
RUNTIME_URL="https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64"

OUTPUT_DIR="./Releases"
OUTPUT_APPIMAGE="Everywhere-x86_64.AppImage"

mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/lib/Everywhere"

# Publish .NET application
echo "Publishing .NET application..."
dotnet restore Everywhere.Linux.slnf
if ! dotnet publish src/Everywhere.Linux/Everywhere.Linux.csproj \
    -c Release -r linux-x64 \
    -o "$APPDIR/usr/lib/Everywhere"; then
    echo -e "\033[0;31mdotnet publish failed. make sure you have the .NET SDK installed.\033[0m"
    echo -e "\033[0;33mYou can download it from https://dotnet.microsoft.com/download\033[0m"
    echo "Cleaning up..."
    rm -rf "$BUILD_DIR"
    exit 1
fi

# prepare AppDir
ln -sr "$APPDIR/usr/lib/Everywhere/$APP_NAME" "$APPDIR/usr/bin/$APP_NAME"
cp img/Everywhere-icon.png "$APPDIR/$APP_ID.png"

# Desktop file
cat > "$APPDIR/$APP_ID.desktop" <<EOF
[Desktop Entry]
Name=Everywhere
Exec=$APP_NAME
Icon=$APP_ID
Type=Application
Categories=Utility;
EOF

# Create AppRun
cat > "$APPDIR/AppRun" <<EOF
#!/bin/sh
HERE=\$(dirname "\$(readlink -f "\$0")")
export PATH="\$HERE/usr/bin:\$PATH"
exec "\$HERE/usr/bin/$APP_NAME" "\$@"
EOF
chmod +x "$APPDIR/AppRun"

cd "$APPDIR"

# Fix broken symlinks
find . -type l -exec symlinks -cr {} + || true 
cd ../..

mksquashfs "$APPDIR" root.squashfs -root-owned -noappend -comp zstd

# Create the AppImage
mkdir -p "$OUTPUT_DIR"
echo "Creating AppImage..."
wget -c "$RUNTIME_URL" -O "$RUNTIME_FILE"
cat "$RUNTIME_FILE" root.squashfs > "$OUTPUT_DIR/$OUTPUT_APPIMAGE"
chmod +x "$OUTPUT_DIR/$OUTPUT_APPIMAGE"

# clean up
echo "Cleaning up..."
rm root.squashfs
rm -rf "$BUILD_DIR"
echo -e "\033[0;32mAppImage created at $OUTPUT_DIR/$OUTPUT_APPIMAGE\033[0m"