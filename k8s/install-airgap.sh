#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
IMAGE_ARCHIVE="${1:-${SCRIPT_DIR}/signal-forge-image-2026.08.30.tar.gz}"

if [[ ! -f "${IMAGE_ARCHIVE}" ]]; then
  echo "Image archive not found: ${IMAGE_ARCHIVE}" >&2
  exit 1
fi
command -v kubectl >/dev/null || { echo "kubectl is required" >&2; exit 1; }

if command -v k3s >/dev/null; then
  sudo k3s ctr images import "${IMAGE_ARCHIVE}"
elif command -v ctr >/dev/null; then
  sudo ctr -n k8s.io images import "${IMAGE_ARCHIVE}"
elif command -v docker >/dev/null; then
  docker load -i "${IMAGE_ARCHIVE}"
else
  echo "One of k3s, ctr, or docker is required to import the image" >&2
  exit 1
fi

kubectl apply -f "${SCRIPT_DIR}/signal-forge.yaml"
kubectl -n signal-forge rollout status deployment/signal-forge --timeout=180s
kubectl -n signal-forge get pod -o wide

NODE_NAME="$(kubectl -n signal-forge get pod -l app.kubernetes.io/name=signal-forge -o jsonpath='{.items[0].spec.nodeName}')"
NODE_IP="$(kubectl get node "${NODE_NAME}" -o jsonpath='{.status.addresses[?(@.type=="InternalIP")].address}')"
echo "Signal Forge: http://${NODE_IP}:5226"
