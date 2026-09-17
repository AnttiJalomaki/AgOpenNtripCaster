#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

ENGINE="${CONTAINER_ENGINE:-}"
IMAGE_REPO="${DOCKER_IMAGE_REPO:-docker.io/nevaberry/agopen-ntripcaster}"
TAG="${APP_TAG:-}"
PUSH=false
TAG_PROD=false

usage() {
    cat <<'EOF'
Usage: deploy/build-private-images.sh [options]

Options:
  --push              Push images to Docker Hub after building
  --prod              Also tag images as backend-prod and web-prod
  --tag TAG           Use TAG instead of the current git short SHA
  --repo REPO         Use image repository instead of docker.io/nevaberry/agopen-ntripcaster
  --engine ENGINE     Use podman or docker

Examples:
  deploy/build-private-images.sh
  deploy/build-private-images.sh --push --tag "$(git rev-parse --short=12 HEAD)"
  deploy/build-private-images.sh --push --prod
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --push)
            PUSH=true
            shift
            ;;
        --prod)
            TAG_PROD=true
            shift
            ;;
        --tag)
            TAG="$2"
            shift 2
            ;;
        --repo)
            IMAGE_REPO="$2"
            shift 2
            ;;
        --engine)
            ENGINE="$2"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 1
            ;;
    esac
done

if [[ -z "$ENGINE" ]]; then
    if command -v podman >/dev/null 2>&1; then
        ENGINE=podman
    elif command -v docker >/dev/null 2>&1; then
        ENGINE=docker
    else
        echo "Neither podman nor docker is available." >&2
        exit 1
    fi
fi

if [[ -z "$TAG" ]]; then
    TAG="$(git -C "$PROJECT_ROOT" rev-parse --short=12 HEAD)"
fi

BACKEND_IMAGE="${IMAGE_REPO}:backend-${TAG}"
WEB_IMAGE="${IMAGE_REPO}:web-${TAG}"
BACKEND_PROD_IMAGE="${IMAGE_REPO}:backend-prod"
WEB_PROD_IMAGE="${IMAGE_REPO}:web-prod"

build_args=(build --platform linux/amd64)
if [[ "$ENGINE" == "podman" ]]; then
    build_args+=(--format docker)
fi

if [[ "$PUSH" == true && "$ENGINE" == "podman" ]] && ! "$ENGINE" login --get-login docker.io >/dev/null 2>&1; then
    echo "Podman is not logged in to Docker Hub. Run: podman login docker.io -u nevaberry" >&2
    exit 1
fi

echo "Building $BACKEND_IMAGE"
"$ENGINE" "${build_args[@]}" -f "$PROJECT_ROOT/deploy/Dockerfile.backend" -t "$BACKEND_IMAGE" "$PROJECT_ROOT"

echo "Building $WEB_IMAGE"
"$ENGINE" "${build_args[@]}" -f "$PROJECT_ROOT/deploy/Dockerfile.web" -t "$WEB_IMAGE" "$PROJECT_ROOT"

if [[ "$TAG_PROD" == true ]]; then
    "$ENGINE" tag "$BACKEND_IMAGE" "$BACKEND_PROD_IMAGE"
    "$ENGINE" tag "$WEB_IMAGE" "$WEB_PROD_IMAGE"
fi

if [[ "$PUSH" == true ]]; then
    echo "Pushing $BACKEND_IMAGE"
    "$ENGINE" push "$BACKEND_IMAGE"
    echo "Pushing $WEB_IMAGE"
    "$ENGINE" push "$WEB_IMAGE"

    if [[ "$TAG_PROD" == true ]]; then
        echo "Pushing $BACKEND_PROD_IMAGE"
        "$ENGINE" push "$BACKEND_PROD_IMAGE"
        echo "Pushing $WEB_PROD_IMAGE"
        "$ENGINE" push "$WEB_PROD_IMAGE"
    fi
fi

echo "Built images:"
echo "  $BACKEND_IMAGE"
echo "  $WEB_IMAGE"
if [[ "$TAG_PROD" == true ]]; then
    echo "  $BACKEND_PROD_IMAGE"
    echo "  $WEB_PROD_IMAGE"
fi
