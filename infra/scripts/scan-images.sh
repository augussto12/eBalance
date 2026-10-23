#!/usr/bin/env bash
# Escaneo de vulnerabilidades de las imágenes que construye este repo (api, web, backup) con Trivy fijado por versión y
# digest. Falla si hay HIGH/CRITICAL CON corrección publicada que no estén en infra/trivy-ignore.yaml (cada excepción
# lleva motivo, archivo y vencimiento). Lo corre el CI; también sirve en la VPS o localmente:
#   ./infra/scripts/scan-images.sh [TAG]       por defecto el IMAGE_TAG de .env, o «local»
#   SCAN_BASE=1 ./infra/scripts/scan-images.sh además INFORMA (sin fallar) sobre postgres oficial (servicio db)
# Trivy corre en un contenedor sin docker.sock: las imágenes se le pasan exportadas con «docker save».
# Detrás de un proxy TLS: HTTPS_PROXY y EXTRA_CA_FILE (igual que deploy.sh). Actualizar Trivy: docs/VERSIONES.md.
set -euo pipefail
cd "$(dirname "$0")/../.."
TRIVY_IMAGE="${TRIVY_IMAGE:-aquasec/trivy:0.75.0@sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa}"
tag="${1:-}"
if [[ -z "$tag" && -f .env ]]; then tag="$(sed -n 's/^IMAGE_TAG=//p' .env | tail -1)"; fi
tag="${tag:-local}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cache="${TRIVY_CACHE_DIR:-$HOME/.cache/alvarez-trivy}"
mkdir -p "$cache"
run=(docker run --rm -v "$work:/work:ro" -v "$PWD/infra/trivy-ignore.yaml:/ignore.yaml:ro" -v "$cache:/root/.cache")
if [[ -n "${HTTPS_PROXY:-}" ]]; then run+=(--network host -e "HTTPS_PROXY=$HTTPS_PROXY" -e "NO_PROXY=${NO_PROXY:-}"); fi
if [[ -n "${EXTRA_CA_FILE:-}" ]]; then run+=(-v "$(realpath "$EXTRA_CA_FILE"):/extra-ca.pem:ro" -e SSL_CERT_FILE=/extra-ca.pem); fi
common=(--scanners vuln --severity "HIGH,CRITICAL" --ignore-unfixed --ignorefile /ignore.yaml --no-progress)

scan() { # imagen archivo exit-code → devuelve el código de Trivy
  local rc=0
  docker save -o "$work/$2.tar" "$1" || return 2
  chmod 644 "$work/$2.tar"
  echo "==> $1"
  "${run[@]}" "$TRIVY_IMAGE" image --input "/work/$2.tar" "${common[@]}" --exit-code "$3" || rc=$?
  rm -f "$work/$2.tar"
  return "$rc"
}

rc=0
for img in api web backup; do
  scan "alvarez-finanzas-$img:$tag" "$img" 1 || rc=1
done
if [[ "${SCAN_BASE:-0}" == 1 ]]; then
  pg="$(sed -n 's/^ *image: \(postgres:[^ ]*\)$/\1/p' compose.yaml | head -1)"
  scan "$pg" postgres 0 || true
fi
if (( rc )); then
  echo "Escaneo con hallazgos HIGH/CRITICAL corregibles: actualizá la imagen base o la dependencia (docs/VERSIONES.md)." >&2
  exit 1
fi
echo "Escaneo de imágenes OK (sin HIGH/CRITICAL corregibles fuera de infra/trivy-ignore.yaml)"
