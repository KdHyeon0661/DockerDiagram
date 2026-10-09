using System.Buffers.Binary;
using System.Text;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class DockerApiSwarmTaskLogTests
{
    [Fact]
    public async Task LocalTaskLog_ResolvesTaskAndReadsBackingContainerLogs()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(TaskList()),
            ScriptedDockerApiServer.Json("""{"ID":"service-1","Spec":{"Name":"api"}}"""),
            ScriptedDockerApiServer.Json(NodeList()),
            ScriptedDockerApiServer.Json(ManagerInfo()),
            ScriptedDockerApiServer.Bytes(
                Multiplex((1, "2026-09-20T03:04:05Z task output\n")),
                "application/vnd.docker.raw-stream"));
        using var service = CreateService(server);

        string logs = await service.GetSwarmTaskContainerLogsAsync("service-1", "task-1", 30);

        Assert.Contains("task output", logs, StringComparison.Ordinal);
        Assert.StartsWith("/containers/container-1/logs", server.Requests[4].Target, StringComparison.Ordinal);
        Assert.Contains("tail=30", server.Requests[4].Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoteTaskLog_StopsBeforeContainerRequestAndExplainsNodeBoundary()
    {
        await using var server = new ScriptedDockerApiServer(
            ScriptedDockerApiServer.Json(TaskList(nodeId: "worker-1")),
            ScriptedDockerApiServer.Json("""{"ID":"service-1","Spec":{"Name":"api"}}"""),
            ScriptedDockerApiServer.Json(NodeList(includeWorker: true)),
            ScriptedDockerApiServer.Json(ManagerInfo()));
        using var service = CreateService(server);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetSwarmTaskContainerLogsAsync("service-1", "task-1", 30));

        Assert.Contains("원격 node", error.Message, StringComparison.Ordinal);
        Assert.Equal(4, server.Requests.Count);
    }

    private static string TaskList(string nodeId = "manager-1") => $$$"""
        [
          {
            "ID":"task-1",
            "Annotations":{"Name":"api.1.task-1"},
            "Slot":1,
            "NodeID":"{{{nodeId}}}",
            "DesiredState":"running",
            "Spec":{"ContainerSpec":{"Image":"example/api:2"}},
            "Status":{"State":"running","ContainerStatus":{"ContainerID":"container-1","ExitCode":0}}
          }
        ]
        """;

    private static string NodeList(bool includeWorker = false) => includeWorker
        ? """
          [
            {
              "ID":"manager-1",
              "Spec":{"Role":"manager","Availability":"active"},
              "Description":{"Hostname":"manager-a"},
              "Status":{"State":"ready"},
              "ManagerStatus":{"Leader":true,"Reachability":"reachable"}
            },
            {
              "ID":"worker-1",
              "Spec":{"Role":"worker","Availability":"active"},
              "Description":{"Hostname":"worker-a"},
              "Status":{"State":"ready"}
            }
          ]
          """
        : """
          [
            {
              "ID":"manager-1",
              "Spec":{"Role":"manager","Availability":"active"},
              "Description":{"Hostname":"manager-a"},
              "Status":{"State":"ready"},
              "ManagerStatus":{"Leader":true,"Reachability":"reachable"}
            }
          ]
          """;

    private static string ManagerInfo() => """
        {"Swarm":{"LocalNodeState":"active","ControlAvailable":true,"NodeID":"manager-1","NodeAddr":"10.0.0.1","Error":""}}
        """;

    private static byte[] Multiplex(params (byte Stream, string Text)[] frames)
    {
        using var output = new MemoryStream();
        var header = new byte[8];
        foreach ((byte stream, string text) in frames)
        {
            Array.Clear(header);
            byte[] payload = Encoding.UTF8.GetBytes(text);
            header[0] = stream;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), payload.Length);
            output.Write(header);
            output.Write(payload);
        }
        return output.ToArray();
    }

    private static DockerApiService CreateService(ScriptedDockerApiServer server) =>
        new(new ConnectionProfile
        {
            Type = EndpointType.DockerContext,
            RuntimeKind = RuntimeKind.DockerSwarm,
            DockerEndpoint = server.DockerEndpoint.ToString()
        });
}
