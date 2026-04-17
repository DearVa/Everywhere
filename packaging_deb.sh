#! /usr/bin/sh
if [ "$#" -lt 2 ]; then
    exit 1
fi 

WORKSPACE=$(pwd)
BINARCH="$1"
VERSION="$2"
BINDIR="$3"
PACKAGINGPATH="/tmp/Everywhere"
INSTALLPATH="/opt/Everywhere"
ARCHSUFFIX="${BINARCH#*-}"

rid_to_deb_arch() {
    case "$1" in
        linux-x64)     echo "amd64" ;;
        linux-arm64)   echo "arm64" ;;
        *)             return 1 ;;
    esac
}

DEBARCH=$(rid_to_deb_arch $BINARCH) || exit 1

rm -rf "$PACKAGINGPATH"
mkdir -p "$PACKAGINGPATH/DEBIAN"
mkdir -p "$PACKAGINGPATH$INSTALLPATH"
mkdir -p "$PACKAGINGPATH/usr/bin"
mkdir -p "$PACKAGINGPATH/usr/share/applications"
mkdir -p "$PACKAGINGPATH/usr/share/icons/hicolor/512x512/apps"

cp -r "$BINDIR"/* "$PACKAGINGPATH$INSTALLPATH/"
chmod +x "$PACKAGINGPATH$INSTALLPATH/Everywhere"
cp "$BINDIR/img/Everywhere-icon.png" "$PACKAGINGPATH/usr/share/icons/hicolor/512x512/apps/Everywhere.png"

cat > "$PACKAGINGPATH/usr/share/applications/Everywhere.desktop" <<EOF
[Desktop Entry]
Name=Everywhere
Comment=A context-aware AI assistant for your desktop.
Exec=/usr/bin/Everywhere
Icon=Everywhere
Type=Application
Terminal=false
Categories=Utility;
Keywords=AI;tool;
EOF

cat > "$PACKAGINGPATH/DEBIAN/control" <<EOF
Package: Everywhere
Version: $VERSION
Architecture: $DEBARCH
Maintainer: DearVa 
Description: Everywhere
Depends: libc6,libx11-6,libglib2.0-0,libatspi2.0-0
Section: utils
Priority: optional
Homepage: https://everywhere.sylinko.com
EOF

cat > "$PACKAGINGPATH/DEBIAN/postinst" <<EOF
#!/bin/sh
set -e
ln -sf "$INSTALLPATH/Everywhere" /usr/bin/Everywhere
exit 0
EOF

cat > "$PACKAGINGPATH/DEBIAN/prerm" <<EOF
#!/bin/sh
set -e
rm -f /usr/bin/Everywhere
exit 0
EOF

chmod 755 "$PACKAGINGPATH/DEBIAN/postinst"
chmod 755 "$PACKAGINGPATH/DEBIAN/prerm"

dpkg-deb --build "$PACKAGINGPATH/" "$WORKSPACE/Everywhere-Linux-$ARCHSUFFIX-v$VERSION.deb"
