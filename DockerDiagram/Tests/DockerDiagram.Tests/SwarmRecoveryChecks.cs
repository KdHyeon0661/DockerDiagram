using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.Views;
using System.IO;
using System.Reflection;
using System.Windows.Controls;

internal static class SwarmRecoveryChecks
{
    public static void LeavePolicy()
    {
        var worker = State(false);
        Require(!SwarmLeavePolicy.Evaluate(worker).Force, "Worker must never use forced leave");
        var manager = State(true);
        var self = Node("node-id", "manager");
        Require(SwarmLeavePolicy.Evaluate(manager, new[] { self }).Force, "Single-node Manager requires explicit force");
        Reject(() => SwarmLeavePolicy.Evaluate(manager, new[] { self, Node("worker-2", "worker") }));
        Reject(() => SwarmLeavePolicy.Evaluate(manager, new[] { self, Node("manager-2", "manager") }));
        Reject(() => SwarmLeavePolicy.Evaluate(manager, Array.Empty<DockerSwarmNode>()));
        Reject(() => SwarmLeavePolicy.Evaluate(manager, new[] { Node("different-node", "manager") }));
        foreach (var status in new[] { "inactive", "pending", "locked", "error", "unknown" })
            Reject(() => SwarmLeavePolicy.Evaluate(SwarmClusterState.Create(status, false)));
    }

    public static void LeaveExecution() => RunLeaveExecutionAsync().GetAwaiter().GetResult();

    private static async Task RunLeaveExecutionAsync()
    {
        foreach (bool manager in new[] { false, true })
        {
            var service = new StubSwarmService { State = State(manager) };
            var workflow = new SwarmLeaveWorkflow(service);
            var plan = await workflow.PrepareAsync();
            var result = await workflow.ExecuteAsync(plan);
            Require(result.ConfirmedInactive, "leave must observe inactive before reporting success");
            Require(service.LeaveCalls == 1 && service.LastForce == manager, "one leave request with correct force");
        }

        var responseLost = new StubSwarmService { State = State(false), ThrowAfterLeave = true };
        var recovery = new SwarmLeaveWorkflow(responseLost);
        Require((await recovery.ExecuteAsync(await recovery.PrepareAsync())).ConfirmedInactive,
            "response lost after successful leave is recovered through /info");
        Require(responseLost.LeaveCalls == 1, "lost response must not repeat the mutation");

        var disconnected = new StubSwarmService { State = State(false), FailReadsAfterLeave = true };
        var uncertain = new SwarmLeaveWorkflow(disconnected);
        Require(!(await uncertain.ExecuteAsync(await uncertain.PrepareAsync())).ConfirmedInactive,
            "unreadable post-state must not be reported as successful");
        Require(disconnected.LeaveCalls == 1, "uncertain outcome must not automatically retry");
    }

    public static void LeaveChangesBeforeExecution() => RunChangedAsync().GetAwaiter().GetResult();

    private static async Task RunChangedAsync()
    {
        var service = new StubSwarmService { State = State(false) };
        var workflow = new SwarmLeaveWorkflow(service);
        var plan = await workflow.PrepareAsync();
        service.State = State(true);
        await RejectAsync(() => workflow.ExecuteAsync(plan));
        Require(service.LeaveCalls == 0, "changed role after confirmation must prevent leave");

        plan = await workflow.PrepareAsync();
        service.Nodes.Add(Node("new-worker", "worker"));
        await RejectAsync(() => workflow.ExecuteAsync(plan));
        Require(service.LeaveCalls == 0, "new cluster member after confirmation must prevent forced leave");

        service = new StubSwarmService { State = State(false) };
        workflow = new SwarmLeaveWorkflow(service);
        plan = await workflow.PrepareAsync();
        service.State = SwarmClusterState.Create("active", false, nodeId: "replacement");
        await RejectAsync(() => workflow.ExecuteAsync(plan));
        Require(service.LeaveCalls == 0, "changed node identity must require new confirmation");
    }

    public static void JoinIdentity()
    {
        var oldNode = Node("old-id", "worker");
        oldNode.Address = "10.0.0.12";
        Require(SwarmJoinVerification.FindJoinedNode(new[] { oldNode }, "new-id", "10.0.0.12", SwarmJoinRole.Worker) == null,
            "IP reuse must not confirm a different node");
        Require(SwarmJoinVerification.FindJoinedNode(new[] { Node("empty-address", "worker") }, "", "", SwarmJoinRole.Worker) == null,
            "missing addresses must not compare equal as proof of membership");
    }

    public static void SetupRecoveryControls()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckSetupControls(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new InvalidOperationException("Swarm recovery UI check failed", failure);
    }

