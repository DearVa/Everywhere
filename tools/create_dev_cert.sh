#!/bin/bash
set -e

CERT_NAME="EverywhereDev"
KEYCHAIN="login.keychain"

echo "Checking if certificate '$CERT_NAME' exists..."
if security find-identity -v -p codesigning | grep -q "$CERT_NAME"; then
    echo "Certificate '$CERT_NAME' already exists, skipping creation."
    exit 0
fi

echo "Generating self-signed certificate..."
# 1. Generate private key and certificate request
openssl req -new -newkey rsa:2048 -days 3650 -nodes -x509 \
    -subj "/CN=$CERT_NAME/O=Everywhere Debug/C=CN" \
    -keyout "$CERT_NAME.key" -out "$CERT_NAME.crt"

# 2. Convert to p12 format (password set to 123456, used for import)
openssl pkcs12 -export -out "$CERT_NAME.p12" \
    -inkey "$CERT_NAME.key" -in "$CERT_NAME.crt" \
    -passout pass:123456

echo "Importing certificate to keychain..."
# 3. Import to current user's keychain
# -T /usr/bin/codesign allows the codesign tool to access this certificate, avoiding frequent prompts during build
security import "$CERT_NAME.p12" -k "$KEYCHAIN" -P 123456 -T /usr/bin/codesign

echo "Cleaning up temporary files..."
rm "$CERT_NAME.key" "$CERT_NAME.crt" "$CERT_NAME.p12"

echo "Done! Certificate '$CERT_NAME' is installed."
echo "Please check if the certificate is successfully used in the next project build."
