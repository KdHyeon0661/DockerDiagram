using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerDiagram.Helpers
{
    public sealed class ComposeProjectPlacementResult
    {
        public int ContainerCount { get; init; }
        public int VolumeCount { get; init; }
        public int NetworkCount { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    public sealed class ComposeProjectPlacementService
    {
        private readonly IDockerService _dockerService;
        private readonly IDialogService _dialogService;

        public ComposeProjectPlacementService(IDockerService dockerService, IDialogService dialogService)
        {
            _dockerService = dockerService;
            _dialogService = dialogService;
        }

        public async Task<ComposeProjectPlacementResult> PlaceAsync(
            SheetViewModel sheet,
            DockerComposeProject project,
            double originX,
            double originY)
        {
            var warnings = new List<string>();
            string layoutInstanceId = Guid.NewGuid().ToString("N");
            var layoutNodes = new Dictionary<string, NodeViewModel>(StringComparer.OrdinalIgnoreCase);
            var containerEntries = new List<(DockerContainer Resource, NodeViewModel Node, string LayoutKey)>();
            var volumeNodes = new Dictionary<string, NodeViewModel>(StringComparer.OrdinalIgnoreCase);
            var networkGroups = new Dictionary<string, GroupViewModel>(StringComparer.OrdinalIgnoreCase);

            foreach (DockerVolume volume in project.Volumes.OrderBy(volume => volume.Name, StringComparer.OrdinalIgnoreCase))
            {
                NodeViewModel node = CreateVolumeNode(sheet, volume.Name, volume.Id, originX, originY);
                node.ComposeLayoutInstanceId = layoutInstanceId;
                volumeNodes[volume.Name] = node;
            }

            foreach (DockerNetworkGroup network in project.Networks.OrderBy(network => network.Name, StringComparer.OrdinalIgnoreCase))
            {
                string title = string.IsNullOrWhiteSpace(network.ComposeResourceName)
                    ? network.Name
                    : network.ComposeResourceName;
                GroupViewModel group = CreateNetworkGroup(sheet, title, network.Name, network.Id, network.Driver, originX, originY);
                group.ComposeLayoutInstanceId = layoutInstanceId;
                networkGroups[network.Name] = group;
            }

            foreach (DockerContainer container in project.Containers
                         .OrderBy(container => container.ComposeServiceName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(container => container.ComposeContainerNumber)
                         .ThenBy(container => container.Name, StringComparer.OrdinalIgnoreCase))
            {
                sheet.CreateNodeAt(container, originX, originY);
                NodeViewModel node = sheet.Nodes[^1];
                node.ComposeLayoutInstanceId = layoutInstanceId;
                string layoutKey = CreateLayoutKey(container, layoutNodes);
                layoutNodes[layoutKey] = node;
                containerEntries.Add((container, node, layoutKey));
            }

            var representativeByService = containerEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Resource.ComposeServiceName))
                .GroupBy(entry => entry.Resource.ComposeServiceName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(entry => entry.Resource.ComposeContainerNumber).First().LayoutKey,
                    StringComparer.OrdinalIgnoreCase);
            var dependencyMap = layoutNodes.Keys.ToDictionary(
                key => key,
                _ => new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in containerEntries)
            {
                foreach (string dependencyService in ParseDependsOn(entry.Resource.Labels))
                {
                    if (!representativeByService.TryGetValue(dependencyService, out string? dependencyKey)) continue;
                    dependencyMap[entry.LayoutKey].Add(dependencyKey);

                    NodeViewModel dependencyNode = layoutNodes[dependencyKey];
                    AddConnectorIfMissing(
                        sheet,
                        dependencyNode,
                        entry.Node,
                        RelationType.Dependency,
                        string.Empty);
                }

                try
                {
                    var inspect = await _dockerService.InspectContainerAsync(entry.Resource.Id);

                    if (inspect.NetworkSettings?.Networks != null)
                    {
                        foreach (var networkEntry in inspect.NetworkSettings.Networks)
                        {
                            string networkName = networkEntry.Key;
                            if (IsBuiltInNetwork(networkName)) continue;

                            if (!networkGroups.TryGetValue(networkName, out GroupViewModel? group))
                            {
                                string networkId = networkEntry.Value?.NetworkID ?? string.Empty;
                                group = CreateNetworkGroup(sheet, networkName, networkName, networkId, "bridge", originX, originY);
                                group.ComposeLayoutInstanceId = layoutInstanceId;
                                networkGroups[networkName] = group;
                            }

                            await group.AddNodeAsync(entry.Node, isRestoring: true);
                        }
                    }

                    if (inspect.Mounts != null)
                    {
                        foreach (var mount in inspect.Mounts.Where(mount => string.Equals(mount.Type, "volume", StringComparison.OrdinalIgnoreCase)))
                        {
                            string volumeName = mount.Name ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(volumeName)) continue;

                            if (!volumeNodes.TryGetValue(volumeName, out NodeViewModel? volumeNode))
                            {
                                volumeNode = CreateVolumeNode(sheet, volumeName, volumeName, originX, originY);
                                volumeNode.ComposeLayoutInstanceId = layoutInstanceId;
                                volumeNodes[volumeName] = volumeNode;
                            }

                            AddConnectorIfMissing(
                                sheet,
                                entry.Node,
                                volumeNode,
                                RelationType.VolumeMount,
                                mount.Destination ?? string.Empty);
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"{entry.Resource.Name}: {ex.Message}");
                }
            }

            ComposeDiagramLayoutService.Arrange(
                sheet,
                layoutNodes,
                dependencyMap,
                originX,
                originY,
                volumeNodes.Values.ToList(),
                networkGroups.Values.ToList());

            return new ComposeProjectPlacementResult
            {
                ContainerCount = containerEntries.Count,
                VolumeCount = volumeNodes.Count,
                NetworkCount = networkGroups.Count,
                Warnings = warnings
            };
        }

        private NodeViewModel CreateVolumeNode(
            SheetViewModel sheet,
            string volumeName,
            string volumeId,
            double x,
            double y)
        {
            sheet.CreateNodeAt(new DockerVolume
            {
                Id = volumeId,
                Name = volumeName
            }, x, y);
            return sheet.Nodes[^1];
        }

        private GroupViewModel CreateNetworkGroup(
            SheetViewModel sheet,
            string title,
            string dockerNetworkName,
            string networkId,
            string driver,
            double x,
            double y)
        {
            var group = new GroupViewModel(x, y, 220, 150, _dockerService, _dialogService, title, GroupType.Network)
            {
                Id = networkId,
                Driver = string.IsNullOrWhiteSpace(driver) ? "bridge" : driver,
                ComposeNetworkName = dockerNetworkName,
                IsDockerConnected = true
            };
            sheet.AddGroup(group);
            return group;
        }

        private void AddConnectorIfMissing(
            SheetViewModel sheet,
            NodeViewModel source,
            NodeViewModel target,
            RelationType relationType,
            string mountPath)
        {
            if (sheet.Connectors.Any(connector =>
                    connector.RelationType == relationType &&
                    ReferenceEquals(connector.Source, source) &&
                    ReferenceEquals(connector.Target, target)))
            {
                return;
            }

            sheet.Connectors.Add(new ConnectorViewModel(source, target, PortDirection.Right, PortDirection.Left, _dialogService)
            {
                RelationType = relationType,
                MountPath = mountPath
            });
        }

        private static string CreateLayoutKey(
            DockerContainer container,
            IReadOnlyDictionary<string, NodeViewModel> existing)
        {
            string service = string.IsNullOrWhiteSpace(container.ComposeServiceName)
                ? container.Name
                : container.ComposeServiceName;
            string suffix = container.ComposeContainerNumber > 0
                ? container.ComposeContainerNumber.ToString()
                : container.Id[..Math.Min(12, container.Id.Length)];
            string key = $"{service}#{suffix}";
            int duplicate = 2;
            while (existing.ContainsKey(key)) key = $"{service}#{suffix}-{duplicate++}";
            return key;
        }

        private static IReadOnlyList<string> ParseDependsOn(IReadOnlyDictionary<string, string> labels)
        {
            if (!labels.TryGetValue("com.docker.compose.depends_on", out string? value) ||
                string.IsNullOrWhiteSpace(value))
            {
                labels.TryGetValue("com.dockerdiagram.depends_on", out value);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.Split(':', 2, StringSplitOptions.TrimEntries)[0])
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsBuiltInNetwork(string networkName) =>
            string.Equals(networkName, "bridge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(networkName, "host", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(networkName, "none", StringComparison.OrdinalIgnoreCase);
    }
}
