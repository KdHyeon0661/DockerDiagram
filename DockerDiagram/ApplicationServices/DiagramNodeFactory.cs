using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.ApplicationServices
{
    public sealed record SwarmNodeSpec(
        RuntimeResourceKind ResourceKind,
        string Name,
        string ImageName,
        double X,
        double Y,
        SheetViewModel ParentSheet,
        RuntimeBindingState BindingState,
        bool IsConnected);

    public static class DiagramNodeFactory
    {
        private static NodeViewModel CreateCore(
            IContainerService containerService,
            IVolumeService volumeService,
            IDialogService dialogService,
            NodeType type) =>
            new(containerService, volumeService, dialogService) { Type = type };

        public static NodeViewModel CreateContainer(
            IContainerService containerService,
            IVolumeService volumeService,
            IDialogService dialogService) =>
            CreateCore(containerService, volumeService, dialogService, NodeType.Container);

        public static NodeViewModel CreateInternet(
            IContainerService containerService,
            IVolumeService volumeService,
            IDialogService dialogService) =>
            CreateCore(containerService, volumeService, dialogService, NodeType.Internet);

        public static NodeViewModel CreateHostPath(
            IContainerService containerService,
            IVolumeService volumeService,
            IDialogService dialogService) =>
            CreateCore(containerService, volumeService, dialogService, NodeType.HostPath);

        public static NodeViewModel CreateForRestore(
            IContainerService containerService,
            IVolumeService volumeService,
            IDialogService dialogService,
            NodeType type) => type switch
            {
                NodeType.Container => CreateContainer(containerService, volumeService, dialogService),
                NodeType.Volume => CreateCore(containerService, volumeService, dialogService, NodeType.Volume),
                NodeType.Internet => CreateInternet(containerService, volumeService, dialogService),
                NodeType.HostPath => CreateHostPath(containerService, volumeService, dialogService),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "지원하지 않는 노드 형식입니다.")
            };

        public static NodeViewModel CreateSwarmNode(
            IContainerService containerService,
            IVolumeService volumeService,
            IDialogService dialogService,
            SwarmNodeSpec spec)
        {
            ArgumentNullException.ThrowIfNull(spec);
            NodeViewModel node = spec.ResourceKind switch
            {
                RuntimeResourceKind.SwarmVolume => CreateVolume(
                    containerService,
                    volumeService,
                    dialogService,
                    spec.Name,
                    spec.X,
                    spec.Y,
                    spec.IsConnected),
                RuntimeResourceKind.SwarmExternalTraffic => CreateInternet(
                    containerService,
                    volumeService,
                    dialogService),
                RuntimeResourceKind.SwarmService or
                    RuntimeResourceKind.SwarmSecret or
                    RuntimeResourceKind.SwarmConfig => CreateContainer(
                        containerService,
                        volumeService,
                        dialogService),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(spec),
                    spec.ResourceKind,
                    "지원하지 않는 Swarm 노드 종류입니다.")
            };

            node.Name = spec.Name;
            node.ImageName = spec.ImageName;
            node.X = spec.X;
            node.Y = spec.Y;
            node.ParentSheet = spec.ParentSheet;
            node.RuntimeKind = RuntimeKind.DockerSwarm;
            node.ResourceKind = spec.ResourceKind;
            node.BindingState = spec.BindingState;
            node.IsSwarmService = spec.ResourceKind == RuntimeResourceKind.SwarmService;
            node.SetDockerConnection(spec.IsConnected);
            return node;
        }

        public static NodeViewModel CreatePendingContainer(
            IContainerService containerService,
            IVolumeService volumeService,
            IDialogService dialogService,
            string name,
            string imageName,
            double x,
            double y)
        {
            NodeViewModel node = CreateContainer(containerService, volumeService, dialogService);
            node.Name = name;
            node.ImageName = imageName;
            node.X = x;
            node.Y = y;
            node.SetLifecycleState(RuntimeLifecycleState.Creating, "Creating");
            return node;
        }

        public static NodeViewModel CreateVolume(
            IContainerService containerService,
            IVolumeService volumeService,
            IDialogService dialogService,
            string name,
            double x,
            double y,
            bool isConnected)
        {
            NodeViewModel node = CreateCore(containerService, volumeService, dialogService, NodeType.Volume);
            node.Name = name;
            node.X = x;
            node.Y = y;
            node.SetDockerConnection(isConnected);
            node.StatusColor = "#E67E22";
            return node;
        }
    }
}
