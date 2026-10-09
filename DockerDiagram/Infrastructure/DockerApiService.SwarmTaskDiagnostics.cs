using Docker.DotNet;
using Docker.DotNet.Models;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Net.Http;

namespace DockerDiagram.Infrastructure
{
    public partial class DockerApiService : ISwarmTaskDiagnosticService
    {
        public async Task<SwarmTaskDiagnosticReport> GetSwarmTaskDiagnosticsAsync(
            string serviceId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(serviceId))
                throw new ArgumentException("Swarm service ID가 비어 있습니다.", nameof(serviceId));

            string normalizedId = serviceId.Trim();
            string filterJson = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                service = new Dictionary<string, bool> { [normalizedId] = true }
            });

            JArray rawTasks;
            JObject rawService;
            try
            {
                rawTasks = await MakeRawDockerRequestAsync<JArray>(
                    HttpMethod.Get,
                    $"tasks?filters={Uri.EscapeDataString(filterJson)}",
                    cancellationToken: cancellationToken);
                rawService = await MakeRawDockerRequestAsync<JObject>(
                    HttpMethod.Get,
                    $"services/{Uri.EscapeDataString(normalizedId)}",
                    cancellationToken: cancellationToken);
            }
            catch (DockerApiException ex)
            {
                throw new InvalidOperationException(
                    $"Swarm task 진단 정보를 조회하지 못했습니다.\nDocker: {ReadDockerErrorMessage(ex.ResponseBody, ex.Message)}",
                    ex);
            }

            List<DockerSwarmNode> nodes = await GetSwarmNodesAsync().WaitAsync(cancellationToken);
            Dictionary<string, string> nodeNames = nodes
                .Where(node => !string.IsNullOrWhiteSpace(node.Id))
                .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => FirstNonEmpty(group.First().Hostname, group.First().Name, group.Key),
                    StringComparer.OrdinalIgnoreCase);
            SwarmClusterState cluster = await GetSwarmStateCoreAsync(cancellationToken);

            var tasks = rawTasks.OfType<JObject>().Select(task =>
            {
                string nodeId = task["NodeID"]?.Value<string>() ?? string.Empty;
                string nodeName = nodeId.Length > 0 && nodeNames.TryGetValue(nodeId, out string? resolved)
                    ? resolved
                    : nodeId.Length > 0 ? nodeId : "Unassigned";
                JObject? status = task["Status"] as JObject;
                JObject? containerStatus = status?["ContainerStatus"] as JObject;
                string taskId = task["ID"]?.Value<string>() ?? string.Empty;
                return new SwarmTaskDiagnostic
                {
                    TaskId = taskId,
                    TaskName = task["Annotations"]?["Name"]?.Value<string>() ?? taskId,
                    Slot = task["Slot"]?.Value<ulong?>() ?? 0,
                    NodeId = nodeId,
                    NodeName = nodeName,
                    DesiredState = task["DesiredState"]?.Value<string>() ?? string.Empty,
                    CurrentState = status?["State"]?.Value<string>() ?? string.Empty,
                    Image = NormalizeImageReference(task["Spec"]?["ContainerSpec"]?["Image"]?.Value<string>() ?? string.Empty),
                    Error = status?["Err"]?.Value<string>() ?? string.Empty,
                    Message = status?["Message"]?.Value<string>() ?? string.Empty,
                    ContainerId = containerStatus?["ContainerID"]?.Value<string>() ?? string.Empty,
                    ExitCode = containerStatus?["ExitCode"]?.Value<long?>() ?? 0,
                    CreatedAt = ParseDockerTimestamp(task["CreatedAt"]),
                    UpdatedAt = ParseDockerTimestamp(task["UpdatedAt"]),
                    StatusTimestamp = ParseDockerTimestamp(status?["Timestamp"])
                };
            });

            return new SwarmTaskDiagnosticReport
            {
                ServiceId = rawService["ID"]?.Value<string>() ?? normalizedId,
                ServiceName = rawService["Spec"]?["Name"]?.Value<string>() ?? normalizedId,
                LocalNodeId = cluster.NodeId,
                Tasks = SwarmTaskDiagnosticAnalyzer.Analyze(tasks)
            };
        }

        public async Task<string> GetSwarmServiceLogsAsync(
            string serviceId,
            int tailCount = 500,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(serviceId))
                throw new ArgumentException("Swarm service ID가 비어 있습니다.", nameof(serviceId));
            if (tailCount < 0)
                throw new ArgumentOutOfRangeException(nameof(tailCount));

            try
            {
                var parameters = new ServiceLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Timestamps = true,
                    Details = true,
                    Follow = false,
                    Tail = tailCount == 0 ? "all" : tailCount.ToString(CultureInfo.InvariantCulture)
                };
                using MultiplexedStream stream = await _client.Swarm.GetServiceLogsAsync(
                    serviceId.Trim(),
                    false,
                    parameters,
                    cancellationToken);
                (string stdout, string stderr) = await stream.ReadOutputToEndAsync(cancellationToken);
                var sections = new List<string>();
                if (!string.IsNullOrWhiteSpace(stdout)) sections.Add(stdout.TrimEnd());
                if (!string.IsNullOrWhiteSpace(stderr)) sections.Add("[stderr]\n" + stderr.TrimEnd());
                return sections.Count == 0 ? "(No service logs found)" : string.Join(Environment.NewLine, sections);
            }
            catch (DockerApiException ex)
            {
                throw new InvalidOperationException(
                    "Service 로그를 조회하지 못했습니다. Docker logging driver가 로그 읽기를 지원하는지 확인해 주세요.\n" +
                    $"Docker: {ReadDockerErrorMessage(ex.ResponseBody, ex.Message)}",
                    ex);
            }
        }

        public async Task<string> GetSwarmTaskContainerLogsAsync(
            string serviceId,
            string taskId,
            int tailCount = 500,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                throw new ArgumentException("Swarm task ID가 비어 있습니다.", nameof(taskId));

            SwarmTaskDiagnosticReport report = await GetSwarmTaskDiagnosticsAsync(serviceId, cancellationToken);
            SwarmTaskDiagnostic task = report.Tasks.FirstOrDefault(candidate =>
                    candidate.TaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("선택한 Swarm task를 더 이상 찾을 수 없습니다.");
            if (string.IsNullOrWhiteSpace(task.ContainerId))
                throw new InvalidOperationException("이 task에는 생성된 container가 없어 container 로그를 읽을 수 없습니다.");
            if (!task.NodeId.Equals(report.LocalNodeId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"이 task는 원격 node '{task.NodeName}'에서 실행됩니다. " +
                    "현재 Manager 연결에서는 해당 node의 container 로그를 직접 읽을 수 없습니다. " +
                    "집계 Service Logs를 사용하거나 그 node에 직접 연결해 주세요.");
            cancellationToken.ThrowIfCancellationRequested();
            return await GetContainerLogsAsync(task.ContainerId, tailCount);
        }

        private static DateTimeOffset? ParseDockerTimestamp(JToken? token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Date && token is JValue value)
            {
                if (value.Value is DateTimeOffset offset) return offset;
                if (value.Value is DateTime dateTime)
                {
                    if (dateTime.Kind == DateTimeKind.Unspecified)
                        dateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
                    return new DateTimeOffset(dateTime);
                }
            }
            return DateTimeOffset.TryParse(
                token.Value<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed)
                ? parsed
                : null;
        }
    }
}
