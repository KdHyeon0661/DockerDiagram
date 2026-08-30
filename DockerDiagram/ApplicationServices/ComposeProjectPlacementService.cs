using DockerDiagram.Diagram;
using DockerDiagram.Contracts;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DockerDiagram.ApplicationServices
{
    public sealed class ComposeProjectPlacementResult
    {
        public int ContainerCount { get; init; }
        public int VolumeCount { get; init; }
        public int NetworkCount { get; init; }
        public IReadOnlyList<NodeViewModel> Nodes { get; init; } = Array.Empty<NodeViewModel>();
        public IReadOnlyList<GroupViewModel> Groups { get; init; } = Array.Empty<GroupViewModel>();
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
            string projectIdentity = project.IdentityKey;
            string layoutInstanceId = Guid.NewGuid().ToString("N");
            var layoutNodes = new Dictionary<string, NodeViewModel>(StringComparer.OrdinalIgnoreCase);
            var containerEntries = new List<(DockerContainer Resource, NodeViewModel Node, string LayoutKey)>();
            var volumeNodes = new Dictionary<string, NodeViewModel>(StringComparer.OrdinalIgnoreCase);
            var networkGroups = new Dictionary<string, GroupViewModel>(StringComparer.OrdinalIgnoreCase);

            foreach (DockerVolume volume in project.Volumes.OrderBy(volume => volume.Name, StringComparer.OrdinalIgnoreCase))
            {
                NodeViewModel? existingNode = sheet.Nodes.FirstOrDefault(node =>
                    node.Type == NodeType.Volume &&
                    string.Equals(node.EffectiveVolumeName, volume.Name, StringComparison.OrdinalIgnoreCase));
                NodeViewModel node = existingNode ?? CreateVolumeNode(sheet, volume.Name, volume.Id, originX, originY);
                node.ComposeProjectName = project.Name;
                node.ComposeProjectIdentity = projectIdentity;
                node.ComposeLayoutInstanceId = layoutInstanceId;
                volumeNodes[volume.Name] = node;
            }

            foreach (DockerNetworkGroup network in project.Networks.OrderBy(network => network.Name, StringComparer.OrdinalIgnoreCase))
            {
                string title = string.IsNullOrWhiteSpace(network.ComposeResourceName)
                    ? network.Name
                    : network.ComposeResourceName;
                GroupViewModel? existingGroup = sheet.Groups.FirstOrDefault(group =>
                    group.Type == GroupType.Network &&
                    ((!string.IsNullOrWhiteSpace(network.Id) && string.Equals(group.Id, network.Id, StringComparison.OrdinalIgnoreCase)) ||
                     string.Equals(group.DockerNetworkName, network.Name, StringComparison.OrdinalIgnoreCase)));
                GroupViewModel group = existingGroup ??
                    CreateNetworkGroup(sheet, title, network.Name, network.Id, network.Driver, originX, originY);
                group.ComposeProjectIdentity = projectIdentity;
                group.ComposeLayoutInstanceId = layoutInstanceId;
                networkGroups[network.Name] = group;
            }

            foreach (DockerContainer container in project.Containers
                         .OrderBy(container => container.ComposeServiceName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(container => container.ComposeContainerNumber)
                         .ThenBy(container => container.Name, StringComparer.OrdinalIgnoreCase))
            {
                NodeViewModel? existingNode = sheet.Nodes.FirstOrDefault(node =>
                    node.Type == NodeType.Container &&
                    !string.IsNullOrWhiteSpace(container.Id) &&
                    string.Equals(node.ContainerId, container.Id, StringComparison.OrdinalIgnoreCase));
                if (existingNode == null)
                {
                    sheet.CreateNodeAt(container, originX, originY);
                    existingNode = sheet.Nodes[^1];
                }

                NodeViewModel node = existingNode;
                node.ComposeProjectName = project.Name;
                node.ComposeProjectIdentity = projectIdentity;
                node.ComposeServiceName = container.ComposeServiceName;
                node.ComposeContainerNumber = container.ComposeContainerNumber;
                node.ComposeLayoutInstanceId = layoutInstanceId;
                node.ComposePlacementWarning = string.Empty;
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
                    sheet.TryAddDirectedConnection(
                        entry.Node,
                        dependencyNode,
                        RelationType.Dependency,
                        mountPath: string.Empty,
                        allowSelfConnection: true);
                }
            }

            using var inspectLimiter = new SemaphoreSlim(4, 4);
            var inspectTasks = containerEntries.Select(async entry =>
            {
                await inspectLimiter.WaitAsync();
                try
                {
                    ContainerInspectResponse inspect = await _dockerService
                        .InspectContainerAsync(entry.Resource.Id)
                        .WaitAsync(TimeSpan.FromSeconds(20));
                    return (Entry: entry, Inspect: inspect, Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Entry: entry, Inspect: (ContainerInspectResponse?)null, Error: ex);
                }
                finally
                {
                    inspectLimiter.Release();
                }
            }).ToList();

            var inspectResults = await Task.WhenAll(inspectTasks);
            foreach (var result in inspectResults)
            {
                var entry = result.Entry;
                if (result.Error != null || result.Inspect == null)
                {
                    string message = result.Error?.Message ?? "컨테이너 상세 정보를 가져오지 못했습니다.";
                    entry.Node.ComposePlacementWarning = message;
                    warnings.Add($"{entry.Resource.Name}: {message}");
                    continue;
                }

                ContainerInspectResponse inspect = result.Inspect;
                if (inspect.NetworkSettings?.Networks != null)
                {
                    foreach (var networkEntry in inspect.NetworkSettings.Networks)
                    {
                        string networkName = networkEntry.Key;
                        if (IsBuiltInNetwork(networkName)) continue;

                        if (!networkGroups.TryGetValue(networkName, out GroupViewModel? group))
                        {
                            string networkId = networkEntry.Value?.NetworkID ?? string.Empty;
                            group = sheet.Groups.FirstOrDefault(candidate =>
                                candidate.Type == GroupType.Network &&
                                ((!string.IsNullOrWhiteSpace(networkId) && string.Equals(candidate.Id, networkId, StringComparison.OrdinalIgnoreCase)) ||
                                 string.Equals(candidate.DockerNetworkName, networkName, StringComparison.OrdinalIgnoreCase)));
                            group ??= CreateNetworkGroup(sheet, networkName, networkName, networkId, "bridge", originX, originY);
                            group.ComposeProjectIdentity = projectIdentity;
                            group.ComposeLayoutInstanceId = layoutInstanceId;
                            networkGroups[networkName] = group;
                        }

                        await group.AddNodeAsync(entry.Node, isRestoring: true);
                    }
                }

                if (inspect.Mounts == null) continue;
                foreach (var mount in inspect.Mounts.Where(mount => string.Equals(mount.Type, "volume", StringComparison.OrdinalIgnoreCase)))
                {
                    string volumeName = mount.Name ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(volumeName)) continue;

                    if (!volumeNodes.TryGetValue(volumeName, out NodeViewModel? volumeNode))
                    {
                        volumeNode = sheet.Nodes.FirstOrDefault(node =>
                            node.Type == NodeType.Volume &&
                            string.Equals(node.EffectiveVolumeName, volumeName, StringComparison.OrdinalIgnoreCase));
                        volumeNode ??= CreateVolumeNode(sheet, volumeName, volumeName, originX, originY);
                        volumeNode.ComposeProjectName = project.Name;
                        volumeNode.ComposeProjectIdentity = projectIdentity;
                        volumeNode.ComposeLayoutInstanceId = layoutInstanceId;
                        volumeNodes[volumeName] = volumeNode;
                    }

                    sheet.TryAddDirectedConnection(
                        entry.Node,
                        volumeNode,
                        RelationType.VolumeMount,
                        mountPath: mount.Destination ?? string.Empty,
                        allowSelfConnection: true);
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
                Nodes = layoutNodes.Values
                    .Concat(volumeNodes.Values)
                    .Distinct<NodeViewModel>(ReferenceEqualityComparer.Instance)
                    .ToList(),
                Groups = networkGroups.Values
                    .Distinct<GroupViewModel>(ReferenceEqualityComparer.Instance)
                    .ToList(),
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
