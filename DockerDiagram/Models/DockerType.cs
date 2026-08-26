using DockerDiagram.Common;

namespace DockerDiagram.Models
{
    /// <summary>
    /// 도커 엔진에서 가져온 리소스(컨테이너, 볼륨, 네트워크 등)들의 공통 속성을 묶어둔 최상위 클래스입니다.
    /// UI에 바인딩되어 상태가 변할 때마다 화면을 갱신(ViewModelBase)합니다.
    /// </summary>
    public abstract class DockerResource : ViewModelBase
    {
        private string _id = string.Empty;
        public string Id { get => _id; set => SetProperty(ref _id, value); } // 리소스의 고유 식별자 (ID)

        private string _name = string.Empty;
        public string Name { get => _name; set => SetProperty(ref _name, value); } // 리소스의 이름

        private string _stateColor = "#FFFFFF";
        public string StateColor { get => _stateColor; set => SetProperty(ref _stateColor, value); } // 상태를 나타내는 UI 색상 (예: running=초록, exited=빨강)

        private string _composeProjectName = string.Empty;
        public string ComposeProjectName
        {
            get => _composeProjectName;
            set
            {
                if (SetProperty(ref _composeProjectName, value))
                    OnPropertyChanged(nameof(IsComposeManaged));
            }
        }

        public string ComposeResourceName { get; set; } = string.Empty;
        public string ComposeWorkingDirectory { get; set; } = string.Empty;
        public string ComposeConfigFiles { get; set; } = string.Empty;
        public string ProjectSource { get; set; } = string.Empty;
        public Dictionary<string, string> Labels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsComposeManaged => !string.IsNullOrWhiteSpace(ComposeProjectName);
    }

    /// <summary>
    /// 컨테이너, 볼륨, 인터넷 등 다이어그램의 '단일 노드'들을 아우르는 공통 부모입니다.
    /// MainViewModel 등에서 노드들을 한 번에 묶어서 처리할 때 유용하게 사용됩니다.
    /// </summary>
    public abstract class DockerNodeBase : DockerResource
    {
        // 상속받는 자식들은 반드시 자기만의 NodeType을 명시하도록 강제합니다.
        public abstract NodeType Type { get; }
    }

    /// <summary>
    /// 도커 컨테이너의 실시간 상태 정보를 담는 모델 클래스입니다.
    /// </summary>
    public class DockerContainer : DockerNodeBase // DockerResource 대신 DockerNodeBase 상속!
    {
        private string _image = string.Empty;
        public string Image { get => _image; set => SetProperty(ref _image, value); } // 컨테이너를 생성한 기반 이미지명

        private string _state = string.Empty;
        public string State { get => _state; set => SetProperty(ref _state, value); } // 현재 상태 (running, exited 등)

        private string _ports = string.Empty;
        public string Ports { get => _ports; set => SetProperty(ref _ports, value); } // 호스트와 연결된 포트 매핑 정보

        public string ComposeServiceName { get; set; } = string.Empty;
        public int ComposeContainerNumber { get; set; }
        public bool IsComposeOneOff { get; set; }
        public bool IsSwarmService { get; set; }
        public string SwarmMode { get; set; } = string.Empty;
        public ulong SwarmDesiredReplicas { get; set; }
        public ulong SwarmRunningReplicas { get; set; }
        public bool IsKubernetesPod { get; set; }
        public bool IsKubernetesResource => IsKubernetesPod || !string.IsNullOrWhiteSpace(KubernetesKind);
        public string KubernetesKind { get; set; } = string.Empty;
        public string KubernetesApiResource { get; set; } = string.Empty;
        public string KubernetesApiVersion { get; set; } = string.Empty;
        public string KubernetesNamespace { get; set; } = string.Empty;
        public string KubernetesNodeName { get; set; } = string.Empty;
        public string KubernetesReady { get; set; } = string.Empty;
        public int KubernetesRestarts { get; set; }
        public int KubernetesDesiredReplicas { get; set; }
        public int KubernetesReadyReplicas { get; set; }
        public string KubernetesPodIp { get; set; } = string.Empty;
        public string KubernetesRawJson { get; set; } = string.Empty;

        public override NodeType Type => NodeType.Container; // override로 구현
    }

    /// <summary>
    /// 도커 볼륨의 정보를 담는 모델 클래스입니다.
    /// </summary>
    public class DockerVolume : DockerNodeBase // DockerResource 대신 DockerNodeBase 상속!
    {
        public override NodeType Type => NodeType.Volume; // override로 구현
    }

    /// <summary>
    /// 외부 네트워크(인터넷) 연결을 시각적으로 표현하기 위한 가상 모델 클래스입니다.
    /// </summary>
    public class DockerInternet : DockerNodeBase // DockerResource 대신 DockerNodeBase 상속!
    {
        public override NodeType Type => NodeType.Internet; // override로 구현
    }

    /// <summary>
    /// 일반 폴더와 도커 네트워크를 모두 아우르는 '그룹'들의 공통 부모(추상) 클래스입니다.
    /// MainViewModel 등에서 두 그룹을 한 번에 묶어서 처리할 때(다형성) 유용하게 사용됩니다.
    /// </summary>
    public abstract class DockerGroupBase : DockerResource
    {
        // 상속받는 자식들은 반드시 자기만의 GroupType을 명시하도록 강제합니다.
        public abstract GroupType Type { get; }
    }

