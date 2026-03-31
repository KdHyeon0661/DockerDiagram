namespace DockerDiagram.Models
{
    /// <summary>
    /// 실행 중인 도커 컨테이너의 실시간 자원 사용량(CPU, 메모리 등) 정보를 담는 데이터 객체입니다.
    /// UI에서 컨테이너의 현재 상태나 부하 정도를 시각적으로 보여줄 때 사용됩니다.
    /// </summary>
    public class ContainerStats
    {
        public double CpuPercentage { get; set; } // 현재 CPU 사용률 (%)
        public double MemoryUsedMB { get; set; }  // 현재 사용 중인 메모리 양 (MB 단위)
        public double MemoryLimitMB { get; set; } // 컨테이너에 할당된 최대 메모리 한도 (MB 단위)
    }
}