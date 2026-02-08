using DockerDiagram.Helpers;

namespace DockerDiagram.Models
{
    // 1. 공통 베이스 클래스 (Id, Name, StateColor)
    public abstract class DockerResource : ViewModelBase
    {
        private string _id = string.Empty;
        public string Id
        {
            get => _id;
            set { if (_id != value) { _id = value; OnPropertyChanged(); } }
        }

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        private string _stateColor = "#FFFFFF";
        public string StateColor
        {
            get => _stateColor;
            set { if (_stateColor != value) { _stateColor = value; OnPropertyChanged(); } }
        }
    }

    // 컨테이너 클래스 (Image, State, Ports 포함 + NodeType)
    public class DockerContainer : DockerResource
    {
        private string _image = string.Empty;
        public string Image
        {
            get => _image;
            set { if (_image != value) { _image = value; OnPropertyChanged(); } }
        }

        private string _state = string.Empty;
        public string State
        {
            get => _state;
            set { if (_state != value) { _state = value; OnPropertyChanged(); } }
        }

        private string _ports = string.Empty;
        public string Ports
        {
            get => _ports;
            set { if (_ports != value) { _ports = value; OnPropertyChanged(); } }
        }

        // 컨테이너도 노드의 일종이므로 Type을 가짐
        private NodeType _type = NodeType.Container;
        public NodeType Type
        {
            get => _type;
            private set { if (_type != value) { _type = value; OnPropertyChanged(); } }
        }
    }

    // 볼륨 클래스 (불필요한 속성 제거 + NodeType만 유지)
    public class DockerVolume : DockerResource
    {
        private NodeType _type = NodeType.Volume;

        public NodeType Type
        {
            get => _type;
            private set { if (_type != value) { _type = value; OnPropertyChanged(); } }
        }
    }

    public class DockerInternet : DockerResource
    {
        private NodeType _type = NodeType.Internet;

        public NodeType Type
        {
            get => _type;
            private set { if (_type != value) { _type = value; OnPropertyChanged(); } }
        }
    }

    // 3. 그룹 클래스 (GroupType 사용 - 2가지 타입 대응)
    public class DockerGroup : DockerResource
    {
        private string _driver = string.Empty;
        public string Driver
        {
            get => _driver;
            set { if (_driver != value) { _driver = value; OnPropertyChanged(); } }
        }

        private GroupType _type;
        public GroupType Type
        {
            get => _type;
            set { if (_type != value) { _type = value; OnPropertyChanged(); } }
        }
    }
}