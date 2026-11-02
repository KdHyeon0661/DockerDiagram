namespace DockerDiagram.Common
{
    public static class DockerConnectionTiming
    {
        public static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan NamedPipeConnectTimeout = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);
        public static readonly TimeSpan RawApiConnectTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan ContextQueryTimeout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan RegistryMetadataTimeout = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan ServiceStartupPollInterval = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan SystemInfoCacheTtl = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan HttpConnectionIdleTimeout = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan HttpConnectionLifetime = TimeSpan.FromMinutes(10);
        public const int HealthFailureThreshold = 3;
    }

    public static class RuntimeSyncTiming
    {
        public static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan ContainerStatsInterval = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan DetailRefreshInterval = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan FullSyncInterval = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan EventDebounceInterval = TimeSpan.FromMilliseconds(600);
        public static readonly TimeSpan ResourceCacheTtl = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan GroupStartStagger = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan GroupStopStagger = TimeSpan.FromMilliseconds(500);
    }

    public static class ContainerCreationTiming
    {
        public static readonly TimeSpan CliCommandTimeout = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan ComposeCommandTimeout = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan MetadataLookupDebounce = TimeSpan.FromMilliseconds(800);
        public static readonly TimeSpan PullProgressPollInterval = TimeSpan.FromMilliseconds(200);
        public static readonly TimeSpan ComposePlacementInspectTimeout = TimeSpan.FromSeconds(20);
    }

    public static class SwarmOperationTiming
    {
        public static readonly TimeSpan ReadinessQueryTimeout = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan SetupShortTimeout = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan LeaveTimeout = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan UiOperationTimeout = TimeSpan.FromSeconds(90);
        public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
        public static readonly TimeSpan StackSyncRetryDelay = TimeSpan.FromMilliseconds(750);
        public static readonly TimeSpan RuntimeQueryTimeout = TimeSpan.FromSeconds(60);
    }
}
