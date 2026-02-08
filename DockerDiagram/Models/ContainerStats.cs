namespace DockerDiagram.Models
{
    public class ContainerStats
    {
        public double CpuPercentage { get; set; }
        public double MemoryUsedMB { get; set; }
        public double MemoryLimitMB { get; set; }
    }
}