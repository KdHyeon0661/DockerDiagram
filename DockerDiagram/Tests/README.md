# DockerDiagram test suite

Tests are grouped by product behavior. The suite keeps checks that protect runtime behavior,
data compatibility, command construction, cancellation, validation, and failure handling.
The exact Docker/Swarm contract status and live-environment gaps are tracked in
[`COVERAGE_MATRIX.md`](COVERAGE_MATRIX.md).

## Layout

- `Diagram/`: diagram object lifetime and resource synchronization
- `Persistence/`: diagram file persistence and viewport restoration
- `Infrastructure/`: Docker service construction and asynchronous infrastructure primitives
- `Docker/Compose/`: Compose YAML interpretation and CLI command construction
- `Docker/Images/`: image reference parsing and pull progress aggregation
- `Docker/Integration/`: container, image, volume, network, event, and system API behavior through a scripted Docker endpoint
- `Docker/Models/`: Docker resource option and result behavior
- `Support/`: reusable local fakes for observable integration boundaries
- `Swarm/Cluster/`: cluster state, join/leave, node management, and safety policy
- `Swarm/Services/`: service validation, mapping, editing, lifecycle, placement, and diagnostics
- `Swarm/Resources/`: Secret/Config models and diagram topology compilation
- `Swarm/Runtime/`: runtime event filtering, identity reconciliation, and offline-state behavior
- `Swarm/Stacks/`: Stack identity, metadata, command construction, and deployment
- `Swarm/Readiness/`: operational readiness policy
- `Swarm/Integration/`: persistence, runtime, cancellation, and fake-HTTP Docker API integration

`Usings.cs` stays at the project root because it configures the entire test assembly.

## Test policy

- Test observable behavior instead of source text, XAML wording, layout dimensions, or method existence.
- Keep one focused test for each meaningful success, failure, cancellation, or compatibility boundary.
- Balance coverage by product behavior, not by padding raw test counts. Docker and Swarm must each cover their public lifecycle, mapping, mutation, and failure boundaries.
- Add regression coverage to the narrowest product-area suite; do not create stage or catch-all suites.
- The default automated suite must not mutate a real Docker Engine or Swarm cluster. Use local scripted HTTP servers and injected command runners.
- Keep live Docker, multi-node Swarm, transport, and WPF verification separate, and never report those rows as passed from the automated suite alone.

## Commands

```powershell
dotnet test Tests/DockerDiagram.Tests.csproj -c Release
dotnet test Tests/DockerDiagram.Tests.csproj -c Release --filter "FullyQualifiedName~Swarm"
dotnet test Tests/DockerDiagram.Tests.csproj -c Release --filter "FullyQualifiedName~Readiness"
```

The full suite is the release gate. Filtered runs are for development feedback only.
