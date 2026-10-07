#!/usr/bin/env sh
# Builds the runner image with the Docker Engine CLI (no Docker Desktop).
set -eu
cd "$(dirname "$0")"
docker build -t factory-s2-runner:latest "$@" .
