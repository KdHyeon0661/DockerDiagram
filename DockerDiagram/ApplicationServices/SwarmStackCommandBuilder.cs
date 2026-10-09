using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public static class SwarmStackCommandBuilder
    {
        public static IReadOnlyList<string> BuildDeployArguments(
            SwarmStackDeploymentOptions options,
            string composeFilePath)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            if (string.IsNullOrWhiteSpace(composeFilePath))
                throw new ArgumentException("Stack YAML 임시 파일 경로가 비어 있습니다.", nameof(composeFilePath));

            var arguments = new List<string> { "stack", "deploy", "--detach=true" };
            if (options.Prune) arguments.Add("--prune");
            if (options.WithRegistryAuth) arguments.Add("--with-registry-auth");
            arguments.Add($"--resolve-image={options.ResolveImage}");
            arguments.Add("-c");
            arguments.Add(composeFilePath);
            arguments.Add(options.StackName.Trim());
            return arguments;
        }

        public static IReadOnlyList<string> BuildRemoveArguments(string stackName)
        {
            SwarmStackNamePolicy.Validate(stackName);
            return new[] { "stack", "rm", stackName.Trim() };
        }

        public static string Preview(IReadOnlyList<string> arguments) =>
            "docker " + string.Join(" ", arguments.Select(Quote));

        private static string Quote(string value) =>
            value.Any(char.IsWhiteSpace) || value.Contains('"')
                ? $"\"{value.Replace("\"", "\\\"")}\""
                : value;
    }
}
