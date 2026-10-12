Signal Forge 소스·배포 번들

준비: Linux 컨테이너용 Docker/Compose와 유효한 RTI Connext DDS 7.3.1 라이선스.
라이선스와 Docker/WSL 설치 파일은 이 번들에 포함하지 않습니다.

PowerShell 7에서 실행:
  ./deployment/start.ps1 -LicenseFile C:/path/to/rti_license.dat

Linux에서 실행:
  docker load -i deployment/signal-forge-images.tar
  export RTI_LICENSE_FILE=/absolute/path/to/rti_license.dat
  docker compose -p signal-forge -f deployment/compose.yaml up -d --pull never

기본 배포는 앱 1개이며 접속 주소는 http://localhost:5237입니다.
-Port 또는 SIGNAL_FORGE_PORT 환경변수로 포트를 변경할 수 있습니다.

같은 이미지로 2개가 필요할 때는 프로젝트 이름과 포트를 다르게 지정하세요:
  ./deployment/start.ps1 -LicenseFile C:/path/to/rti_license.dat -ProjectName signal-forge-a -Port 5237
  ./deployment/start.ps1 -LicenseFile C:/path/to/rti_license.dat -ProjectName signal-forge-b -Port 5238
각 프로젝트의 데이터 볼륨과 쿠키 식별자는 자동으로 분리됩니다.
같은 메시지 모듈·토픽·타입·도메인·호환 QoS로 설정하고 수신을 먼저 시작하세요.
멀티캐스트 Discovery 기본 주소는 239.255.0.1입니다.
HTTP는 로컬에만 공개됩니다. 외부 공개 전 인증·방화벽을 검토하세요.
쿠키 식별자는 Compose 프로젝트 이름을 사용합니다. 업그레이드 시 프로젝트 이름을 유지하세요.

폐쇄망에서 소스 재빌드:
  source/DDSClient-source.zip을 source 아래에 풉니다(source/DDSClient 생성).
  docker load -i deployment/signal-forge-images.tar
  cd source
  docker build --pull=false --network none --target final -f Signal-Forge/Dockerfile -t signal-forge:rebuilt .

재빌드한 이미지로 실행하려면 SIGNAL_FORGE_IMAGE=signal-forge:rebuilt를 설정하세요.
이미지 파일에는 실행 앱·.NET SDK·ASP.NET 기반 이미지가 들어 있습니다.
소스에는 로컬 NuGet 저장소와 생성된 메시지 타입이 포함됩니다.
XML 정의를 바꾸려면 별도 설치한 RTI SDK로 DDSClient 코드를 먼저 재생성하세요.

운영 프로파일·사용자 데이터·키·라이선스·Git 기록·자격 증명은 제외합니다.
새 배포에 운영 프로필을 자동 복사하지는 않습니다. 같은 데이터 볼륨을 사용하는 업그레이드는 기존 Topics XML 프로필을 JSON 연결 설정으로 변환하고 원본·백업을 보존합니다.

토픽은 UI에서 선택하며 connection.json과 qos_profiles.xml 두 파일로 가져오기·내보내기합니다.
QoS XML은 빌드 후에도 수정할 수 있으며 저장한 변경본은 새 DDS 세션에 적용됩니다.
메시지 모듈 추가·교체는 앱 재빌드 없이 가능합니다. DLL과 대응하는 모듈 XML을 함께 배치한 뒤 앱을 재시작하세요.
Compose의 추가/교체 모듈 경로는 데이터 볼륨 안의 /app/data/modules입니다.
신뢰하는 DLL만 배치해야 하며 기존 DDSClient Core·RTI 런타임과 호환되어야 합니다.
통합 테스트: source/Signal-Forge/eng/test-dds-integration.ps1 -Docker -Rti
