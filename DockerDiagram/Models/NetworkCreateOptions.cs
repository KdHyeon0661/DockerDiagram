using System.Collections.Generic;

namespace DockerDiagram.Models
{
    public class NetworkCreateOptions
    {
        public string Name { get; set; } = "";
        public string Driver { get; set; } = "bridge";
        public string Subnet { get; set; } = "";
        public string Gateway { get; set; } = "";
        public string IpRange { get; set; } = "";
        public bool Internal { get; set; }
        public bool Attachable { get; set; }
        public bool EnableIPv6 { get; set; }
        public bool External { get; set; }
        public string ComposeNetworkName { get; set; } = "";
        public string ComposeRawNetworkYaml { get; set; } = "";
        public Dictionary<string, string> Labels { get; set; } = [];
        public Dictionary<string, string> DriverOptions { get; set; } = [];
        public Dictionary<string, string> AuxAddresses { get; set; } = [];

        public bool HasIpam => !string.IsNullOrWhiteSpace(Subnet) ||
                               !string.IsNullOrWhiteSpace(Gateway) ||
                               !string.IsNullOrWhiteSpace(IpRange) ||
                               AuxAddresses.Count > 0;

        public static NetworkCreateOptions Basic(string name, string driver)
        {
            return new NetworkCreateOptions
            {
                Name = name,
                Driver = string.IsNullOrWhiteSpace(driver) ? "bridge" : driver
            };
        }
    }
}
