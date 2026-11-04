#!/bin/sh
# Applies the migrations baked into this image. Does not install tools.
# Worklane.Migrator calls WorklaneDbContextFactory, which reads
# ConnectionStrings__Default. Do not pass that value on the command line:
# it would put the migrator password in the process argv.
set -eu
if [ -z "${ConnectionStrings__Default:-}" ]; then
  echo "ConnectionStrings__Default is required" >&2
  exit 1
fi
exec dotnet /opt/worklane/Worklane.Migrator.dll
