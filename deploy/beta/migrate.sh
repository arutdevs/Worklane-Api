#!/bin/sh
# Applies the migrations baked into this image. Does not install tools.
# The bundle calls WorklaneDbContextFactory, which reads ConnectionStrings__Default.
# Do not pass --connection: that puts the migrator password in the process argv.
set -eu
if [ -z "${ConnectionStrings__Default:-}" ]; then
  echo "ConnectionStrings__Default is required" >&2
  exit 1
fi
mkdir -p "${DOTNET_BUNDLE_EXTRACT_BASE_DIR:-/tmp/bundle-extract}"
exec /opt/worklane/efbundle