    /// <summary>
    /// 단순한 시각적 묶음을 위한 일반 폴더(그룹) 모델 클래스입니다.
    /// </summary>
    public class DockerGeneralGroup : DockerGroupBase // DockerResource 대신 DockerGroupBase 상속!
    {
        public override GroupType Type => GroupType.General; // override로 구현
    }

    /// <summary>
    /// 여러 컨테이너를 동일한 네트워크 대역으로 묶어주는 도커 네트워크 모델 클래스입니다.
    /// </summary>
    public class DockerNetworkGroup : DockerGroupBase // DockerResource 대신 DockerGroupBase 상속!
    {
        private string _driver = "bridge";
        public string Driver { get => _driver; set => SetProperty(ref _driver, value); }

        public override GroupType Type => GroupType.Network; // override로 구현
    }

    public sealed class DockerComposeProject
    {
        public string Name { get; init; } = string.Empty;
        public string WorkingDirectory { get; init; } = string.Empty;
        public string ConfigFiles { get; init; } = string.Empty;
        public string Source { get; init; } = "Compose";
        public List<DockerContainer> Containers { get; init; } = [];
        public List<DockerVolume> Volumes { get; init; } = [];
        public List<DockerNetworkGroup> Networks { get; init; } = [];
        public int ResourceCount => Containers.Count + Volumes.Count + Networks.Count;
        public string SourceLabel => string.IsNullOrWhiteSpace(Source) ? "Project" : Source;
        public string IdentityKey => CreateIdentityKey(Source, Name, WorkingDirectory, ConfigFiles);

        public static string CreateIdentityKey(
            string? source,
            string? name,
            string? workingDirectory,
            string? configFiles)
        {
            static string Normalize(string? value) =>
                (value ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/').ToUpperInvariant();

            string normalizedConfigs = string.Join(",",
                (configFiles ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Normalize)
                    .OrderBy(value => value, StringComparer.Ordinal));

            return string.Join("\u001F",
                Normalize(string.IsNullOrWhiteSpace(source) ? "Compose" : source),
                Normalize(name),
                Normalize(workingDirectory),
                normalizedConfigs);
        }
    }

    public sealed class DockerSwarmTask
    {
        public string Id { get; init; } = string.Empty;
        public ulong Slot { get; init; }
        public string NodeId { get; init; } = string.Empty;
        public string NodeName { get; init; } = "-";
        public string DesiredState { get; init; } = string.Empty;
        public string CurrentState { get; init; } = string.Empty;
        public string Image { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
        public string ContainerId { get; init; } = string.Empty;
        public string StatusColor { get; init; } = "#808080";
        public string ShortId => Id.Length > 12 ? Id[..12] : Id;
        public string ShortContainerId => ContainerId.Length > 12 ? ContainerId[..12] : ContainerId;
    }

    public sealed class DockerSwarmNode : DockerResource
    {
        private string _hostname = string.Empty;
        private string _role = string.Empty;
        private string _availability = string.Empty;
        private string _status = string.Empty;
        private string _address = string.Empty;
        private string _managerStatus = string.Empty;
        private string _engineVersion = string.Empty;

        public string Hostname { get => _hostname; set => SetProperty(ref _hostname, value); }
        public string Role
        {
            get => _role;
            set
            {
                if (SetProperty(ref _role, value)) OnPropertyChanged(nameof(RoleLabel));
            }
        }
        public string Availability { get => _availability; set => SetProperty(ref _availability, value); }
        public string Status { get => _status; set => SetProperty(ref _status, value); }
        public string Address { get => _address; set => SetProperty(ref _address, value); }
        public string ManagerStatus
        {
            get => _managerStatus;
            set
            {
                if (SetProperty(ref _managerStatus, value)) OnPropertyChanged(nameof(RoleLabel));
            }
        }
        public string EngineVersion { get => _engineVersion; set => SetProperty(ref _engineVersion, value); }
        public string RoleLabel => string.IsNullOrWhiteSpace(ManagerStatus) ? Role : $"{Role} / {ManagerStatus}";
    }

    public sealed class DockerKubernetesNode : DockerResource
    {
        private string _role = string.Empty;
        private string _status = string.Empty;
        private string _version = string.Empty;
        private string _internalIp = string.Empty;
        private string _osImage = string.Empty;

        public string Role
        {
            get => _role;
            set
            {
                if (SetProperty(ref _role, value)) OnPropertyChanged(nameof(RoleLabel));
            }
        }
        public string Status { get => _status; set => SetProperty(ref _status, value); }
        public string Version { get => _version; set => SetProperty(ref _version, value); }
        public string InternalIp { get => _internalIp; set => SetProperty(ref _internalIp, value); }
        public string OsImage { get => _osImage; set => SetProperty(ref _osImage, value); }
        public string RoleLabel => string.IsNullOrWhiteSpace(Role) ? "node" : Role;
    }
}