    public static void JoinButtonFlow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var role in new[] { SwarmJoinRole.Worker, SwarmJoinRole.Manager })
                {
                    foreach (string outcome in new[] { "success", "cancel", "error", "changed" })
                        CheckJoinButton(role, outcome);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new InvalidOperationException("Swarm Join button flow failed", failure);
    }

    private static void CheckJoinButton(SwarmJoinRole role, string outcome)
    {
        int requests = 0, releases = 0;
        SwarmClusterState state = SwarmClusterState.Create("inactive", false);
        var manager = new StubSwarmService { State = State(true) };
        var dialogs = DispatchProxy.Create<IDialogService, CallProxy>();
        ((CallProxy)(object)dialogs).Handle = (method, _) =>
        {
            if (method != "ShowConfirm") return null;
            if (outcome == "changed") state = SwarmClusterState.Create("pending", false);
            return outcome != "cancel";
        };
        var factory = DispatchProxy.Create<IDockerServiceFactory, CallProxy>();
        ((CallProxy)(object)factory).Handle = (method, _) =>
        {
            if (method != "Release") return null;
            releases++;
            return true;
        };
        var target = DispatchProxy.Create<IDockerService, CallProxy>();
        ((CallProxy)(object)target).Handle = (method, args) =>
        {
            if (method == "GetSwarmStateAsync") return Task.FromResult(state);
            if (method != "JoinSwarmAsync") throw new NotSupportedException(method);
            requests++;
            var options = (SwarmJoinOptions)args![0]!;
            Require(options.JoinToken == (role == SwarmJoinRole.Manager ? "SWMTKN-manager-ui" : "SWMTKN-worker-ui"), "selected role token");
            Require(options.RemoteManagerAddresses.Single() == "10.0.0.10:2378", "edited Manager port reaches Join request");
            if (outcome == "error") return Task.FromException(new IOException("rejected " + options.JoinToken));
            state = SwarmClusterState.Create("active", role == SwarmJoinRole.Manager, nodeId: "joined-ui");
            manager.Nodes.Add(Node("joined-ui", role == SwarmJoinRole.Manager ? "manager" : "worker"));
            return Task.CompletedTask;
        };
        var window = new SwarmSetupDialog(manager, dialogs, factory, suggestLocalAddresses: false);
        try
        {
            Require(((ComboBox)window.FindName("AdvertiseAddressBox")).Text == "", "remote setup must not suggest this PC's LAN IP");
            InvokeAsync(window, "RefreshStateAsync");
            var options = new SwarmTargetConnectionOptions { Host = "test", AdvertiseAddress = "10.0.0.12", Role = role };
            SetField(window, "_targetConnection", new SwarmTargetConnectionSession(options, new ConnectionProfile(), target, state, factory));
            SetField(window, "_targetCanJoin", true);
            ((TextBox)window.FindName("TargetManagerAddressTextBox")).Text = "10.0.0.10:2378";
            window.GetType().GetMethod("JoinTarget_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
            Require(requests == (outcome is "cancel" or "changed" ? 0 : 1), "correct Join mutation count");
            Require(((Button)window.FindName("CloseButton")).IsEnabled, "Join path must restore UI");
            string summary = ((TextBlock)window.FindName("TargetConnectionSummaryText")).Text;
            Require(!summary.Contains("SWMTKN-"), "error presentation must redact tokens");
            Require(releases == (outcome == "success" ? 1 : 0), "only confirmed Join releases connection");
            if (outcome == "error")
                Require(((Button)window.FindName("JoinTargetButton")).IsEnabled, "inactive failed Join permits retry");
            if (outcome == "changed")
                Require(!((Button)window.FindName("JoinTargetButton")).IsEnabled, "pending preflight prevents retry");
        }
        finally { window.Close(); }
        Require(releases == 1, "close releases remaining connection exactly once");

        var managerState = SwarmClusterState.Create("active", true, nodeId: "self", nodeAddress: "10.0.0.10",
            remoteManagers: new[] { new SwarmManagerEndpoint("self", "10.0.0.10:2378") });
        string address = (string)typeof(SwarmSetupDialog).GetMethod("GetDefaultManagerAddress", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { managerState })!;
        Require(address == "10.0.0.10:2378", "custom Manager port must be retained");
    }

    private static void CheckSetupControls()
    {
        // Window는 생성만 하고 표시하지 않습니다. 실제 Docker/SSH 호출은 하지 않습니다.
        var manager = new StubSwarmService { State = State(true) };
        var dialogs = DispatchProxy.Create<IDialogService, CallProxy>();
        ((CallProxy)(object)dialogs).Handle = (_, _) => null;
        var factory = DispatchProxy.Create<IDockerServiceFactory, CallProxy>();
        ((CallProxy)(object)factory).Handle = (method, _) => method == "Release" ? true : null;
        var target = DispatchProxy.Create<IDockerService, CallProxy>();
        SwarmClusterState targetState = SwarmClusterState.Create("pending", false);
        ((CallProxy)(object)target).Handle = (method, _) => method == "GetSwarmStateAsync"
            ? Task.FromResult(targetState) : throw new NotSupportedException(method);
        var window = new SwarmSetupDialog(manager, dialogs, factory);
        try
        {
            InvokeAsync(window, "RefreshStateAsync");
            Require(((Button)window.FindName("AddTargetNodeButton")).IsEnabled, "refresh must restore Manager actions");
            Require(((Button)window.FindName("LeaveManagerButton")).IsEnabled, "Manager leave must be reachable");
            var options = new SwarmTargetConnectionOptions { Host = "test-host", AdvertiseAddress = "10.0.0.12" };
            SetField(window, "_targetConnection", new SwarmTargetConnectionSession(
                options, new ConnectionProfile(), target, targetState, factory));
            InvokeAsync(window, "RecoverTargetAsync", "");
            Require(!((Button)window.FindName("JoinTargetButton")).IsEnabled, "pending must block retry");
            Require(((Button)window.FindName("RecheckTargetButton")).IsEnabled, "pending must allow state-only recheck");
            targetState = SwarmClusterState.Create("inactive", false);
            InvokeAsync(window, "RecoverTargetAsync", "");
            Require(((Button)window.FindName("JoinTargetButton")).IsEnabled, "inactive must allow explicit retry");

            targetState = SwarmClusterState.Create("active", false, nodeId: "joined-worker");
            InvokeAsync(window, "RecoverTargetAsync", "");
            Require(!((Button)window.FindName("JoinTargetButton")).IsEnabled, "active but unconfirmed membership must block Join");
            Require(GetField(window, "_targetConnection") != null, "unconfirmed membership must retain recheck connection");
            manager.Nodes.Add(Node("joined-worker", "worker"));
            InvokeAsync(window, "RecoverTargetAsync", "");
            Require(GetField(window, "_targetConnection") == null, "matching Manager Node ID must finish and release connection");

            manager.State = SwarmClusterState.Create("inactive", false);
            InvokeAsync(window, "RefreshStateAsync");
            Require(((Button)window.FindName("InitializeButton")).IsEnabled, "inactive after leave must restore initialization");
            Require(!((Button)window.FindName("LeaveManagerButton")).IsEnabled, "inactive must disable leave");
        }
        finally { window.Close(); }
    }

    private static void InvokeAsync(object instance, string method, params object[] arguments) =>
        ((Task)instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(instance, arguments)!).GetAwaiter().GetResult();
    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static object? GetField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);

    private static SwarmClusterState State(bool manager) => SwarmClusterState.Create("active", manager, nodeId: "node-id");
    private static DockerSwarmNode Node(string id, string role) => new() { Id = id, Role = role };
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected leave policy to reject this state");
    }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected changed leave plan to be rejected");
    }

    private sealed class StubSwarmService : ISwarmService
    {
        public SwarmClusterState State { get; set; } = SwarmClusterState.Create("inactive", false);
        public List<DockerSwarmNode> Nodes { get; } = new() { Node("node-id", "manager") };
        public int LeaveCalls { get; private set; }
        public bool LastForce { get; private set; }
        public bool ThrowAfterLeave { get; init; }
        public bool FailReadsAfterLeave { get; init; }
        public Task<SwarmClusterState> GetSwarmStateAsync() =>
            FailReadsAfterLeave && LeaveCalls > 0
                ? Task.FromException<SwarmClusterState>(new IOException("connection lost"))
                : Task.FromResult(State);
        public Task<List<DockerSwarmNode>> GetSwarmNodesAsync() => Task.FromResult(Nodes);
        public Task LeaveSwarmAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeaveCalls++;
            LastForce = force;
            State = SwarmClusterState.Create("inactive", false);
            return ThrowAfterLeave ? Task.FromException(new IOException("response lost")) : Task.CompletedTask;
        }
        public Task<string> InitializeSwarmAsync(SwarmInitializeOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SwarmJoinTokens> GetJoinTokensAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SwarmJoinTokens("SWMTKN-worker-ui", "SWMTKN-manager-ui"));
        public Task JoinSwarmAsync(SwarmJoinOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<DockerContainer>> GetSwarmServicesAsync() => throw new NotSupportedException();
        public Task<List<DockerSwarmTask>> GetSwarmServiceTasksAsync(string serviceId) => throw new NotSupportedException();
        public Task<object> InspectSwarmServiceRawAsync(string serviceId) => throw new NotSupportedException();
        public Task ScaleSwarmServiceAsync(string serviceId, ulong replicas) => throw new NotSupportedException();
        public Task RemoveSwarmServiceAsync(string serviceId) => throw new NotSupportedException();
    }
}

public class CallProxy : DispatchProxy
{
    public Func<string, object?[]?, object?> Handle { get; set; } = (_, _) => null;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handle(targetMethod!.Name, args);
}
