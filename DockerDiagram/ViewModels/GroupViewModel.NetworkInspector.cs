using DockerDiagram.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace DockerDiagram.ViewModels
{
    public partial class GroupViewModel
    {
        private bool _isNetworkDetailsLoading;
        private string _networkDetailsStatus = string.Empty;
        private string _networkScope = "-";
        private string _networkSubnetSummary = "-";
        private string _networkGatewaySummary = "-";
        private bool _networkIngress;

        public ObservableCollection<NetworkEndpointSummary> NetworkConnectedContainers { get; } = new();

        public bool IsBuiltInDockerNetwork =>
            Type == GroupType.Network &&
            (string.Equals(DockerNetworkName, "bridge", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(DockerNetworkName, "host", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(DockerNetworkName, "none", StringComparison.OrdinalIgnoreCase));

        public string NetworkScope
        {
            get => _networkScope;
            private set => SetProperty(ref _networkScope, string.IsNullOrWhiteSpace(value) ? "-" : value);
        }

        public string NetworkSubnetSummary
        {
            get => _networkSubnetSummary;
            private set => SetProperty(ref _networkSubnetSummary, string.IsNullOrWhiteSpace(value) ? "-" : value);
        }

        public string NetworkGatewaySummary
        {
            get => _networkGatewaySummary;
            private set => SetProperty(ref _networkGatewaySummary, string.IsNullOrWhiteSpace(value) ? "-" : value);
        }

        public bool NetworkIngress
        {
            get => _networkIngress;
            private set => SetProperty(ref _networkIngress, value);
        }

        public string NetworkIPv6Text => EnableIPv6 ? "Enabled" : "Disabled";
        public string NetworkInternalText => Internal ? "Yes" : "No";
        public string NetworkAttachableText => Attachable ? "Yes" : "No";

        public bool IsNetworkDetailsLoading
        {
            get => _isNetworkDetailsLoading;
            private set => SetProperty(ref _isNetworkDetailsLoading, value);
        }

        public string NetworkDetailsStatus
        {
            get => _networkDetailsStatus;
            private set
            {
                if (SetProperty(ref _networkDetailsStatus, value))
                    OnPropertyChanged(nameof(HasNetworkDetailsStatus));
            }
        }

        public bool HasNetworkDetailsStatus => !string.IsNullOrWhiteSpace(NetworkDetailsStatus);
        public bool HasNetworkConnectedContainers => NetworkConnectedContainers.Count > 0;
        public int NetworkConnectedContainerCount => NetworkConnectedContainers.Count;

        public async Task RefreshNetworkDetailsAsync()
        {
            if (Type != GroupType.Network || !IsDockerConnected || IsNetworkDetailsLoading)
                return;

            string inspectKey = !string.IsNullOrWhiteSpace(Id) && !Guid.TryParse(Id, out _)
                ? Id
                : DockerNetworkName;
            if (string.IsNullOrWhiteSpace(inspectKey))
                return;

            IsNetworkDetailsLoading = true;
            NetworkDetailsStatus = string.Empty;

            try
            {
                var network = await _networkService.InspectNetworkAsync(inspectKey);

                if (!string.IsNullOrWhiteSpace(network.ID))
                    Id = network.ID;
                if (!string.IsNullOrWhiteSpace(network.Driver))
                    Driver = network.Driver;

                NetworkScope = network.Scope;
                Internal = network.Internal == true;
                Attachable = network.Attachable == true;
                EnableIPv6 = network.EnableIPv6 == true;
                NetworkIngress = network.Ingress == true;

                var ipamConfigs = network.IPAM?.Config?.ToList();
                NetworkSubnetSummary = BuildCompactValue(ipamConfigs?.Select(config => config.Subnet));
                NetworkGatewaySummary = BuildCompactValue(ipamConfigs?.Select(config => config.Gateway));

                OnPropertyChanged(nameof(Driver));
                OnPropertyChanged(nameof(NetworkIPv6Text));
                OnPropertyChanged(nameof(NetworkInternalText));
                OnPropertyChanged(nameof(NetworkAttachableText));
                OnPropertyChanged(nameof(IsBuiltInDockerNetwork));

                NetworkConnectedContainers.Clear();
                if (network.Containers != null)
                {
                    foreach (var endpoint in network.Containers
                                 .OrderBy(item => item.Value?.Name ?? item.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        string containerId = endpoint.Key ?? string.Empty;
                        string containerName = string.IsNullOrWhiteSpace(endpoint.Value?.Name)
                            ? ShortId(containerId)
                            : endpoint.Value.Name;
                        var diagramNode = FindContainerNode(containerId, containerName);

                        NetworkConnectedContainers.Add(new NetworkEndpointSummary(
                            containerName,
                            NormalizeEndpointAddress(endpoint.Value?.IPv4Address),
                            NormalizeEndpointAddress(endpoint.Value?.IPv6Address),
                            NormalizeContainerStatus(diagramNode?.DetailStatus)));
                    }
                }

                OnPropertyChanged(nameof(HasNetworkConnectedContainers));
                OnPropertyChanged(nameof(NetworkConnectedContainerCount));
            }
            catch (Exception ex)
            {
                NetworkDetailsStatus = $"Unable to refresh network details: {ex.Message}";
            }
            finally
            {
                IsNetworkDetailsLoading = false;
            }
        }

        private NodeViewModel? FindContainerNode(string containerId, string containerName)
        {
            var candidates = ParentSheet?.Nodes
                .Where(node => node.Type == NodeType.Container)
                .ToList() ?? ContainedNodes.Where(node => node.Type == NodeType.Container).ToList();

            return candidates.FirstOrDefault(node => SameDockerId(node.ContainerId, containerId))
                   ?? candidates.FirstOrDefault(node =>
                       string.Equals(node.Name, containerName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool SameDockerId(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
            return first.StartsWith(second, StringComparison.OrdinalIgnoreCase) ||
                   second.StartsWith(first, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildCompactValue(IEnumerable<string?>? values)
        {
            var distinctValues = values?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            return distinctValues.Count switch
            {
                0 => "-",
                1 => distinctValues[0],
                _ => $"{distinctValues[0]}  +{distinctValues.Count - 1}"
            };
        }

        private static string NormalizeEndpointAddress(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "-";
            int slashIndex = value.IndexOf('/');
            return slashIndex > 0 ? value[..slashIndex] : value;
        }

        private static string NormalizeContainerStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "unknown";
            if (status.Contains("running", StringComparison.OrdinalIgnoreCase)) return "running";
            if (status.Contains("exited", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("stopped", StringComparison.OrdinalIgnoreCase)) return "exited";
            return "unknown";
        }

        private static string ShortId(string id) =>
            string.IsNullOrWhiteSpace(id) ? "Unknown container" : id[..Math.Min(12, id.Length)];

        public sealed class NetworkEndpointSummary
        {
            public NetworkEndpointSummary(string name, string ipv4, string ipv6, string status)
            {
                Name = name;
                IPv4 = ipv4;
                IPv6 = ipv6;
                Status = status;
            }

            public string Name { get; }
            public string IPv4 { get; }
            public string IPv6 { get; }
            public string Status { get; }
            public string AddressSummary => $"IPv4 {IPv4}   ·   IPv6 {IPv6}";
        }
    }
}