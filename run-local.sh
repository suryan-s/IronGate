#!/bin/bash
# Load .env file and run IronGate locally
# Usage: ./run-local.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="${SCRIPT_DIR}/.env"

if [[ ! -f "$ENV_FILE" ]]; then
    echo "ERROR: .env file not found at $ENV_FILE"
    exit 1
fi

echo "Loading environment from $ENV_FILE"
set -a
source "$ENV_FILE"
set +a

echo "Starting IronGate on http://localhost:8080"
echo "Keycloak: $KEYCLOAK_URL"
echo "Redis: $REDIS_CONNECTION_STRING"
echo "OTEL: $OTEL_ENDPOINT"
echo ""

cd "${SCRIPT_DIR}/src/IronGate"
exec dotnet run --no-build