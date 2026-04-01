#!/usr/bin/env bash
set -e

PORT=${PORT:-5100}
ENV=${ASPNETCORE_ENVIRONMENT:-Development}

echo "Starting llens on http://localhost:$PORT"
echo "Browse: http://localhost:$PORT/browse"
echo ""

ASPNETCORE_URLS="http://localhost:$PORT" \
ASPNETCORE_ENVIRONMENT="$ENV" \
dotnet run --no-launch-profile --project "$(dirname "$0")/Llens.csproj"
