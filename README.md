# Visual Docker Manager (DockerDiagram)

Windows + WPF(.NET 9)로 만든 **Docker 시각화/관리 도구**입니다.  
Docker Desktop(Windows)과 연결해서 **컨테이너/볼륨/네트워크/이미지**를 목록으로 보고, 캔버스에 **노드(Node)**로 배치해 **관계(Connector)**를 그린 뒤 **레이아웃을 저장(.vdm)** 하거나 **docker-compose.yml로 내보내기** 할 수 있습니다.

> 이 저장소는 `net9.0-windows` / `UseWPF=true` 기반이며, Docker 엔진 연결은 `npipe://./pipe/docker_engine`(Windows Named Pipe)를 사용합니다.

---

## 주요 기능

### Docker 리소스 탐색 & 동기화
- Docker 엔진과 주기적으로 동기화하여 다음 항목을 표시합니다.
  - **Containers**
  - **Volumes**
  - **Networks**
  - **Docker Images**
- 목록에서 항목을 **드래그 앤 드롭**으로 캔버스에 배치합니다.
- 목록 항목에서 **삭제(컨테이너/볼륨/네트워크/이미지)**를 지원합니다.
  - 이미지 삭제 실패 시 **강제 삭제(force)**를 제안하는 흐름이 포함되어 있습니다.

### 캔버스(다이어그램) 편집
- 노드(Node): 컨테이너/볼륨/네트워크를 시각 요소로 배치
- 커넥터(Connector): 노드 간 관계를 표현
- 라우팅: `OrthogonalRouter`로 **직교(꺾이는) 경로** 생성 (포트 방향 포함)

### 관계(Connector) 타입
코드상 관계 타입은 3가지로 정의되어 있으며, 상황에 따라 표시 정보가 다릅니다.
- **Dependency**: Container ↔ Container
- **VolumeMount**: Container ↔ Volume (마운트 경로 표시 가능)
- **NetworkAttach**: Container ↔ Network (컨테이너 IP 표시 가능)

### 그룹(Group)
- 사각 드래그로 노드를 묶어 **그룹**을 생성/관리
- 그룹 단위 **정렬(Arrange)** 및 **Start All / Stop All** 제공

### 내보내기 & 저장
- **Save**: `.vdm` 파일로 레이아웃 저장 (JSON 기반)
- **Load**: `.vdm` 파일 로드
- **Export Compose**: 현재 시트를 `docker-compose.yml`로 내보내기
- 자동 저장: 마지막 저장 경로(`Properties.Settings.Default.LastFilePath`)가 존재하면 **1분 주기**로 QuickSave 수행

### Docker Desktop 감시
- Docker 프로세스가 꺼진 것을 감지하면 **재실행 여부를 묻고**, 거부 시 앱을 종료합니다.

---

## 요구 사항

- Windows 10/11
- Docker Desktop 설치 및 실행(권장: WSL2 백엔드)
- .NET SDK 9 (또는 Visual Studio에서 .NET 9 Desktop 개발 환경)
- (Terminal 기능 사용 시) `docker` CLI가 PATH에 잡혀있어야 합니다.
  - `OpenTerminal()`은 `cmd.exe`에서 `docker exec -it ...`를 실행합니다.

---

## 빌드/실행

### Visual Studio
1. Visual Studio에서 `DockerDiagram.csproj`를 엽니다.
2. 빌드 후 실행(F5)

### CLI
```bash
dotnet restore
dotnet build -c Release
dotnet run
```

---

## 사용 방법(빠른 가이드)

### 1) Docker 리소스 배치
- 좌측 패널의 **Existing Containers / Volumes / Networks / Docker Images**에서 항목을 캔버스로 드래그합니다.
- `Templates` 섹션의 **New Container / New Volume / New Network** 템플릿을 이용하면 생성 다이얼로그(예: `ContainerDialog`)를 통해 새 리소스 생성 흐름을 시작할 수 있습니다.

### 2) 줌/팬
- **마우스 휠**: Zoom (Scale)
- **오른쪽 버튼 드래그**: Pan (OffsetX/OffsetY)

### 3) 선택/삭제
- 요소 선택 후 **Delete 키**로 삭제할 수 있습니다.

### 4) 컨테이너 제어
- 컨테이너 노드 선택 시:
  - Start / Stop / Pause(토글) / Restart
  - Terminal (컨테이너 내부 쉘/명령 프롬프트 진입 시도)

---

## 저장 파일(.vdm) 포맷

저장 확장자는 `.vdm`이며, 내부는 JSON입니다.  
모델 정의는 `Models/SaveData.cs`의 `DiagramFile`, `SheetData`, `NodeData`, `ConnectionData`, `GroupData`를 따릅니다.

