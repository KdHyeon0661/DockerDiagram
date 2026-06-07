# Visual Docker Manager (DockerDiagram)

**Visual Docker Manager**는 Windows WPF 기반의 Docker 시각화 및 관리 도구입니다.

복잡한 Docker 명령어를 직접 기억하지 않아도, 컨테이너, 볼륨, 네트워크, 외부 연결을 캔버스 위에 노드로 배치하고 선으로 연결하면서 실제 Docker 리소스를 관리할 수 있습니다.

## 주요 기능

- 컨테이너, 볼륨, 네트워크, 그룹, 연결선을 다루는 시각적 Docker 캔버스
- Local Docker, SSH 원격 Docker, Docker context 기반 연결 지원
- 컨테이너 생성, 시작, 중지, 일시정지, 재시작, kill, rename, exec 지원
- 이미지 pull, search, delete, build, tag, push, save, load, import 지원
- 볼륨 고급 옵션, 백업/복원, 사용 감지, 삭제 보호, 안전 prune 지원
- 네트워크 IPAM, subnet, gateway, IPv6, labels, driver options, static IP, aliases, endpoint options 지원
- 컨테이너/볼륨/네트워크 raw inspect JSON 보기
- Docker events 기반 실시간 동기화와 보조 polling
- Docker Engine `system/df` 기반 디스크 사용량 보기
- Docker Compose import/export와 네트워크/볼륨 옵션 보존
- `.vdm` 프로젝트 저장/불러오기

## 시각적 Docker 다이어그램

Docker 리소스를 캔버스 위의 시각 요소로 표현합니다.

- 컨테이너 노드
- 볼륨 노드
- 네트워크 그룹
- 인터넷/외부 연결 노드
- 의존성 연결선
- 볼륨 마운트 연결선
- 네트워크 연결선

캔버스는 드래그 앤 드롭, 선택, 리사이즈, 직각 라우팅, 줌, 패닝, undo/redo, 다중 시트를 지원합니다.

## 연결 관리

여러 Docker 환경을 하나의 앱 안에서 관리할 수 있습니다.

- Local Docker Desktop
- SSH 터널을 통한 원격 Docker
- Docker context 기반 연결
- 여러 연결 워크스페이스

각 워크스페이스는 독립적인 시트와 Docker 연결 정보를 가질 수 있습니다.

## 컨테이너 관리

컨테이너 관련 기능은 다음을 지원합니다.

- UI 옵션 기반 컨테이너 생성 및 실행
- `docker run` 형태의 명령어 분석 및 실행
- start, stop, pause, unpause, restart, kill
- 컨테이너 이름 변경
- 사용자 지정 exec 명령 실행
- 터미널 열기
- 로그 및 상세 정보 보기
- 컨테이너를 이미지로 commit
- 컨테이너 파일시스템을 tar로 export
- tar 파일시스템을 이미지로 import
- 기존 컨테이너 정보에서 Dockerfile 형태의 요약 추출

## 이미지 관리

이미지 관리 화면과 리소스 목록에서 Docker 이미지를 관리할 수 있습니다.

- 이미지 검색
- 이미지 pull
- 이미지 삭제 및 강제 삭제
- Dockerfile context 기반 이미지 build
- 이미지 tag
- 이미지 push
- 이미지를 tar로 save
- tar 이미지 load
- root filesystem tar를 이미지로 import

이미지 build는 `docker build` CLI를 직접 실행하지 않고 Docker.DotNet을 통해 Docker Engine API로 처리합니다.

## 볼륨 관리

볼륨은 단순 생성뿐 아니라 고급 옵션과 데이터 보호 흐름을 지원합니다.

- 관리형 볼륨 생성
- 외부 볼륨 참조
- 다이어그램 이름과 실제 Docker 볼륨 이름 분리
- driver, driver options, labels 설정
- `.vdm` 저장 시 볼륨 옵션 보존
- Compose import/export 시 볼륨 옵션 보존
- 볼륨 raw inspect JSON 보기
- driver, mountpoint, labels, options, size, ref count 표시
- 사용 중인 컨테이너와 mount destination, read/write 정보 표시
- 볼륨 tar 백업/복원
- 백업, 삭제, 재생성, 복원, rollback 보호를 포함한 볼륨 재생성
- 사용 중인 볼륨 삭제 보호
- 삭제될 미사용 볼륨 목록을 먼저 보여주는 안전 volume prune

