#!/usr/bin/env bash
# Start the DepthWizard backend on localhost:8000.
# Run from the repository root:  bash backend/start_backend.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

cd "$REPO_ROOT"

exec uvicorn backend.app.main:app \
    --host 0.0.0.0 \
    --port 8000 \
    --reload
