# DockerDiagram

DockerDiagram은 Docker 리소스를 다이어그램으로 이해하고, 같은 화면에서 실제 리소스를 조회·생성·제어할 수 있도록 만든 Windows 데스크톱 애플리케이션이다. 단순한 그림 편집기가 아니라 Docker Engine의 상태와 시각 모델을 연결하여, 인프라 구조 파악과 운영 작업을 한 흐름 안에서 수행하는 것을 목표로 한다.

## 프로젝트가 해결하려는 문제

Docker CLI와 관리 화면은 개별 리소스의 상태를 확인하기에는 효율적이지만, 다음 관계를 한눈에 파악하기는 어렵다.

- 어떤 컨테이너가 서로 의존하는가
- 컨테이너가 어느 네트워크에 속하는가
- 어느 볼륨이 어떤 컨테이너에 마운트되는가
- 외부 포트가 어느 서비스로 이어지는가
- Compose 구성이 실제 실행 리소스와 어떻게 대응하는가

DockerDiagram은 이 관계를 노드, 그룹, 연결선으로 표현한다. 발견된 리소스를 시트에 배치하거나 Compose YAML을 가져오면 구조를 자동으로 구성하며, 선택한 리소스의 상세 정보와 실행 작업도 제공한다.

## 핵심 기능

### Docker 리소스 탐색과 제어

- 로컬 또는 원격 Docker Engine 연결
- 컨테이너, 이미지, 볼륨, 네트워크 조회
- 컨테이너 시작, 중지, 재시작, 일시 정지, 종료, 삭제, 이름 변경
- 로그, 실시간 자원 사용량, 상세 설정, 파일 전송, 명령 실행
- 이미지 검색, 가져오기, 빌드, 태그, 저장, 불러오기, 삭제
- 볼륨 생성, 사용량 확인, 백업, 복원, 재생성
- 네트워크 생성과 컨테이너 연결·해제

### 시각적 다이어그램 편집

- 컨테이너, 볼륨, 인터넷 노드 생성
- 일반 그룹과 네트워크 그룹 생성
- 노드 이동·크기 조절과 직교 연결선 유지
- 확대, 축소, 이동, 화면 맞춤
- 여러 시트 관리와 시트별 연결 프로필 유지
- 선택 항목 속성 편집과 실행 상태 표시

### Compose 가져오기와 자동 배치

- `.yml`, `.yaml` 파일 분석
- 서비스, 네트워크, 볼륨, 포트, 환경 변수, 의존 관계 변환
- 실행 여부를 선택할 수 있는 가져오기 흐름
- Tree, Forest, DAG, Cycle을 구분하는 토폴로지 분석
- 계층형 컨테이너 배치, 볼륨 선반 배치, 네트워크 경계 계산
- 겹침을 줄이는 직교 연결선 라우팅
- 편집 결과의 Compose YAML 내보내기

### Kubernetes 리소스 시각화와 작업

- 현재 컨텍스트의 Node, Pod, Deployment, ReplicaSet, Service 조회
- ConfigMap, Secret, Ingress, PersistentVolumeClaim 조회
- 소유 관계, selector, 볼륨 청구 관계 시각화
- YAML 확인, describe, 로그 확인, 스케일 조정, 재시작, 삭제
- 매니페스트 적용과 포트 포워딩

### 저장, 복원, 동기화

- 노드, 그룹, 연결선, 화면 위치, 배율, 연결 정보를 파일로 저장
- 저장된 다이어그램을 오프라인 구조로 먼저 복원한 뒤 실제 상태 재연결
- Docker 이벤트 감시와 주기적 보정 동기화
- 실행 취소와 다시 실행

## 기술 구성

| 구분 | 사용 기술 |
|---|---|
| UI | WPF, XAML |
| 실행 환경 | .NET 10 for Windows |
| 설계 | MVVM 기반 View·ViewModel·Service 분리 |
| Docker 연동 | Docker.DotNet, Docker CLI |
| YAML | YamlDotNet |
| 차트 | OxyPlot.Wpf |
| UI 동작 | Microsoft.Xaml.Behaviors.Wpf |
| 테스트 | xUnit |

## 빠른 시작

### 준비 사항

- Windows 10 또는 Windows 11
- .NET 10 SDK
- Docker Desktop 또는 접근 가능한 Docker Engine
- Compose 가져오기 후 실제 배포를 사용할 경우 Docker Compose 명령
- 원격 Docker 연결을 사용할 경우 Windows OpenSSH 클라이언트와 원격 Linux 호스트 접근 권한
- Kubernetes 기능을 사용할 경우 접근 가능한 클러스터와 올바른 현재 컨텍스트

### 실행

```powershell
dotnet restore
dotnet run --project DockerDiagram.csproj
```

### 테스트

```powershell
dotnet test Tests/DockerDiagram.Tests/DockerDiagram.Tests.csproj -c Release
```

## 문서 안내

- [프로젝트 종합설명서](docs/PROJECT_MANUAL.md)
- [면접 발표·질의응답 대본](docs/INTERVIEW_SCRIPT.md)
- [아키텍처와 프로젝트 구조](docs/ARCHITECTURE.md)
- [기능 설명서](docs/FEATURE_GUIDE.md)
- [사용자 안내서](docs/USER_GUIDE.md)
- [개발 및 확장 안내서](docs/DEVELOPMENT_GUIDE.md)

## 핵심 개념

DockerDiagram의 시트는 다음 세 종류의 시각 요소를 중심으로 구성된다.

| 요소 | 역할 |
|---|---|
| 노드 | 컨테이너, 볼륨, 외부 트래픽 또는 런타임 리소스를 표현한다. |
| 그룹 | 일반 묶음 또는 네트워크 영역을 표현한다. |
| 연결선 | 의존성, 볼륨 마운트, 네트워크 연결 등 관계의 의미를 보존한다. |

각 요소는 화면 모양과 실제 리소스 의미를 분리하여 보관한다. 따라서 같은 카드 형태를 재사용하면서도 리소스 종류, 생성 출처, 실제 엔진과의 연결 상태를 구분할 수 있다.
