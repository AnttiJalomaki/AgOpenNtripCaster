#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IMAGE_REPO="${DOCKER_IMAGE_REPO:-docker.io/nevaberry/agopen-ntripcaster}"
APP_TAG_VALUE="${1:-${APP_TAG:-prod}}"
DOCKERHUB_USERNAME="${DOCKERHUB_USERNAME:-nevaberry}"
TOKEN_FILE="${DOCKER_TOKEN_FILE:-/root/docker.env}"

read_token_file() {
    local file="$1"
    local line key value

    [[ -f "$file" ]] || return 0

    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line#"${line%%[![:space:]]*}"}"
        line="${line%"${line##*[![:space:]]}"}"

        [[ -z "$line" || "${line:0:1}" == "#" ]] && continue

        if [[ "$line" == *=* ]]; then
            key="${line%%=*}"
            value="${line#*=}"
            value="${value%\"}"
            value="${value#\"}"
            value="${value%\'}"
            value="${value#\'}"

            case "$key" in
                DOCKERHUB_USERNAME|DOCKER_USERNAME)
                    DOCKERHUB_USERNAME="$value"
                    ;;
                DOCKERHUB_TOKEN|DOCKER_TOKEN|DOCKER_PASSWORD)
                    DOCKERHUB_TOKEN="$value"
                    ;;
            esac
        else
            DOCKERHUB_TOKEN="$line"
        fi
    done < "$file"
}

read_token_file "$TOKEN_FILE"

if [[ -n "${DOCKERHUB_TOKEN:-}" ]]; then
    printf '%s' "$DOCKERHUB_TOKEN" | docker login docker.io -u "$DOCKERHUB_USERNAME" --password-stdin >/dev/null
    echo "Docker Hub login succeeded for $DOCKERHUB_USERNAME"
else
    echo "No Docker Hub token found. Put a read-only token in $TOKEN_FILE or login manually." >&2
    exit 1
fi

cd "$SCRIPT_DIR"

echo "Deploying $IMAGE_REPO with APP_TAG=$APP_TAG_VALUE"
APP_TAG="$APP_TAG_VALUE" DOCKER_IMAGE_REPO="$IMAGE_REPO" docker compose -f docker-compose.prod.yml pull
APP_TAG="$APP_TAG_VALUE" DOCKER_IMAGE_REPO="$IMAGE_REPO" docker compose -f docker-compose.prod.yml up -d --remove-orphans
APP_TAG="$APP_TAG_VALUE" DOCKER_IMAGE_REPO="$IMAGE_REPO" docker compose -f docker-compose.prod.yml ps
