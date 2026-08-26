using DockerDiagram.Common;

namespace DockerDiagram.Models
{
    /// <summary>
    /// 도커 엔진에 저장된 도커 이미지(Docker Image)의 정보를 담는 데이터 모델입니다.
    /// UI의 이미지 목록 등에 바인딩되어 데이터가 변경될 때마다 화면을 자동으로 갱신합니다.
    /// </summary>
    public class DockerImage : ViewModelBase
    {
        private string _id = string.Empty;
        public string Id { get => _id; set => SetProperty(ref _id, value); } // 이미지의 고유 식별자 (예: sha256:...)

        private string _repository = string.Empty;
        public string Repository { get => _repository; set => SetProperty(ref _repository, value); } // 이미지 저장소/이름 (예: nginx, mysql)

        private string _tag = string.Empty;
        public string Tag { get => _tag; set => SetProperty(ref _tag, value); } // 이미지의 버전 태그 (예: latest, 8.0)

        private long _size;
        public long Size
        {
            get => _size;
            set
            {
                // Size 값이 실제로 변경되었다면, FormattedSize(MB/GB 텍스트)의 화면 갱신 알림도 함께 보냅니다.
                if (SetProperty(ref _size, value))
                {
                    OnPropertyChanged(nameof(FormattedSize));
                }
            }
        } // 이미지의 실제 크기 (Byte 단위)

        public string FormattedSize // Byte 단위의 크기를 사람이 읽기 쉬운 MB 또는 GB 단위의 문자열로 변환하여 반환합니다. (읽기 전용)
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