## 네트워크 관리

네트워크 생성과 컨테이너 연결 옵션을 세밀하게 설정할 수 있습니다.

- driver 선택
- IPAM subnet
- gateway
- ip_range
- aux_addresses
- internal network
- attachable
- IPv6 활성화
- labels
- driver options
- macvlan/ipvlan parent 옵션
- 컨테이너별 static IPv4
- 컨테이너별 static IPv6
- network aliases
- endpoint driver options

기존 방식도 유지됩니다. 네트워크 영역을 만들고, 컨테이너 노드를 그 영역 위에 배치하면 해당 네트워크에 연결되는 흐름을 계속 사용할 수 있습니다.

## Docker Compose 지원

Docker Compose YAML을 가져오거나 내보낼 수 있습니다.

보존 대상은 다음과 같습니다.

- services
- depends_on
- ports
- environment
- volume mounts
- top-level volumes
- top-level networks
- network IPAM options
- network labels / driver_opts
- volume external / name / labels / driver_opts
- round-trip 품질을 위한 raw Compose YAML 조각

Compose 배포는 `IComposeService` 뒤로 격리되어 있습니다. 기본 구현은 Docker Compose CLI 플러그인을 사용합니다. `docker compose up`은 Docker Engine API의 단일 기능이 아니라 Compose 플러그인이 여러 Engine API 호출로 풀어 실행하는 기능이기 때문입니다.

## 시스템 도구

전역 Docker 관리 기능은 다음을 지원합니다.

- Docker system disk usage 보기
- container prune
- image prune
- network prune
- system prune
- 안전 volume prune
- Docker events 기반 동기화
- raw inspect viewer

## 요구 사항

- Windows 10 또는 Windows 11
- Docker Desktop 또는 접근 가능한 Docker Engine
- Compose 배포 기능 사용 시 Docker Compose CLI 플러그인
- .NET SDK 10
- Visual Studio의 .NET Desktop Development workload 또는 .NET CLI

일부 기능은 Docker CLI가 PATH에 있어야 합니다.

- `docker exec -it` 기반 대화형 터미널
- `docker compose` 기반 Compose 배포
- Docker context 검색 및 관리

핵심 Docker 리소스 관리는 가능한 범위에서 Docker.DotNet과 Docker Engine API를 사용합니다.

## 빌드 및 실행

### Visual Studio

1. `DockerDiagram.csproj`를 엽니다.
2. NuGet 패키지를 복원합니다.
3. F5로 빌드 및 실행합니다.

### CLI

```bash
dotnet restore
dotnet build
dotnet run
```

## 빠른 사용 방법

1. Docker Desktop을 실행하거나 원격 Docker 연결을 준비합니다.
2. DockerDiagram을 실행합니다.
3. 왼쪽 리소스 목록에서 기존 컨테이너, 볼륨, 네트워크를 캔버스로 드래그합니다.
4. 템플릿에서 새 컨테이너, 볼륨, 네트워크, 그룹, 인터넷 노드를 만들 수 있습니다.
5. 컨테이너와 볼륨 또는 네트워크를 선으로 연결합니다.
6. 항목을 선택하면 상세 정보와 작업 버튼을 사용할 수 있습니다.
7. 프로젝트를 `.vdm` 파일로 저장합니다.

## `.vdm` 프로젝트 파일

DockerDiagram은 다이어그램을 `.vdm` 파일로 저장합니다.

파일은 JSON 기반이며 다음 정보를 저장합니다.

- 파일 포맷 버전
- 저장 시각
- 연결 워크스페이스
- 시트 목록
- 캔버스 크기, 오프셋, 확대 비율
- 노드
- 연결선
- 그룹
- Docker 리소스 메타데이터
- 네트워크/볼륨 고급 옵션
- Compose 보존 데이터

