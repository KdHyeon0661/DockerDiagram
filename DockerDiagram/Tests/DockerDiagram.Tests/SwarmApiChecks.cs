using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class SwarmApiChecks
{
    public static void Lifecycle() => LifecycleAsync().GetAwaiter().GetResult();
    private static async Task LifecycleAsync()
    {
        await using var engine = new DockerResponseServer();
        using var service = engine.CreateService();
        Require((await service.GetSwarmStateAsync()).Membership == SwarmMembershipState.Inactive, "initial state");
        string id = await service.InitializeSwarmAsync(new SwarmInitializeOptions
        {
            AdvertiseAddress = "10.0.0.10", ListenAddress = "0.0.0.0:2378", DataPathPort = 4790
        });
        var initialized = await service.GetSwarmStateAsync();
        Require(id == "test-node" && initialized.IsManager, $"init response id=[{id}], state={initialized.Membership}");
        var init = engine.Requests.Single(r => r.Path == "/swarm/init");
        Require(init.Method == "POST" && init.Body?["AdvertiseAddr"]?.ToString() == "10.0.0.10", "init wire payload");
        Require(init.Body?["DataPathPort"]?.GetValue<int>() == 4790, "data path port is serialized");
        var tokens = await service.GetJoinTokensAsync();
        Require(tokens.GetToken(SwarmJoinRole.Worker) == "SWMTKN-worker-test", "worker token deserialization");
        Require(tokens.GetToken(SwarmJoinRole.Manager) == "SWMTKN-manager-test", "manager token deserialization");
        var workflow = new SwarmLeaveWorkflow(service);
        Require((await workflow.ExecuteAsync(await workflow.PrepareAsync())).ConfirmedInactive, "single-node Manager leave over API");
        string forceQuery = engine.Requests.Last(r => r.Path == "/swarm/leave").Query;
        Require(forceQuery.Contains("force=true", StringComparison.OrdinalIgnoreCase) || forceQuery.Contains("force=1"),
            "Manager force query: " + forceQuery);

        foreach (var role in new[] { SwarmJoinRole.Worker, SwarmJoinRole.Manager })
        {
            await service.JoinSwarmAsync(new SwarmJoinOptions
            {
                RemoteManagerAddresses = new[] { "10.0.0.10:2378" },
                AdvertiseAddress = "10.0.0.12", Role = role, JoinToken = tokens.GetToken(role)
            });
            var joined = await service.GetSwarmStateAsync();
            Require(SwarmJoinVerification.HasExpectedRole(joined, role), "joined role over actual API");
            var request = engine.Requests.Last(r => r.Path == "/swarm/join");
            Require(request.Method == "POST" && request.Body?["RemoteAddrs"]?[0]?.ToString() == "10.0.0.10:2378", "join endpoint");
            Require(request.Body?["JoinToken"]?.ToString() == tokens.GetToken(role), "role-specific token on wire");
            Require(request.Body?["AdvertiseAddr"]?.ToString() == "10.0.0.12", "target advertise address on wire");
            Require((await workflow.ExecuteAsync(await workflow.PrepareAsync())).ConfirmedInactive, "leave allows rejoin");
        }
    }

    public static void GuardsAndCancellation() => GuardsAsync().GetAwaiter().GetResult();
    private static async Task GuardsAsync()
    {
        await using var engine = new DockerResponseServer();
        using var service = engine.CreateService();
        var join = new SwarmJoinOptions { JoinToken = "SWMTKN-worker-test", RemoteManagerAddresses = new[] { "10.0.0.10:2377" } };
        engine.State = "active";
        await RejectAsync(() => service.JoinSwarmAsync(join));
        await RejectAsync(() => service.GetJoinTokensAsync());
        Require(!engine.Requests.Any(r => r.Method == "POST" || r.Path == "/swarm"), "Worker cannot fetch tokens or join twice");
        engine.Manager = true;
        engine.ExtraNode = true;
        await RejectAsync(() => service.LeaveSwarmAsync(force: true));
        Require(!engine.Requests.Any(r => r.Path == "/swarm/leave"), "multi-node Manager leave blocked before mutation");
        engine.Manager = false;
        engine.State = "inactive";
        engine.DelayInfo = true;
        using var cts = new CancellationTokenSource();
        Task operation = service.JoinSwarmAsync(join, cts.Token);
        await engine.InfoArrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();
        try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        engine.ReleaseInfo.TrySetResult();
        Require(!engine.Requests.Any(r => r.Path == "/swarm/join"), "cancelled preflight must not send Join");
    }

    public static void MenuAccess()
    {
        Require(RuntimeUiProfiles.For(RuntimeKind.DockerEngine).ShowSwarmSetup, "standalone and remote Worker need setup access");
        Require(RuntimeUiProfiles.For(RuntimeKind.DockerSwarm).ShowSwarmSetup, "Manager setup access");
        RuntimeUiProfile kubernetes = RuntimeUiProfiles.For(RuntimeKind.Kubernetes);
        Require(kubernetes.IsAvailable, "Kubernetes runtime must remain available");
        Require(kubernetes.ShowStandaloneSidebar, "Kubernetes resources use the existing resource sidebar");
        Require(!kubernetes.ShowSwarmSetup, "Kubernetes runtime must not expose Docker mutation");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected invalid state to be rejected");
    }

    private sealed record Request(string Method, string Path, string Query, JsonNode? Body);

    // 실제 엔진 대신 루프백에서 Docker API 응답을 반환하는 테스트 전용 서버입니다.
    private sealed class DockerResponseServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;
        private readonly List<Task> _clients = new();
        public ConcurrentQueue<Request> Requests { get; } = new();
        public string State = "inactive";
        public bool Manager;
        public bool ExtraNode;
        public bool DelayInfo;
        public TaskCompletionSource InfoArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseInfo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DockerResponseServer() { _listener.Start(); _acceptLoop = AcceptAsync(); }
        public DockerApiService CreateService() => new(new ConnectionProfile
        {
            Type = EndpointType.DockerContext,
            DockerEndpoint = $"tcp://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}"
        });
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _clients.Add(RespondAsync(client));
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using NetworkStream stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    string[] line = (await reader.ReadLineAsync(_stop.Token) ?? "").Split(' ');
                    if (line.Length < 2) return;
                    int length = 0;
                    string? header;
                    while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(_stop.Token)))
                    {
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = int.Parse(header.Split(':')[1].Trim());
                        if (header.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase))
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), _stop.Token);
                    }
                    char[] body = new char[length];
                    int offset = 0;
                    while (offset < length)
                    {
                        int read = await reader.ReadAsync(body.AsMemory(offset), _stop.Token);
                        if (read == 0) throw new IOException("Incomplete request");
                        offset += read;
                    }
                    var uri = new Uri("http://localhost" + line[1]);
                    string path = Regex.Replace(uri.AbsolutePath, @"^/v\d+\.\d+", "");
                    var request = new Request(line[0], path, uri.Query, length == 0 ? null : JsonNode.Parse(new string(body)));
                    Requests.Enqueue(request);
                    if (path == "/info" && DelayInfo)
                    {
                        InfoArrived.TrySetResult();
                        await ReleaseInfo.Task.WaitAsync(_stop.Token);
                    }
                    string json = Reply(request);
                    byte[] payload = Encoding.UTF8.GetBytes(json);
                    byte[] headers = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.WriteAsync(payload, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) when (DelayInfo) { /* cancelled client disconnected during delayed /info */ }
            }
        }
        private string Reply(Request request)
        {
            switch (request.Path)
            {
                case "/_ping": return "OK";
                case "/version": return """{"ApiVersion":"1.41","Version":"test"}""";
                case "/info":
                    return new JsonObject { ["Swarm"] = new JsonObject
                    {
                        ["LocalNodeState"] = State, ["ControlAvailable"] = Manager,
                        ["NodeID"] = State == "active" ? "test-node" : "",
                        ["NodeAddr"] = "10.0.0.12",
                        ["RemoteManagers"] = new JsonArray(new JsonObject { ["NodeID"] = "manager", ["Addr"] = "10.0.0.10:2378" })
                    }}.ToJsonString();
                case "/swarm/init": State = "active"; Manager = true; return "\"test-node\"";
                case "/swarm": return """{"JoinTokens":{"Worker":"SWMTKN-worker-test","Manager":"SWMTKN-manager-test"}}""";
                case "/swarm/join":
                    State = "active"; Manager = request.Body?["JoinToken"]?.ToString() == "SWMTKN-manager-test";
                    return "";
                case "/swarm/leave": State = "inactive"; Manager = false; return "";
                case "/nodes":
                    var nodes = new JsonArray(new JsonObject
                    {
                        ["ID"] = "test-node", ["Spec"] = new JsonObject { ["Role"] = Manager ? "manager" : "worker" },
                        ["Description"] = new JsonObject { ["Hostname"] = "test-host" },
                        ["Status"] = new JsonObject { ["State"] = "ready", ["Addr"] = "10.0.0.12" }
                    });
                    if (ExtraNode) nodes.Add(new JsonObject { ["ID"] = "extra", ["Spec"] = new JsonObject { ["Role"] = "worker" } });
                    return nodes.ToJsonString();
                default: throw new InvalidOperationException("Unexpected Docker API endpoint: " + request.Path);
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _acceptLoop;
            await Task.WhenAll(_clients);
            _stop.Dispose();
        }
    }
}
