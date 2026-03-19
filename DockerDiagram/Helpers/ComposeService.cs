using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DockerDiagram.Helpers
{
    public class ComposeService
    {
        public string? ContainerName { get; set; }
        public string? Image { get; set; }
        public string? Restart { get; set; }
        public List<string>? Ports { get; set; }
        public List<string>? Environment { get; set; }
        public List<string>? Volumes { get; set; }
        public List<string>? DependsOn { get; set; }

        // 정적 IP가 있으면 딕셔너리, 없으면 단순 문자열 리스트로 처리하기 위해 object 타입 사용
        public object? Networks { get; set; }
    }

    public class ComposeServiceNetwork
    {
        public string? Ipv4Address { get; set; }
    }

    public class ComposeNetwork
    {
        public string? Driver { get; set; }
        public ComposeIpam? Ipam { get; set; }
    }

    public class ComposeIpam
    {
        public List<ComposeIpamConfig>? Config { get; set; }
    }

    public class ComposeIpamConfig
    {
        public string? Subnet { get; set; }
    }
}
