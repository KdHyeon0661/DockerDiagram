using DockerDiagram.Helpers;

namespace DockerDiagram.Models
{
    public class DockerImage : ViewModelBase // 도커 이미지 정보를 담는 클래스
    {
        private string _id = string.Empty;
        public string Id
        {
            get => _id;
            set { if (_id != value) { _id = value; OnPropertyChanged(); } }
        }

        private string _repository = string.Empty;
        public string Repository
        {
            get => _repository;
            set { if (_repository != value) { _repository = value; OnPropertyChanged(); } }
        }

        private string _tag = string.Empty;
        public string Tag
        {
            get => _tag;
            set { if (_tag != value) { _tag = value; OnPropertyChanged(); } }
        }

        private long _size;
        public long Size
        {
            get => _size;
            set
            {
                if (_size != value)
                {
                    _size = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FormattedSize));
                }
            }
        }
        public string FormattedSize
        {
            get
            {
                if (Size <= 0) return "0 B";

                double sizeMb = Size / 1024.0 / 1024.0;

                if (sizeMb >= 1024) // 1GB 이상일 때
                {
                    double sizeGb = sizeMb / 1024.0;
                    return $"{sizeGb:F2} GB"; // 예: 1.25 GB
                }
                else
                {
                    return $"{sizeMb:F1} MB"; // 예: 350.5 MB
                }
            }
        }
    }
}