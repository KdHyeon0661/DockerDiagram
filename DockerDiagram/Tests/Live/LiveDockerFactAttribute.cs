using Xunit;

namespace DockerDiagram.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LiveDockerFactAttribute : FactAttribute
{
    public LiveDockerFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DOCKERDIAGRAM_RUN_LIVE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set DOCKERDIAGRAM_RUN_LIVE_TESTS=1 to run isolated tests against the local Docker daemon.";
        }
    }
}
