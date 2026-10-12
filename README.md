# Signal Forge DDS

DDSClient를 사용하는 웹 기반 DDS 작업 도구입니다. .NET 9, Blazor, MudBlazor와 RTI Connext DDS 7.3.1을 사용합니다.

## 실행 후 설정

- 메시지 모듈: 빌드된 모듈을 선택합니다. 임의 XML 업로드로 실행 중 메시지 타입을 만들지는 않습니다.
- 토픽: UI에서 이름, 메시지 타입, 발행/구독 방향, QoS 프로파일을 선택합니다. DDSClient에는 코드로 등록합니다.
- 연결 설정: `connection.json`으로 가져오기·내보내기합니다. Topics XML은 필요하지 않습니다.
- QoS: 외부 `qos_profiles.xml`을 UI 또는 원문으로 수정할 수 있습니다. 모듈이나 앱을 다시 빌드할 필요가 없습니다.
- 기존 DDS 세션은 생성 당시 설정을 유지합니다. 저장한 변경본은 새 세션에 적용됩니다.
- 사용 중인 프로필도 편집·저장할 수 있지만 삭제는 해당 세션 종료 후 가능합니다.

## 메시지 모듈 추가·교체

DDSClient에서 생성·빌드한 C# 메시지 모듈 DLL과 정확히 대응하는 계약 XML을 함께 배치한 뒤 앱을 재시작합니다. Signal Forge 재빌드는 필요하지 않습니다.

```text
modules/
  DdsAmbassador.Messages.Telemetry.dll
  Telemetry.xml
  DdsAmbassador.Messages.Telemetry2.dll
  Telemetry2.xml
```

- 기본 모듈 경로는 실행 파일 옆 `modules` 폴더입니다.
- `SIGNAL_FORGE_MODULE_DIRECTORY`로 추가/교체 모듈 경로를 지정할 수 있습니다. 같은 DLL 이름이면 외부 경로의 모듈이 우선합니다.
- Docker Compose는 영구 볼륨의 `/app/data/modules`를 추가/교체 경로로 사용합니다.
- DLL과 XML이 일치하지 않거나 모듈 이름이 중복되면 시작을 실패시킵니다.
- 모듈은 실행 코드를 포함합니다. 신뢰하는 빌드 산출물만 관리자가 배치해야 합니다.
- DDSClient Core와 RTI 런타임에 호환되는 모듈이어야 합니다. 서로 다른 운영체제의 네이티브 RTI 런타임은 대체할 수 없습니다.
- 실행 중 DLL 덮어쓰기·핫 리로드는 지원하지 않습니다. 앱을 정지한 상태에서 배치하고 다시 시작하세요.
- 메시지 구조가 바뀌면 기존 토픽 선택이나 저장 payload도 새 타입에 맞게 수정해야 합니다.

## 프로필 저장

`data/dds-profiles.json`은 프로필 목록과 저장 버전을, 하위 프로필 버전 폴더는 연결 JSON·QoS XML·메시지 계약 스냅샷을 보관합니다. 기존 Topics XML 프로필은 읽기 전용 호환 경로에서 JSON으로 변환하며, 새 저장·내보내기는 Topics XML을 생성하지 않습니다. 이전 파일과 백업은 복구용으로 보존합니다.

저장 위치는 `DdsProfiles__StoragePath`로 지정할 수 있습니다. 여러 브라우저의 동시 편집은 목록 리비전으로 충돌을 감지합니다. 하나의 프로필 볼륨에는 앱 한 개를 실행하세요.

## 빌드·배포

초기 소스 빌드에는 형제 경로의 DDSClient 저장소와 생성된 C# 모듈 프로젝트가 필요합니다.

```powershell
dotnet build .\ASAP.csproj --configuration Release
dotnet run --project .\ASAP.csproj
docker build -f Dockerfile -t signal-forge:ddsclient ..
```

빌드 시 모듈 목록은 DDSClient의 `DDS_Nuget/generated/BuiltModules.props`에서 가져오며, 선택한 DLL·XML을 배포 폴더로 복사합니다. XML 변경 후에는 먼저 DDSClient의 메시지 생성·빌드를 실행해야 합니다. Signal Forge 빌드 자체가 RTI 타입을 생성하지는 않습니다.

별도 빌드 목록은 `DdsModuleSelectionFile`, DDSClient 위치는 `DDSClientRoot`로 지정할 수 있습니다. 배포 후에는 새 모듈을 추가·교체하기 위해 앱을 다시 빌드할 필요가 없습니다.

RTI 라이선스는 `RTI_LICENSE_FILE`로 별도 제공하세요. Docker Compose는 `deploy/compose.yaml`, Kubernetes 자료는 `k8s/`에 있습니다. 라이선스·개인 키·실제 사용자 데이터는 소스 번들에 넣지 않습니다.

## 송수신·검증

일반 발행은 구독자 발견을 기다리지 않습니다. 과거 메시지 재생 여부는 QoS durability 정책에 따릅니다. UI 이력은 세션당 최근 50건으로 제한하지만 수신 이벤트와 누적 개수는 모두 처리합니다.

RTI가 제공한 원본 발행 시각이 있으면 수신 지연을 표시하고, 없으면 지연값을 만들지 않습니다. 서로 다른 PC의 시각 차이는 지연값에 영향을 줄 수 있습니다.

```powershell
./eng/test-dds-integration.ps1 -Docker -RuntimeStress
```

실제 DDS 부하 검사는 유효한 라이선스와 Docker가 필요합니다. 격리된 루프백 환경에서 실행하며 현재 앱 세션을 조작하지 않습니다. JSON 연결 설정·프로필 저장·모듈 로딩에 대한 검사 모드는 통합 검사 프로그램의 `--settings-regression`, `--module-catalog`입니다.
