using System.IO;
using System.Reflection;
using System.Text.Json;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Tests;

public sealed class FileServiceTests
{
    [Fact]
    public void WriteValidatedFileAtomically_RoundTripsAndBacksUpPreviousFile()
    {
        string directory = CreateTestDirectory();
        string path = Path.Combine(directory, "diagram.vdm");

        try
        {
            var first = new DiagramFile
            {
                Version = "test-1",
                ActiveSheetIndex = 0,
                Sheets =
                {
                    new SheetData
                    {
                        Title = "First",
                        MapWidth = 2400,
                        MapHeight = 1800,
                        Scale = 1.25,
                        Nodes =
                        {
                            new NodeData
                            {
                                Id = "node-1",
                                Name = "web",
                                Type = NodeType.Container,
                                X = 120,
                                Y = 240
                            }
                        }
                    }
                }
            };

            DiagramSaveResult firstResult = FileService.WriteValidatedFileAtomically(
                path,
                JsonSerializer.Serialize(first));

            Assert.True(firstResult.Success, firstResult.ErrorMessage);

            var second = new DiagramFile
            {
                Version = "test-2",
                Sheets = { new SheetData { Title = "Second" } }
            };

            DiagramSaveResult secondResult = FileService.WriteValidatedFileAtomically(
                path,
                JsonSerializer.Serialize(second));

            Assert.True(secondResult.Success, secondResult.ErrorMessage);
            Assert.True(File.Exists(path + ".bak"));

            var third = new DiagramFile
            {
                Version = "test-3",
                Sheets =
                {
                    new SheetData
                    {
                        Title = "Third",
                        Scale = 1.25,
                        HasViewportCenter = true,
                        ViewportCenterX = 480,
                        ViewportCenterY = 360
                    }
                }
            };

            DiagramSaveResult thirdResult = FileService.WriteValidatedFileAtomically(
                path,
                JsonSerializer.Serialize(third));

            Assert.True(thirdResult.Success, thirdResult.ErrorMessage);

            DiagramFile? restored = JsonSerializer.Deserialize<DiagramFile>(File.ReadAllText(path));
            DiagramFile? backup = JsonSerializer.Deserialize<DiagramFile>(File.ReadAllText(path + ".bak"));

            Assert.NotNull(restored);
            Assert.Equal("test-3", restored.Version);
            SheetData restoredSheet = restored.Sheets.Single();
            Assert.Equal("Third", restoredSheet.Title);
            Assert.True(restoredSheet.HasViewportCenter);
            Assert.Equal(480, restoredSheet.ViewportCenterX);
            Assert.Equal(360, restoredSheet.ViewportCenterY);
            Assert.NotNull(backup);
            Assert.Equal("test-2", backup.Version);
            Assert.Equal("Second", backup.Sheets.Single().Title);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WriteValidatedFileAtomically_InvalidJsonPreservesExistingFile()
    {
        string directory = CreateTestDirectory();
        string path = Path.Combine(directory, "diagram.vdm");
        const string original = "original-content";

        try
        {
            File.WriteAllText(path, original);

            DiagramSaveResult result = FileService.WriteValidatedFileAtomically(path, "{ invalid json");

            Assert.False(result.Success);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".bak"));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ViewportCenter_RestoresSameWorldPositionAcrossViewportSizes()
    {
        IDockerService dockerService = DispatchProxy.Create<IDockerService, TrackingDockerServiceProxy>();
        IDialogService dialogService = DispatchProxy.Create<IDialogService, UnexpectedDialogServiceProxy>();
        var sheet = new SheetViewModel(
            "Viewport",
            new ConnectionProfile(),
            dockerService,
            dialogService)
        {
            Scale = 1.25,
            OffsetX = -100,
            OffsetY = -50
        };

        Assert.False(sheet.RestoreViewportOffset(viewportWidth: 1000, viewportHeight: 800));
        Assert.True(sheet.CaptureViewportCenter(viewportWidth: 1000, viewportHeight: 800));
        Assert.Equal(480, sheet.ViewportCenterX);
        Assert.Equal(360, sheet.ViewportCenterY);

        sheet.OffsetX = 0;
        sheet.OffsetY = 0;

        Assert.True(sheet.RestoreViewportOffset(viewportWidth: 1400, viewportHeight: 1000));
        Assert.Equal(100, sheet.OffsetX);
        Assert.Equal(50, sheet.OffsetY);
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "DockerDiagram.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

public class UnexpectedDialogServiceProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new NotSupportedException($"Unexpected dialog service call: {targetMethod?.Name}");
}
