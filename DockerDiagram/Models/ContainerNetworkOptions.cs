using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Models
{
    public class ContainerNetworkOptions
    {
        public string StaticIPv4 { get; set; } = "";
        public string StaticIPv6 { get; set; } = "";
        public List<string> Aliases { get; set; } = new();
        public Dictionary<string, string> DriverOptions { get; set; } = new();

        public bool HasAnyOption =>
            !string.IsNullOrWhiteSpace(StaticIPv4) ||
            !string.IsNullOrWhiteSpace(StaticIPv6) ||
            Aliases.Any(alias => !string.IsNullOrWhiteSpace(alias)) ||
            DriverOptions.Count > 0;

        public ContainerNetworkOptions Clone()
        {
            return new ContainerNetworkOptions
            {
                StaticIPv4 = StaticIPv4,
                StaticIPv6 = StaticIPv6,
                Aliases = Aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).Select(alias => alias.Trim()).ToList(),
                DriverOptions = new Dictionary<string, string>(DriverOptions)
            };
        }
    }
}
