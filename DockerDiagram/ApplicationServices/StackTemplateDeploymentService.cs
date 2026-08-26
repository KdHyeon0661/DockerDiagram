using DockerDiagram.Infrastructure;
using DockerDiagram.Diagram;
using DockerDiagram.Contracts;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.ApplicationServices
{
    public sealed class StackTemplateApplication
    {
        public List<NodeViewModel> Nodes { get; } = [];
        public List<GroupViewModel> Groups { get; } = [];
        public List<ConnectorViewModel> Connectors { get; } = [];
        public List<string> ContainerIds { get; } = [];
        public List<string> VolumeNames { get; } = [];
        public List<string> NetworkIds { get; } = [];
    }

    public sealed class StackTemplateDeploymentService
    {
        private static readonly Regex PlaceholderRegex =
            new(@"\$\{([A-Z0-9_]+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IDockerService _dockerService;
        private readonly IDialogService _dialogService;

        public StackTemplateDeploymentService(IDockerService dockerService, IDialogService dialogService)
        {
            _dockerService = dockerService;
            _dialogService = dialogService;
        }

        public static async Task<string> SuggestProjectNameAsync(
            StackTemplateDefinition template,
            SheetViewModel sheet,
            IDockerService dockerService)
        {
            string baseName = NormalizeProjectName(template.DefaultProjectName);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "stack";

            var diagramContainerNames = sheet.Nodes
                .Where(node => node.Type == NodeType.Container)
                .Select(node => node.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var diagramVolumeNames = sheet.Nodes
                .Where(node => node.Type == NodeType.Volume)
                .Select(node => node.EffectiveVolumeName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var diagramNetworkNames = sheet.Groups
                .Where(group => group.Type == GroupType.Network)
                .Select(group => group.DockerNetworkName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var dockerContainerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dockerVolumeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dockerNetworkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var containers = await dockerService.GetContainersAsync();
                foreach (var container in containers)
                {
                    if (!string.IsNullOrWhiteSpace(container.Name))
                        dockerContainerNames.Add(container.Name.TrimStart('/'));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StackTemplate] Container name scan failed: {ex.Message}");
            }

            try
            {
                var volumes = await dockerService.GetVolumesAsync();
                foreach (var volume in volumes)
                {
                    if (!string.IsNullOrWhiteSpace(volume.Name))
                        dockerVolumeNames.Add(volume.Name);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StackTemplate] Volume name scan failed: {ex.Message}");
            }

            try
            {
                var networks = await dockerService.GetNetworksAsync();
                foreach (var network in networks)
                {
                    if (!string.IsNullOrWhiteSpace(network.Name))
                        dockerNetworkNames.Add(network.Name);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StackTemplate] Network name scan failed: {ex.Message}");
            }

            for (int i = 1; i <= 999; i++)
            {
                string candidate = i == 1 ? baseName : $"{baseName}-{i}";
                if (!HasNameConflict(
                        template,
                        candidate,
                        diagramContainerNames,
                        diagramVolumeNames,
                        diagramNetworkNames,
                        dockerContainerNames,
                        dockerVolumeNames,
                        dockerNetworkNames))
                {
                    return candidate;
                }
            }

            return $"{baseName}-{DateTimeOffset.Now:yyyyMMddHHmmss}";
        }

        public async Task<StackTemplateApplication> ApplyAsync(
            StackTemplateDefinition template,
            StackTemplateDeploymentOptions options,
            SheetViewModel sheet,
            double originX,
            double originY)
        {
            var application = new StackTemplateApplication();
            var values = BuildValues(template, options);
            var networkDefinitions = template.Networks.Where(item => IsEnabled(item.Key, string.Empty, values)).ToList();
            var volumeDefinitions = template.Volumes.Where(item => IsEnabled(item.Key, item.EnabledWhen, values)).ToList();
            var containerDefinitions = template.Containers.Where(item => IsEnabled(item.Key, item.EnabledWhen, values)).ToList();
            var sensitiveVariableKeys = GetSensitiveVariableKeys(template);

            await AvoidHostPortConflictsAsync(template, options, sheet, values, containerDefinitions);
            await ValidateAsync(template, options, sheet, values, networkDefinitions, volumeDefinitions, containerDefinitions);

            var groupsByKey = new Dictionary<string, GroupViewModel>(StringComparer.OrdinalIgnoreCase);
            var volumeNodesByKey = new Dictionary<string, NodeViewModel>(StringComparer.OrdinalIgnoreCase);
            var containerNodesByKey = new Dictionary<string, NodeViewModel>(StringComparer.OrdinalIgnoreCase);
            var volumeNamesByKey = volumeDefinitions.ToDictionary(
                item => item.Key,
                item => Resolve(item.Name, values),
                StringComparer.OrdinalIgnoreCase);

            originX = Math.Round(originX / 10.0) * 10.0;
            originY = Math.Round(originY / 10.0) * 10.0;

            try
            {
                foreach (var network in networkDefinitions)
                {
                    string networkName = Resolve(network.Name, values);
                    string networkId;

                    if (options.DeployToDocker)
                    {
                        networkId = await _dockerService.CreateNetworkAsync(new NetworkCreateOptions
                        {
                            Name = networkName,
                            Driver = string.IsNullOrWhiteSpace(network.Driver) ? "bridge" : network.Driver,
                            Labels = CreateTemplateLabels(template, options.ProjectName, network.Key, "network")
                        });
                        application.NetworkIds.Add(networkId);
                    }
                    else
                    {
                        networkId = Guid.NewGuid().ToString();
                    }

                    var group = new GroupViewModel(
                        originX + network.X,
                        originY + network.Y,
                        network.Width,
                        network.Height,
                        _dockerService,
                        _dialogService,
                        networkName,
                        GroupType.Network)
                    {
                        Id = networkId,
                        Driver = string.IsNullOrWhiteSpace(network.Driver) ? "bridge" : network.Driver,
                        ComposeNetworkName = networkName,
                        IsDockerConnected = options.DeployToDocker
                    };

                    sheet.AddGroup(group);
                    groupsByKey[network.Key] = group;
                    application.Groups.Add(group);
                }

                foreach (var volume in volumeDefinitions)
                {
                    string volumeName = volumeNamesByKey[volume.Key];
                    string driver = string.IsNullOrWhiteSpace(volume.Driver) ? "local" : volume.Driver;

                    if (options.DeployToDocker)
                    {
                        await _dockerService.CreateVolumeAsync(new VolumeCreateOptions
                        {
                            Name = volumeName,
                            DockerVolumeName = volumeName,
                            Driver = driver,
                            Labels = CreateTemplateLabels(template, options.ProjectName, volume.Key, "volume")
                        });
                        application.VolumeNames.Add(volumeName);
                    }

                    var node = new NodeViewModel(_dockerService, _dockerService, _dialogService)
                    {
                        ParentSheet = sheet,
                        Name = volumeName,
                        DockerVolumeName = volumeName,
                        ComposeProjectName = options.ProjectName,
                        ComposeServiceName = volume.Key,
                        Type = NodeType.Volume,
                        Driver = driver,
                        ImageName = driver,
                        X = originX + volume.X,
                        Y = originY + volume.Y,
                        StatusColor = options.DeployToDocker ? "#E67E22" : "#808080",
                        IsDockerConnected = options.DeployToDocker,
                        DetailStatus = options.DeployToDocker ? "Created" : "Template plan"
                    };

                    sheet.Nodes.Add(node);
                    volumeNodesByKey[volume.Key] = node;
                    application.Nodes.Add(node);
                }

                var creationOrder = GetContainerCreationOrder(containerDefinitions, template.Dependencies);
                foreach (var container in containerDefinitions)
                {
                    string containerName = Resolve(container.Name, values);
                    string imageReference = Resolve(container.Image, values);
                    var ports = container.Ports.Select(port => Resolve(port, values)).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
                    var environment = BuildEnvironment(container.Environment, values, sensitiveVariableKeys, maskSensitive: true);
                    var enabledNetworks = container.Networks
                        .Where(groupsByKey.ContainsKey)
                        .ToList();

                    var node = new NodeViewModel(_dockerService, _dockerService, _dialogService)
                    {
                        ParentSheet = sheet,
                        Name = options.DeployToDocker ? $"{containerName} (Queued...)" : containerName,
                        ImageName = imageReference,
                        ComposeProjectName = options.ProjectName,
                        ComposeServiceName = container.Key,
                        ComposeContainerNumber = 1,
                        Type = NodeType.Container,
                        X = originX + container.X,
                        Y = originY + container.Y,
                        PortBindings = ports,
                        PortInfo = string.Join(", ", ports),
                        EnvironmentVariables = environment,
                        RestartPolicy = container.RestartPolicy,
                        IsCreating = options.DeployToDocker,
                        StatusColor = options.DeployToDocker ? "#FFC107" : "#808080",
                        IsDockerConnected = false,
                        IsRunning = false,
                        DetailStatus = options.DeployToDocker ? "Queued" : "Template plan"
                    };

                    sheet.Nodes.Add(node);
                    if (options.DeployToDocker)
                        node.SetCreationProgress("Queued");
                    containerNodesByKey[container.Key] = node;
                    application.Nodes.Add(node);

                    foreach (string networkKey in enabledNetworks)
                    {
                        if (groupsByKey.TryGetValue(networkKey, out var group))
                            await group.AddNodeAsync(node, isRestoring: true);
                    }

                    foreach (var mount in container.VolumeMounts)
                    {
                        if (!volumeNodesByKey.TryGetValue(mount.Volume, out var volumeNode)) continue;

                        var connector = new ConnectorViewModel(
                            node,
                            volumeNode,
                            PortDirection.Bottom,
                            PortDirection.Top,
                            _dialogService)
                        {
                            RelationType = RelationType.VolumeMount,
                            MountPath = Resolve(mount.Target, values)
                        };
                        sheet.Connectors.Add(connector);
                        application.Connectors.Add(connector);
                    }
                }

                foreach (var dependency in template.Dependencies)
                {
                    if (!containerNodesByKey.TryGetValue(dependency.Source, out var source) ||
                        !containerNodesByKey.TryGetValue(dependency.Target, out var target))
                    {
                        continue;
                    }

                    var connector = new ConnectorViewModel(
                        source,
                        target,
                        PortDirection.Right,
                        PortDirection.Left,
                        _dialogService)
                    {
                        RelationType = RelationType.Dependency
                    };
                    sheet.Connectors.Add(connector);
                    application.Connectors.Add(connector);
                }

                if (options.DeployToDocker)
                {
                    var pulledImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var container in creationOrder)
                    {
                        string containerName = Resolve(container.Name, values);
                        string imageReference = Resolve(container.Image, values);
                        var (image, tag) = SplitImageReference(imageReference);
                        var ports = container.Ports
                            .Select(port => Resolve(port, values))
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .ToList();
                        var environment = BuildEnvironment(container.Environment, values, sensitiveVariableKeys, maskSensitive: false);
                        var binds = new List<string>();
                        var enabledNetworks = container.Networks
                            .Where(groupsByKey.ContainsKey)
                            .ToList();
                        string primaryNetworkName = enabledNetworks.Count > 0
                            ? groupsByKey[enabledNetworks[0]].DockerNetworkName
                            : string.Empty;
                        NodeViewModel node = containerNodesByKey[container.Key];

                        foreach (var mount in container.VolumeMounts)
                        {
                            if (!volumeNamesByKey.TryGetValue(mount.Volume, out string? volumeName))
                                continue;

                            string binding = $"{volumeName}:{Resolve(mount.Target, values)}";
                            if (mount.ReadOnly)
                                binding += ":ro";
                            binds.Add(binding);
                        }

                        if (pulledImages.Add(imageReference))
                        {
                            node.Name = $"{containerName} (Pulling...)";
                            node.StatusColor = "#0D6EFD";
                            node.SetCreationProgress($"Pulling image ({application.ContainerIds.Count + 1}/{creationOrder.Count})");

                            var tracker = new DockerPullProgressTracker();
                            var progress = new Progress<Docker.DotNet.Models.JSONMessage>(message =>
                            {
                                var snapshot = tracker.Update(message);
                                node.SetCreationProgress(
                                    $"Pulling image ({application.ContainerIds.Count + 1}/{creationOrder.Count}) - {snapshot.Message}",
                                    snapshot.Percent);
                                node.StatusColor = snapshot.Percent.HasValue ? "#0D6EFD" : "#FFC107";
                            });

                            await _dockerService.PullImageWithProgressAsync(image, tag, progress);
                            node.SetCreationProgress($"Image pull complete ({application.ContainerIds.Count + 1}/{creationOrder.Count})", 100);
                        }

                        node.Name = $"{containerName} (Creating...)";
                        node.StatusColor = "#FFC107";
                        node.SetCreationProgress($"Creating container ({application.ContainerIds.Count + 1}/{creationOrder.Count})");
                        string containerId = await _dockerService.CreateAndStartContainerAsync(
                            containerName,
                            image,
                            tag,
                            ports,
                            environment,
                            binds,
                            container.RestartPolicy,
                            0,
                            0,
                            Resolve(container.Command, values),
                            false,
                            primaryNetworkName,
                            CreateTemplateLabels(
                                template,
                                options.ProjectName,
                                container.Key,
                                "container",
                                template.Dependencies
                                    .Where(dependency => dependency.Source.Equals(container.Key, StringComparison.OrdinalIgnoreCase))
                                    .Select(dependency => dependency.Target)));
                        application.ContainerIds.Add(containerId);

                        node.Name = containerName;
                        node.ContainerId = containerId;
                        node.IsCreating = false;
                        node.IsDockerConnected = true;
                        node.IsRunning = true;
                        node.DetailStatus = "Running";
                        node.StatusColor = "#28A745";
                        node.CreationProgressValue = 100;
                        node.CreationProgressMessage = "Created";

                        for (int networkIndex = 1; networkIndex < enabledNetworks.Count; networkIndex++)
                        {
                            node.DetailStatus = $"Connecting network {networkIndex + 1}/{enabledNetworks.Count}";
                            node.StatusColor = "#0D6EFD";
                            GroupViewModel group = groupsByKey[enabledNetworks[networkIndex]];
                            await _dockerService.ConnectNetworkAsync(
                                group.DockerNetworkName,
                                containerId,
                                node.GetNetworkOptions(group.Title));
                        }

                        node.DetailStatus = "Running";
                        node.StatusColor = "#28A745";
                    }
                }

                sheet.MapWidth = Math.Max(
                    sheet.MapWidth,
                    application.Nodes.Select(node => node.X + node.Width).Concat(
                        application.Groups.Select(group => group.X + group.Width)).DefaultIfEmpty(originX).Max() + 120);
                sheet.MapHeight = Math.Max(
                    sheet.MapHeight,
                    application.Nodes.Select(node => node.Y + node.Height).Concat(
                        application.Groups.Select(group => group.Y + group.Height)).DefaultIfEmpty(originY).Max() + 120);
                sheet.UpdateGroupLayering();
                return application;
            }
            catch
            {
                foreach (var node in application.Nodes.Where(node => node.Type == NodeType.Container))
                {
                    node.Name = node.Name.Replace(" (Queued...)", string.Empty)
                        .Replace(" (Pulling...)", string.Empty)
                        .Replace(" (Creating...)", string.Empty);
                    node.IsCreating = false;
                    node.IsRunning = false;
                    node.DetailStatus = "Failed - rolling back";
                    node.StatusColor = "#DC3545";
                }

                await RemoveAsync(application, sheet, options.DeployToDocker);
                throw;
            }
        }

        public async Task RemoveAsync(
            StackTemplateApplication application,
            SheetViewModel sheet,
            bool removeDockerResources)
        {
            foreach (var connector in application.Connectors.ToList())
                sheet.Connectors.Remove(connector);

            foreach (var group in application.Groups)
                group.ContainedNodes.Clear();

            foreach (var node in application.Nodes.ToList())
                sheet.Nodes.Remove(node);

            foreach (var group in application.Groups.ToList())
                sheet.Groups.Remove(group);

            if (!removeDockerResources)
            {
                sheet.UpdateGroupLayering();
                return;
            }

            foreach (string containerId in application.ContainerIds.AsEnumerable().Reverse())
            {
                try { await _dockerService.RemoveContainerAsync(containerId); }
                catch (Exception ex) { Debug.WriteLine($"[StackTemplate] Container rollback failed: {ex.Message}"); }
            }

            foreach (string volumeName in application.VolumeNames.AsEnumerable().Reverse())
            {
                try { await _dockerService.RemoveVolumeAsync(volumeName); }
                catch (Exception ex) { Debug.WriteLine($"[StackTemplate] Volume rollback failed: {ex.Message}"); }
            }

            foreach (string networkId in application.NetworkIds.AsEnumerable().Reverse())
            {
                try { await _dockerService.RemoveNetworkAsync(networkId); }
                catch (Exception ex) { Debug.WriteLine($"[StackTemplate] Network rollback failed: {ex.Message}"); }
            }

            sheet.UpdateGroupLayering();
        }

        private async Task AvoidHostPortConflictsAsync(
            StackTemplateDefinition template,
            StackTemplateDeploymentOptions options,
            SheetViewModel sheet,
            Dictionary<string, string> values,
            IReadOnlyCollection<StackTemplateContainerDefinition> containerDefinitions)
        {
            var hostPortVariableKeys = GetHostPortVariableKeys(containerDefinitions);
            if (hostPortVariableKeys.Count == 0)
                return;

            var usedPorts = GetDiagramHostPorts(sheet);

            if (options.DeployToDocker)
            {
                try
                {
                    var existingContainers = await _dockerService.GetContainersAsync();
                    usedPorts.UnionWith(GetPublishedDockerPorts(existingContainers));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[StackTemplate] Docker port scan failed: {ex.Message}");
                }
            }

            foreach (var variable in template.Variables.Where(variable => hostPortVariableKeys.Contains(variable.Key)))
            {
                if (!values.TryGetValue(variable.Key, out string? value) ||
                    !int.TryParse(value, out int desiredPort) ||
                    desiredPort is < 1 or > 65535)
                {
                    continue;
                }

                string desired = desiredPort.ToString();
                if (!usedPorts.Contains(desired))
                {
                    usedPorts.Add(desired);
                    continue;
                }

                int availablePort = FindAvailablePort(desiredPort, usedPorts);
                string adjusted = availablePort.ToString();
                values[variable.Key] = adjusted;
                options.Variables[variable.Key] = adjusted;
                usedPorts.Add(adjusted);
            }
        }

        private async Task ValidateAsync(
            StackTemplateDefinition template,
            StackTemplateDeploymentOptions options,
            SheetViewModel sheet,
            IReadOnlyDictionary<string, string> values,
            IReadOnlyCollection<StackTemplateNetworkDefinition> networkDefinitions,
            IReadOnlyCollection<StackTemplateVolumeDefinition> volumeDefinitions,
            IReadOnlyCollection<StackTemplateContainerDefinition> containerDefinitions)
        {
            foreach (var variable in template.Variables)
            {
                values.TryGetValue(variable.Key, out string? value);
                if (variable.Required && string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException($"'{variable.Label}' 값을 입력해 주세요.");

                if (variable.Type.Equals("port", StringComparison.OrdinalIgnoreCase) &&
                    (!int.TryParse(value, out int port) || port is < 1 or > 65535))
                {
                    throw new InvalidOperationException($"'{variable.Label}' 포트는 1~65535 사이여야 합니다.");
                }
            }

            var containerNames = containerDefinitions.Select(item => Resolve(item.Name, values)).ToList();
            var volumeNames = volumeDefinitions.Select(item => Resolve(item.Name, values)).ToList();
            var networkNames = networkDefinitions.Select(item => Resolve(item.Name, values)).ToList();
            var hostPorts = containerDefinitions
                .SelectMany(item => item.Ports)
                .Select(port => ExtractHostPort(Resolve(port, values)))
                .Where(port => !string.IsNullOrWhiteSpace(port))
                .ToList();

            EnsureUnique(containerNames, "컨테이너 이름");
            EnsureUnique(volumeNames, "볼륨 이름");
            EnsureUnique(networkNames, "네트워크 이름");
            EnsureUnique(hostPorts, "호스트 포트");

            var diagramContainerNames = sheet.Nodes
                .Where(node => node.Type == NodeType.Container)
                .Select(node => node.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var diagramVolumeNames = sheet.Nodes
                .Where(node => node.Type == NodeType.Volume)
                .Select(node => node.EffectiveVolumeName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var diagramNetworkNames = sheet.Groups
                .Where(group => group.Type == GroupType.Network)
                .Select(group => group.DockerNetworkName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            ThrowOnConflict(containerNames, diagramContainerNames, "다이어그램 컨테이너");
            ThrowOnConflict(volumeNames, diagramVolumeNames, "다이어그램 볼륨");
            ThrowOnConflict(networkNames, diagramNetworkNames, "다이어그램 네트워크");

            var diagramPorts = sheet.Nodes
                .Where(node => node.Type == NodeType.Container)
                .SelectMany(node => node.PortBindings)
                .Select(ExtractHostPort)
                .Where(port => !string.IsNullOrWhiteSpace(port))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ThrowOnConflict(hostPorts, diagramPorts, "호스트 포트");

            if (!options.DeployToDocker) return;

            var existingContainers = await _dockerService.GetContainersAsync();
            var existingVolumes = await _dockerService.GetVolumesAsync();
            var existingNetworks = await _dockerService.GetNetworksAsync();

            ThrowOnConflict(
                containerNames,
                existingContainers.Select(container => container.Name.TrimStart('/')).ToHashSet(StringComparer.OrdinalIgnoreCase),
                "Docker 컨테이너");
            ThrowOnConflict(
                volumeNames,
                existingVolumes.Select(volume => volume.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                "Docker 볼륨");
            ThrowOnConflict(
                networkNames,
                existingNetworks.Select(network => network.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                "Docker 네트워크");

            var usedDockerPorts = GetPublishedDockerPorts(existingContainers);
            ThrowOnConflict(hostPorts, usedDockerPorts, "Docker 호스트 포트");
        }

        private static HashSet<string> GetSensitiveVariableKeys(StackTemplateDefinition template)
        {
            return template.Variables
                .Where(variable => variable.Type.Equals("password", StringComparison.OrdinalIgnoreCase))
                .Select(variable => variable.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> BuildEnvironment(
            IReadOnlyDictionary<string, string> environment,
            IReadOnlyDictionary<string, string> values,
            ISet<string> sensitiveVariableKeys,
            bool maskSensitive)
        {
            var result = new List<string>();

            foreach (var item in environment)
            {
                string resolvedValue = Resolve(item.Value, values);
                if (maskSensitive &&
                    (ContainsSensitivePlaceholder(item.Value, sensitiveVariableKeys) ||
                     LooksSensitiveEnvironmentName(item.Key)))
                {
                    resolvedValue = "<secret>";
                }

                result.Add($"{item.Key}={resolvedValue}");
            }

            return result;
        }

        private static bool ContainsSensitivePlaceholder(string value, ISet<string> sensitiveVariableKeys)
        {
            if (sensitiveVariableKeys.Count == 0 || string.IsNullOrWhiteSpace(value))
                return false;

            return PlaceholderRegex.Matches(value)
                .Any(match => sensitiveVariableKeys.Contains(match.Groups[1].Value));
        }

        private static bool LooksSensitiveEnvironmentName(string name)
        {
            return name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string> GetHostPortVariableKeys(
            IReadOnlyCollection<StackTemplateContainerDefinition> containerDefinitions)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var portMapping in containerDefinitions.SelectMany(container => container.Ports))
            {
                string hostPart = ExtractHostPort(portMapping);
                if (string.IsNullOrWhiteSpace(hostPart))
                    continue;

                foreach (Match match in PlaceholderRegex.Matches(hostPart))
                    keys.Add(match.Groups[1].Value);
            }

            return keys;
        }

        private static HashSet<string> GetDiagramHostPorts(SheetViewModel sheet)
        {
            return sheet.Nodes
                .Where(node => node.Type == NodeType.Container)
                .SelectMany(node => node.PortBindings)
                .Select(ExtractHostPort)
                .Where(port => !string.IsNullOrWhiteSpace(port))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> GetPublishedDockerPorts(IEnumerable<DockerContainer> containers)
        {
            return containers
                .SelectMany(container => Regex.Matches(container.Ports ?? string.Empty, @"(?<!\d)(\d+)->")
                    .Select(match => match.Groups[1].Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static string ExtractHostPort(string portMapping)
        {
            if (string.IsNullOrWhiteSpace(portMapping))
                return string.Empty;

            string mapping = portMapping.Trim();
            int slashIndex = mapping.IndexOf('/');
            if (slashIndex >= 0)
                mapping = mapping[..slashIndex];

            string[] parts = mapping.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
                return string.Empty;

            return parts[^2];
        }

        private static int FindAvailablePort(int preferredPort, ISet<string> usedPorts)
        {
            for (int port = Math.Max(1, preferredPort); port <= 65535; port++)
            {
                if (!usedPorts.Contains(port.ToString()))
                    return port;
            }

            for (int port = 1024; port < Math.Max(1024, preferredPort); port++)
            {
                if (!usedPorts.Contains(port.ToString()))
                    return port;
            }

            throw new InvalidOperationException("사용 가능한 호스트 포트를 찾지 못했습니다.");
        }

        private static Dictionary<string, string> BuildValues(
            StackTemplateDefinition template,
            StackTemplateDeploymentOptions options)
        {
            string projectName = NormalizeProjectName(options.ProjectName);
            if (string.IsNullOrWhiteSpace(projectName))
                throw new InvalidOperationException("프로젝트 이름을 입력해 주세요.");

            options.ProjectName = projectName;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PROJECT"] = projectName
            };

            foreach (var variable in template.Variables)
            {
                values[variable.Key] = options.Variables.TryGetValue(variable.Key, out string? value)
                    ? value.Trim()
                    : variable.DefaultValue;
            }

            return values;
        }

        private static bool HasNameConflict(
            StackTemplateDefinition template,
            string projectName,
            ISet<string> diagramContainerNames,
            ISet<string> diagramVolumeNames,
            ISet<string> diagramNetworkNames,
            ISet<string> dockerContainerNames,
            ISet<string> dockerVolumeNames,
            ISet<string> dockerNetworkNames)
        {
            var values = BuildDefaultValues(template, projectName);
            var containerNames = template.Containers
                .Where(item => IsEnabled(item.Key, item.EnabledWhen, values))
                .Select(item => Resolve(item.Name, values));
            var volumeNames = template.Volumes
                .Where(item => IsEnabled(item.Key, item.EnabledWhen, values))
                .Select(item => Resolve(item.Name, values));
            var networkNames = template.Networks
                .Where(item => IsEnabled(item.Key, string.Empty, values))
                .Select(item => Resolve(item.Name, values));

            return containerNames.Any(name =>
                       diagramContainerNames.Contains(name) || dockerContainerNames.Contains(name)) ||
                   volumeNames.Any(name =>
                       diagramVolumeNames.Contains(name) || dockerVolumeNames.Contains(name)) ||
                   networkNames.Any(name =>
                       diagramNetworkNames.Contains(name) || dockerNetworkNames.Contains(name));
        }

        private static Dictionary<string, string> BuildDefaultValues(
            StackTemplateDefinition template,
            string projectName)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PROJECT"] = NormalizeProjectName(projectName)
            };

            foreach (var variable in template.Variables)
                values[variable.Key] = variable.DefaultValue;

            return values;
        }

        private static string Resolve(string? value, IReadOnlyDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            string resolved = value;
            for (int depth = 0; depth < 10; depth++)
            {
                string next = PlaceholderRegex.Replace(resolved, match =>
                {
                    string key = match.Groups[1].Value;
                    if (!values.TryGetValue(key, out string? replacement))
                        throw new InvalidOperationException($"템플릿 변수 '{key}'가 정의되어 있지 않습니다.");
                    return replacement;
                });

                if (next == resolved) return next;
                resolved = next;
            }

            throw new InvalidOperationException("템플릿 변수 치환이 순환 참조로 끝나지 않았습니다.");
        }

        private static bool IsEnabled(
            string key,
            string enabledWhen,
            IReadOnlyDictionary<string, string> values)
        {
            if (string.IsNullOrWhiteSpace(enabledWhen)) return true;
            return values.TryGetValue(enabledWhen, out string? value) &&
                   bool.TryParse(value, out bool enabled) &&
                   enabled;
        }

        private static string NormalizeProjectName(string value)
        {
            string normalized = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9_.-]+", "-");
            return normalized.Trim('-', '.');
        }

        private static Dictionary<string, string> CreateTemplateLabels(
            StackTemplateDefinition template,
            string projectName,
            string resourceName,
            string resourceType,
            IEnumerable<string>? dependsOn = null)
        {
            var labels = new Dictionary<string, string>
            {
                ["com.dockerdiagram.template"] = template.Id,
                ["com.dockerdiagram.project"] = projectName,
                ["com.dockerdiagram.resource"] = resourceName,
                ["com.dockerdiagram.resource-type"] = resourceType
            };

            string dependsOnValue = string.Join(",", dependsOn ?? Enumerable.Empty<string>());
            if (!string.IsNullOrWhiteSpace(dependsOnValue))
                labels["com.dockerdiagram.depends_on"] = dependsOnValue;

            return labels;
        }

        private static (string Image, string Tag) SplitImageReference(string imageReference)
        {
            int slash = imageReference.LastIndexOf('/');
            int colon = imageReference.LastIndexOf(':');
            if (colon > slash && colon < imageReference.Length - 1)
                return (imageReference[..colon], imageReference[(colon + 1)..]);
            return (imageReference, "latest");
        }

        private static void EnsureUnique(IEnumerable<string> values, string label)
        {
            var duplicate = values
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
                throw new InvalidOperationException($"{label} '{duplicate.Key}' 값이 템플릿 안에서 중복됩니다.");
        }

        private static void ThrowOnConflict(
            IEnumerable<string> candidates,
            ISet<string> existing,
            string label)
        {
            string? conflict = candidates.FirstOrDefault(existing.Contains);
            if (!string.IsNullOrWhiteSpace(conflict))
                throw new InvalidOperationException($"{label} '{conflict}'이(가) 이미 존재합니다.");
        }

        private static List<StackTemplateContainerDefinition> GetContainerCreationOrder(
            IReadOnlyCollection<StackTemplateContainerDefinition> containers,
            IReadOnlyCollection<StackTemplateDependencyDefinition> dependencies)
        {
            var byKey = containers.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
            var inDegree = containers.ToDictionary(item => item.Key, _ => 0, StringComparer.OrdinalIgnoreCase);
            var dependents = containers.ToDictionary(
                item => item.Key,
                _ => new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var dependency in dependencies)
            {
                if (!byKey.ContainsKey(dependency.Source) || !byKey.ContainsKey(dependency.Target))
                    continue;

                inDegree[dependency.Source]++;
                dependents[dependency.Target].Add(dependency.Source);
            }

            var queue = new Queue<string>(
                containers.Where(item => inDegree[item.Key] == 0).Select(item => item.Key));
            var ordered = new List<StackTemplateContainerDefinition>();

            while (queue.Count > 0)
            {
                string key = queue.Dequeue();
                ordered.Add(byKey[key]);

                foreach (string dependent in dependents[key])
                {
                    inDegree[dependent]--;
                    if (inDegree[dependent] == 0)
                        queue.Enqueue(dependent);
                }
            }

            if (ordered.Count != containers.Count)
                throw new InvalidOperationException("템플릿 컨테이너 의존성에 순환이 있습니다.");

            return ordered;
        }
    }
}
