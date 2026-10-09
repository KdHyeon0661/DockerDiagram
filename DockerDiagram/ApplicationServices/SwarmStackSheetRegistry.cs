using DockerDiagram.Common;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DockerDiagram.ApplicationServices
{
    public sealed class SwarmStackSheetState : ViewModelBase
    {
        private string _stackName = string.Empty;
        private string _sourcePath = string.Empty;
        private string _yaml = string.Empty;
        private SwarmStackDeployState _deployState = SwarmStackDeployState.Draft;
        private DateTimeOffset? _lastDeployedAt;
        private string _lastError = string.Empty;
        private string _runtimeSummary = "Draft stack";

        public string StackName { get => _stackName; set => SetProperty(ref _stackName, value ?? string.Empty); }
        public string SourcePath { get => _sourcePath; set => SetProperty(ref _sourcePath, value ?? string.Empty); }
        public string Yaml { get => _yaml; set => SetProperty(ref _yaml, value ?? string.Empty); }
        public SwarmStackDeployState DeployState { get => _deployState; set => SetProperty(ref _deployState, value); }
        public DateTimeOffset? LastDeployedAt { get => _lastDeployedAt; set => SetProperty(ref _lastDeployedAt, value); }
        public string LastError { get => _lastError; set => SetProperty(ref _lastError, value ?? string.Empty); }
        public string RuntimeSummary { get => _runtimeSummary; set => SetProperty(ref _runtimeSummary, value ?? string.Empty); }
    }

    public static class SwarmStackSheetRegistry
    {
        private static readonly ConditionalWeakTable<SheetViewModel, SwarmStackSheetState> States = new();

        public static bool IsStackSheet(SheetViewModel? sheet) =>
            sheet?.RuntimeKind == RuntimeKind.DockerSwarm &&
            (SwarmStackDocument.HasMetadata(sheet.ComposeRawYaml) ||
             sheet.Title.StartsWith("Stack ", StringComparison.OrdinalIgnoreCase) ||
             States.TryGetValue(sheet, out _));

        public static SwarmStackSheetState GetOrCreate(SheetViewModel sheet, int fallbackOrdinal = 1)
        {
            ArgumentNullException.ThrowIfNull(sheet);
            if (sheet.RuntimeKind != RuntimeKind.DockerSwarm)
                throw new InvalidOperationException("Docker Swarm 시트에서만 Stack identity를 사용할 수 있습니다.");

            return States.GetValue(sheet, current =>
            {
                SwarmStackDocumentData document = SwarmStackDocument.Parse(current.ComposeRawYaml);
                string stackName = string.IsNullOrWhiteSpace(document.Metadata.StackName)
                    ? SwarmStackNamePolicy.Suggest(current.Title, fallbackOrdinal)
                    : document.Metadata.StackName;
                return new SwarmStackSheetState
                {
                    StackName = stackName,
                    SourcePath = document.Metadata.SourcePath,
                    Yaml = document.Yaml,
                    DeployState = document.Metadata.DeployState,
                    LastDeployedAt = document.Metadata.LastDeployedAt,
                    LastError = document.Metadata.LastError,
                    RuntimeSummary = document.Metadata.DeployState == SwarmStackDeployState.Draft
                        ? "Draft stack"
                        : document.Metadata.DeployState.ToString()
                };
            });
        }

        public static void Persist(SheetViewModel sheet, SwarmStackSheetState state)
        {
            ArgumentNullException.ThrowIfNull(sheet);
            ArgumentNullException.ThrowIfNull(state);
            sheet.ComposeRawYaml = SwarmStackDocument.Serialize(
                state.Yaml,
                new SwarmStackDocumentMetadata(
                    state.StackName,
                    state.SourcePath,
                    state.DeployState,
                    state.LastDeployedAt,
                    SummarizeError(state.LastError)));
        }

        private static string SummarizeError(string value)
        {
            string oneLine = string.Join(" ", (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return oneLine.Length <= 300 ? oneLine : oneLine[..300];
        }
    }

    public sealed record SwarmStackDocumentMetadata(
        string StackName,
        string SourcePath,
        SwarmStackDeployState DeployState,
        DateTimeOffset? LastDeployedAt,
        string LastError)
    {
        public static SwarmStackDocumentMetadata Empty { get; } =
            new(string.Empty, string.Empty, SwarmStackDeployState.Draft, null, string.Empty);
    }

    public sealed record SwarmStackDocumentData(string Yaml, SwarmStackDocumentMetadata Metadata);

    public static class SwarmStackDocument
    {
        private const string Header = "# dockerdiagram.swarm-stack: ";

        public static bool HasMetadata(string text) =>
            !string.IsNullOrEmpty(text) && text.StartsWith(Header, StringComparison.Ordinal);

        public static string Serialize(string yaml, SwarmStackDocumentMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            string cleanYaml = Parse(yaml ?? string.Empty).Yaml.TrimStart('\r', '\n');
            string json = JsonSerializer.Serialize(metadata);
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            return $"{Header}{encoded}{Environment.NewLine}{cleanYaml}";
        }

        public static SwarmStackDocumentData Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Header, StringComparison.Ordinal))
                return new SwarmStackDocumentData(text ?? string.Empty, SwarmStackDocumentMetadata.Empty);

            int lineEnd = text.IndexOf('\n');
            string headerLine = lineEnd < 0 ? text : text[..lineEnd];
            string yaml = lineEnd < 0 ? string.Empty : text[(lineEnd + 1)..];
            try
            {
                string encoded = headerLine[Header.Length..].Trim();
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                SwarmStackDocumentMetadata? metadata = JsonSerializer.Deserialize<SwarmStackDocumentMetadata>(json);
                return new SwarmStackDocumentData(yaml, metadata ?? SwarmStackDocumentMetadata.Empty);
            }
            catch (Exception)
            {
                // 손상된 앱 메타데이터는 실행 YAML에 섞지 않고 Draft로 복구합니다.
                return new SwarmStackDocumentData(yaml, SwarmStackDocumentMetadata.Empty);
            }
        }
    }
}