- 최상위: `DiagramFile`
  - `Version`, `SavedAt`, `ActiveSheetIndex`, `Sheets[]`
- 시트: `SheetData`
  - `Title`, `MapWidth`, `MapHeight`, `OffsetX`, `OffsetY`, `Scale`
  - `Nodes[]`, `Connections[]`, `Groups[]`
- 노드: `NodeData`
  - `Id`(다이어그램용), `DockerId`(실제 Docker ID), `Name`, `ImageName`, `Type`, `X/Y/Width/Height`
- 연결: `ConnectionData`
  - `SourceNodeId`, `TargetNodeId`, `SourceDir`, `TargetDir`, `RelationType`

> 참고: `NodeType`, `PortDirection`, `RelationType`는 `JsonStringEnumConverter`를 사용하지 않으므로 기본 직렬화 기준으로 **숫자(enum ordinal)** 로 저장됩니다.

---

## 프로젝트 구조

```
DockerDiagram/
├─ DockerDiagram.csproj
├─ App.xaml / App.xaml.cs
├─ MainWindow.xaml / MainWindow.xaml.cs
├─ Helpers/
│  ├─ DockerApiService.cs          # Docker.DotNet 기반 API (start/stop/pause/restart, copy, exec 등)
│  ├─ DockerServiceHelper.cs       # Docker Desktop 실행/감지 (Windows)
│  ├─ ComposeExportService.cs      # docker-compose.yml 생성
│  ├─ FileService.cs              # .vdm 저장/로드(QuickSave 포함)
│  ├─ OrthogonalRouter.cs          # 직교 라우팅 + PortDirection
│  └─ RelayCommand.cs / ViewModelBase.cs ...
├─ Models/
│  ├─ NodeType.cs
│  ├─ DockerContainer.cs / DockerImage.cs
│  ├─ TemplateItem.cs
│  └─ SaveData.cs                  # .vdm 파일 모델
├─ ViewModels/
│  ├─ MainViewModel.cs             # 전체 상태/동기화/시트 관리/삭제/Export/Save
│  ├─ SheetViewModel.cs            # 캔버스 상태(줌/팬, 노드/커넥터/그룹)
│  ├─ NodeViewModel.cs             # 노드(컨테이너/볼륨/네트워크) + 컨테이너 제어 커맨드
│  ├─ ConnectorViewModel.cs        # 연결선(관계 타입, IP/마운트 경로 표시)
│  └─ GroupViewModel.cs            # 그룹(Arrange, StartAll/StopAll)
└─ Views/
   ├─ ContainerDialog.xaml(.cs)
   ├─ VolumeDialog.xaml(.cs)
   ├─ NetworkDialog.xaml(.cs)
   ├─ MountDialog.xaml(.cs)
   └─ ArrangeDialog.xaml(.cs)
```

---

## 트러블슈팅

### Docker가 실행 중인데도 연결이 안 될 때
- Docker Desktop이 **정상적으로 Running**인지 확인
- Windows에서 Named Pipe(`npipe://./pipe/docker_engine`) 접근 권한 문제일 수 있습니다.
- Docker Desktop 재시작 후 재시도

### Terminal 버튼이 동작하지 않을 때
- `docker` CLI가 PATH에 없으면 `docker exec ...`가 실패합니다.
- PowerShell/CMD에서 `docker version`이 동작하는지 확인하세요.

---

## 참고(패키지)

- Docker.DotNet
- System.ServiceProcess.ServiceController

---

## 기술적 특징 (Advanced Features)
- **Zero-Loss 볼륨 연결**: 실행 중인 컨테이너에 볼륨을 추가할 때, 내부 데이터를 호스트로 백업 후 재생성된 컨테이너로 복원하는 마이그레이션 로직을 구현하여 데이터 무결성을 보장합니다.
- **스마트 그룹 제어**: DAG(유향 비순환 그래프) 분석을 통해 컨테이너 간 의존성을 파악하고, 올바른 순서대로 시작(Start All) 및 종료(Stop All)를 수행합니다.
- **실시간 양방향 동기화**: `DispatcherTimer`와 `Docker API`를 활용해 외부에서의 변경 사항(생성/삭제)을 UI에 즉시 반영하고, UI 조작을 Docker 엔진 명령으로 실시간 변환합니다.

---

## 추가 예정

- **private 이미지 관련 처리**
- **swarm 기능**
- **저장 세분화**
- **엔드포인트 지정**
- **etc**

---

## 라이선스

이 ZIP에 별도의 LICENSE 파일이 포함되어 있지 않습니다. 필요하다면 라이선스를 추가해 주세요.
