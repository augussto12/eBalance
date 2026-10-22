#!/usr/bin/env bash
# Construye las imágenes locales. EXTRA_CA_FILE (opcional) agrega una CA solo en las etapas de build,
# para entornos detrás de un proxy TLS corporativo. En la VPS normalmente no hace falta.
set -euo pipefail
cd "$(dirname "$0")/../.."
tag="${1:-local}"
args=(--load)
if [[ -n "${EXTRA_CA_FILE:-}" ]]; then
  args+=(--network host --secret "id=extra_ca,src=$EXTRA_CA_FILE")
  [[ -n "${HTTPS_PROXY:-}" ]] && args+=(--build-arg "HTTPS_PROXY=$HTTPS_PROXY" --build-arg "NO_PROXY=${NO_PROXY:-}")
fi
for img in api web backup; do
  docker buildx build "${args[@]}" -f "infra/docker/$img.Dockerfile" -t "alvarez-finanzas-$img:$tag" .
done