파일 포맷 버전은 루트 `DiagramFile` 모델에 남겨둡니다. 이후 저장 구조가 바뀌어도 구버전 `.vdm` 파일과의 호환성을 판단하기 위해 필요합니다.

## 프로젝트 구조

```text
DockerDiagram/
├─ DockerDiagram.csproj
├─ App.xaml / App.xaml.cs
├─ MainWindow.xaml / MainWindow.xaml.cs
├─ Helpers/
│  ├─ DockerApiService.cs
│  ├─ DockerInterface.cs
│  ├─ DockerContextService.cs
│  ├─ DockerComposeCliService.cs
│  ├─ ComposeImportService.cs
│  ├─ ComposeExportService.cs
│  ├─ FileService.cs
│  ├─ DialogService.cs
│  ├─ SshTunnelManager.cs
│  └─ OrthogonalRouter.cs
├─ Models/
│  ├─ SaveData.cs
│  ├─ DockerContainer.cs
│  ├─ DockerImage.cs
│  ├─ NetworkCreateOptions.cs
│  ├─ VolumeCreateOptions.cs
│  └─ ...
├─ ViewModels/
│  ├─ MainViewModel.cs
│  ├─ SheetManagerViewModel.cs
│  ├─ SheetViewModel.cs
│  ├─ NodeViewModel.cs
│  ├─ ConnectorViewModel.cs
│  ├─ GroupViewModel.cs
│  ├─ ResourceExplorerViewModel.cs
│  ├─ InspectorViewModel.cs
│  └─ ToolboxViewModel.cs
└─ Views/
   ├─ ContainerDialog.xaml
   ├─ VolumeDialog.xaml
   ├─ NetworkDialog.xaml
   ├─ ImageManagerWindow.xaml
   ├─ SystemDiskUsageWindow.xaml
   ├─ RawInspectWindow.xaml
   └─ ...
```

## 구조 메모

이 앱은 MVVM에 맞춰 구조를 정리하고 있습니다.

- ViewModel은 상태와 명령을 관리합니다.
- 창 생성과 파일 다이얼로그는 `IDialogService`를 통해 처리합니다.
- Docker Engine 통신은 서비스 인터페이스를 통해 분리합니다.
- Compose 배포는 `IComposeService` 뒤로 격리합니다.
- Docker API 호출은 가능한 경우 Docker.DotNet을 사용합니다.
- Engine API로 직접 대응하기 어려운 기능만 전용 인프라 서비스 안에서 CLI를 사용합니다.

## 문제 해결

### Docker가 감지되지 않을 때

- Docker Desktop이 실행 중인지 확인합니다.
- 터미널에서 `docker version`이 동작하는지 확인합니다.
- Docker Desktop을 재시작합니다.
- Windows named pipe 또는 Docker endpoint 접근 권한을 확인합니다.

### Compose 배포가 실패할 때

- `docker compose version`이 동작하는지 확인합니다.
- Docker Compose CLI 플러그인이 설치되어 있는지 확인합니다.
- 현재 선택한 연결 프로필이 대상 Docker Engine에 접근 가능한지 확인합니다.

### SSH 연결이 실패할 때

- host, port, username, private key 경로를 확인합니다.
- 원격 서버에 SSH 접속이 가능한지 확인합니다.
- 방화벽과 포트 접근 권한을 확인합니다.

### 터미널이 열리지 않을 때

- Docker CLI가 PATH에 있는지 확인합니다.
- 컨테이너 안에 `bash`, `sh`, `cmd`, `powershell` 중 하나가 있는지 확인합니다.

## 향후 개선 후보

- Docker Compose 프로젝트 라이프사이클 대시보드
- registry login/logout 관리
- Buildx / BuildKit cache 관리
- Docker Swarm 지원
- Kubernetes 연동
- Docker secrets/configs
- 더 정밀한 볼륨 크기 분석

## 라이선스

이 프로젝트는 MIT License를 따릅니다.  
자세한 내용은 [LICENSE](./LICENSE) 파일을 참고하세요.
