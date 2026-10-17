#!/bin/sh
# Applies the migrations baked into this image. Does not install tools and
# does not read the read-only /src mount.
set -eu
if [ -z "${ConnectionStrings__Default:-}" ]; then
  echo "ConnectionStrings__Default is required" >&2
  exit 1
fi
mkdir -p "${DOTNET_BUNDLE_EXTRACT_BASE_DIR:-/tmp/bundle-extract}"
exec /opt/worklane/efbundle --connection "${ConnectionStrings__Default}"
