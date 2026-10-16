#!/usr/bin/env sh
# Builds the runner image with the Docker Engine CLI (no Docker Desktop).
# DESIGNC_PYTHON is designc's python/ folder (the package registry checks import); default: the sibling checkout.
set -eu
cd "$(dirname "$0")"
docker build -t factory-s2-runner:latest --build-context pobserve-parser=../../tools/pobserve-parser \
  --build-context designc-python="${DESIGNC_PYTHON:-../../../../Designc/app/python}" "$@" .
