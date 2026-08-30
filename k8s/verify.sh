#!/usr/bin/env bash
set -euo pipefail

kubectl -n signal-forge get deployment,pod,service,pvc -o wide
kubectl -n signal-forge rollout status deployment/signal-forge --timeout=60s
POD="$(kubectl -n signal-forge get pod -l app.kubernetes.io/name=signal-forge -o jsonpath='{.items[0].metadata.name}')"
kubectl -n signal-forge exec "${POD}" -- sh -c 'test -w /app/data'
NODE_NAME="$(kubectl -n signal-forge get pod "${POD}" -o jsonpath='{.spec.nodeName}')"
NODE_IP="$(kubectl get node "${NODE_NAME}" -o jsonpath='{.status.addresses[?(@.type=="InternalIP")].address}')"
curl --fail --silent --show-error "http://${NODE_IP}:5226/health"
