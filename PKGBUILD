# Maintainer: Pasical <pasicalyang@gmail.com>
pkgname=everywhere-bin
pkgver=0.0.0
pkgrel=1
pkgdesc="Context-aware AI assistant for your desktop. Ready to respond intelligently, seamlessly integrating multiple LLMs and MCP tools."
arch=('x86_64')
url="https://github.com/pasical/Everywhere"
license=('BSL-1.1')
depends=('dotnet-runtime' 'fontconfig' 'libx11' 'libice' 'libsm' 'hicolor-icon-theme')
provides=('everywhere')
conflicts=('everywhere')

_filename="Everywhere-Linux-x64-v${pkgver}.tar.zst"

source=("${url}/releases/download/v${pkgver}/${_filename}"
        "https://raw.githubusercontent.com/pasical/Everywhere/main/LICENSE")

sha256sums=('SKIP'
           'SKIP')

package() {
    cd "${srcdir}"
    mkdir -p "${pkgdir}/opt/Everywhere"
    bsdtar -xf "${_filename}" -C "${pkgdir}/opt/Everywhere"
    find "${pkgdir}" -type d -exec chmod 755 {} +
    mkdir -p "${pkgdir}/usr/bin"
    ln -s "/opt/Everywhere/Everywhere" "${pkgdir}/usr/bin/Everywhere"
    
    if [ -f "${pkgdir}/opt/Everywhere/Everywhere.desktop" ]; then
        mkdir -p "${pkgdir}/usr/share/applications"
        cp "${pkgdir}/opt/Everywhere/Everywhere.desktop" "${pkgdir}/usr/share/applications/"
    fi  
    
    mkdir -p "${pkgdir}/usr/share/licenses/everywhere"
    if [ -f "${srcdir}/LICENSE" ]; then
        cp "${srcdir}/LICENSE" "${pkgdir}/usr/share/licenses/everywhere/"
    fi
}