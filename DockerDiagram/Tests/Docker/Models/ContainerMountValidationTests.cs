using System.Reflection;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class ContainerMountValidationTests
{
    [Theory]
    [InlineData("./relative", "/data", "절대 Docker 호스트 경로")]
    [InlineData("C:\\data", "relative", "절대 경로")]
    public void DetailEditorRejectsRelativeBindSourceAndContainerTarget(
        string source,
        string target,
        string expectedMessage)
    {
        using IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, LenientDialogServiceProxy>();
        using var node = new NodeViewModel(dockerService, dockerService, dialogService)
        {
            Type = NodeType.Container,
            Name = "api"
        };
        ContainerOperationsViewModel operations = node.ContainerOperations;
        operations.BindMountItems.Add(new MountDisplayItem
        {
            Kind = MountKind.Bind,
            Source = source,
            Target = target,
            Mode = "rw",
            BindPropagation = "rprivate"
        });
        MethodInfo validate = typeof(ContainerOperationsViewModel).GetMethod(
            "ValidateDrafts",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = [null];

        bool valid = (bool)validate.Invoke(operations, arguments)!;

        Assert.False(valid);
        Assert.Contains(expectedMessage, Assert.IsType<string>(arguments[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredContainerCreationOverloadCannotFallBackToLossyLegacyStrings()
    {
        MethodInfo overload = typeof(IContainerService).GetMethods()
            .Single(method =>
                method.Name == nameof(IContainerService.CreateAndStartContainerAsync) &&
                method.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(IReadOnlyList<ContainerMountSpec>)));

        Assert.True(overload.IsAbstract);
    }
}
