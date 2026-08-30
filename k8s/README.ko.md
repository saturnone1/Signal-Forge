# Signal Forge 에어갭 Kubernetes 설치

이 번들은 검증된 `signal-forge:2026.08.30` 컨테이너 이미지와 Kubernetes 배포 설정을 함께 제공합니다.

## 구성

- `signal-forge-image-2026.08.30.tar.gz`: 오프라인 컨테이너 이미지
- `signal-forge.yaml`: Namespace, PVC, Deployment, Service
- `secret.example.yaml`: 선택형 HTTP Basic 인증 예시
- `install-airgap.sh`: 이미지 가져오기 및 배포
- `verify.sh`: 배포·볼륨·상태 확인
- `SHA256SUMS`: 파일 무결성 확인

## 사전 조건

- Linux Kubernetes 노드와 `kubectl`
- 이미지 런타임: k3s, containerd(`ctr`), 또는 Docker 중 하나
- 노드 TCP 5226 포트가 비어 있어야 함
- DDS 통신 대상과 Kubernetes 노드 사이의 UDP 유니캐스트/멀티캐스트 경로가 허용되어야 함
- 다중 노드 클러스터에서는 Signal Forge Pod가 배치될 수 있는 모든 노드에 이미지를 가져오거나, 대상 노드를 고정해야 함

Signal Forge는 DDS 네트워크를 노드 NIC에 직접 연결하기 위해 `hostNetwork: true`를 사용합니다. 같은 노드에서 5226 포트를 사용하는 다른 워크로드와 동시에 실행할 수 없습니다.

## 설치

```bash
cd signal-forge-airgap-2026-08-30
sha256sum -c SHA256SUMS
chmod +x install-airgap.sh verify.sh
./install-airgap.sh
```

설치 스크립트는 사용 가능한 런타임을 순서대로 확인해 이미지를 가져오고 `signal-forge.yaml`을 적용합니다. 수동 설치가 필요하면 다음 중 환경에 맞는 명령을 사용합니다.

```bash
# k3s
sudo k3s ctr images import signal-forge-image-2026.08.30.tar.gz

# 일반 containerd Kubernetes
sudo ctr -n k8s.io images import signal-forge-image-2026.08.30.tar.gz

# Docker 런타임
docker load -i signal-forge-image-2026.08.30.tar.gz

kubectl apply -f signal-forge.yaml
kubectl -n signal-forge rollout status deployment/signal-forge --timeout=180s
```

Pod가 배치된 노드의 IP가 `192.168.0.11`이면 브라우저 주소는 `http://192.168.0.11:5226`입니다.

## 선택: 접속 인증

`secret.example.yaml`을 복사하고 사용자 이름과 긴 임의 비밀번호를 설정한 뒤 적용합니다. 실제 비밀번호가 들어간 파일은 저장소나 공유 번들에 다시 넣지 마십시오.

```bash
cp secret.example.yaml secret.yaml
vi secret.yaml
kubectl apply -f secret.yaml
kubectl -n signal-forge rollout restart deployment/signal-forge
```

Secret이 없으면 인증 없이 동작합니다. `/health`는 Kubernetes probe를 위해 인증 대상에서 제외됩니다.

## 확인

```bash
./verify.sh
kubectl -n signal-forge logs deployment/signal-forge --tail=200
curl http://192.168.0.11:5226/health
```

정상 응답은 `{"status":"connected"}`입니다. DDS 검색이 되지 않으면 노드 방화벽, 멀티캐스트 라우팅, Domain ID, `topics.xml`, `qos_profiles.xml` 순서로 확인하십시오.

## 데이터와 업데이트

- DDS 프로필과 Data Protection 키는 PVC의 `/app/data`에 유지됩니다.
- 프로필 저장소는 단일 writer 전제이므로 `replicas: 1`과 `Recreate` 전략을 유지하십시오.
- 업데이트할 때 새 이미지를 각 대상 노드에 가져오고 manifest의 image 태그를 변경한 후 다시 적용합니다.
- PVC 백업 없이 Namespace/PVC를 삭제하지 마십시오.

```bash
POD="$(kubectl -n signal-forge get pod -l app.kubernetes.io/name=signal-forge -o jsonpath='{.items[0].metadata.name}')"
kubectl -n signal-forge exec "${POD}" -- tar -C /app -czf /tmp/signal-forge-data.tgz data
kubectl -n signal-forge cp "${POD}:/tmp/signal-forge-data.tgz" ./signal-forge-data.tgz
```

## 제거

애플리케이션만 제거하고 프로필 PVC는 남기려면 다음을 실행합니다.

```bash
kubectl -n signal-forge delete deployment signal-forge
kubectl -n signal-forge delete service signal-forge
```

`kubectl delete namespace signal-forge`는 PVC와 저장 데이터를 함께 제거할 수 있으므로 백업 후에만 수행하십시오.
