using DockerDiagram.Helpers;

namespace DockerDiagram.Models
{
    public class DockerContainer : ViewModelBase // 도커 컨테이너 정보(id, 이름, 이미지, 상태, 포트)와 색상 정보를 담는 클래스
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

        private string _stateColor = "#FFFFFF";
        public string StateColor
        {
            get => _stateColor;
            set { if (_stateColor != value) { _stateColor = value; OnPropertyChanged(); } }
        }

        private NodeType _type;
        public NodeType Type
        {
            get => _type;
            set { if (_type != value) { _type = value; OnPropertyChanged(); } }
        }
    }
}