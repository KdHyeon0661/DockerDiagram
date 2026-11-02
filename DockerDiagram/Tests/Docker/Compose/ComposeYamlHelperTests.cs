using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class ComposeYamlHelperTests
{

    private static readonly string[] LongPortSyntaxConvertsToDockerDisplayFormExpectedValues = new[] { "127.0.0.1:8080:80/tcp" };
    private static readonly string[] NetworkOptionsPreserveAddressesAliasesAndDriverOptionsExpectedValues = new[] { "api", "internal-api" };

    [Fact]
    public void ParseMappingInvalidYamlReturnsNull()
    {
        Assert.Null(ComposeYamlHelper.ParseMapping("services: [unterminated"));
    }

    [Fact]
    public void EnvironmentMappingConvertsValuesAndNullEntries()
    {
        Dictionary<object, object>? root = ComposeYamlHelper.ParseMapping("""
            environment:
              ASPNETCORE_ENVIRONMENT: Production
              INHERITED:
            """);

        List<string> environment = ComposeYamlHelper.ToEnvironmentList(
            ComposeYamlHelper.GetValue(root, "environment"));

        Assert.Contains("ASPNETCORE_ENVIRONMENT=Production", environment);
        Assert.Contains("INHERITED", environment);
    }

    [Fact]
    public void LongPortSyntaxConvertsToDockerDisplayForm()
    {
        Dictionary<object, object>? root = ComposeYamlHelper.ParseMapping("""
            ports:
              - target: 80
                published: 8080
                protocol: tcp
                host_ip: 127.0.0.1
            """);

        List<string> ports = ComposeYamlHelper.ToPortBindingList(
            ComposeYamlHelper.GetValue(root, "ports"));

        Assert.Equal(LongPortSyntaxConvertsToDockerDisplayFormExpectedValues, ports);
    }

    [Fact]
    public void VolumeSyntaxHandlesShortModeAndLongForm()
    {
        Dictionary<object, object>? root = ComposeYamlHelper.ParseMapping("""
            volumes:
              - data:/var/lib/app:ro
              - type: bind
                source: ./config
                target: /etc/app
            """);

        List<VolumeMountInfo> mounts = ComposeYamlHelper.ToVolumeMounts(
            ComposeYamlHelper.GetValue(root, "volumes"));

        Assert.Collection(
            mounts,
            volume =>
            {
                Assert.Equal("data", volume.Source);
                Assert.Equal("/var/lib/app", volume.Target);
                Assert.Equal(MountKind.Volume, volume.Kind);
                Assert.True(volume.ReadOnly);
            },
            bind =>
            {
                Assert.Equal("./config", bind.Source);
                Assert.Equal("/etc/app", bind.Target);
                Assert.Equal(MountKind.Bind, bind.Kind);
                Assert.Equal("rprivate", bind.BindPropagation);
            });
    }

    [Fact]
    public void LongMountSyntaxPreservesAdvancedOptions()
    {
        Dictionary<object, object>? root = ComposeYamlHelper.ParseMapping("""
            volumes:
              - type: volume
                source: data
                target: /var/lib/app
                read_only: true
                volume:
                  nocopy: true
              - type: bind
                source: ./config
                target: /etc/app
                bind:
                  propagation: rshared
              - type: tmpfs
                target: /run/cache
                tmpfs:
                  size: 64m
                  mode: 1770
            """);

        List<VolumeMountInfo> mounts = ComposeYamlHelper.ToVolumeMounts(
            ComposeYamlHelper.GetValue(root, "volumes"));

        Assert.True(mounts[0].VolumeNoCopy);
        Assert.True(mounts[0].ReadOnly);
        Assert.Equal("rshared", mounts[1].BindPropagation);
        Assert.Equal(MountKind.Tmpfs, mounts[2].Kind);
        Assert.Equal(64L * 1024 * 1024, mounts[2].TmpfsSizeBytes);
        Assert.Equal(Convert.ToUInt32("1770", 8), mounts[2].TmpfsMode);
    }

    [Fact]
    public void UnsupportedExplicitMountTypeIsNotMisclassifiedAsVolume()
    {
        Dictionary<object, object>? root = ComposeYamlHelper.ParseMapping("""
            volumes:
              - type: secret
                source: api-token
                target: /run/token
            """);

        List<VolumeMountInfo> mounts = ComposeYamlHelper.ToVolumeMounts(
            ComposeYamlHelper.GetValue(root, "volumes"));

        Assert.Empty(mounts);
    }

    [Fact]
    public void ServiceTmpfsSyntaxConvertsToExplicitMounts()
    {
        Dictionary<object, object>? root = ComposeYamlHelper.ParseMapping("""
            tmpfs:
              - /run
              - /cache:size=32m,mode=1750,ro
            """);

        List<ContainerMountSpec> mounts = ComposeYamlHelper.ToTmpfsMounts(
            ComposeYamlHelper.GetValue(root, "tmpfs"));

        Assert.Collection(
            mounts,
            first => Assert.Equal("/run", first.Target),
            second =>
            {
                Assert.Equal("/cache", second.Target);
                Assert.Equal(32L * 1024 * 1024, second.TmpfsSizeBytes);
                Assert.Equal(Convert.ToUInt32("1750", 8), second.TmpfsMode);
                Assert.True(second.ReadOnly);
            });
    }

    [Fact]
    public void NetworkOptionsPreserveAddressesAliasesAndDriverOptions()
    {
        Dictionary<object, object>? root = ComposeYamlHelper.ParseMapping("""
            networks:
              backend:
                ipv4_address: 10.30.0.12
                ipv6_address: fd00::12
                aliases: [api, internal-api]
                driver_opts:
                  com.example.option: enabled
            """);

        var options = ComposeYamlHelper.GetNetworkOptions(
            ComposeYamlHelper.GetValue(root, "networks"),
            "backend");

        Assert.Equal("10.30.0.12", options.StaticIPv4);
        Assert.Equal("fd00::12", options.StaticIPv6);
        Assert.Equal(NetworkOptionsPreserveAddressesAliasesAndDriverOptionsExpectedValues, options.Aliases);
        Assert.Equal("enabled", options.DriverOptions["com.example.option"]);
    }

    [Fact]
    public void ServiceLookupIsCaseInsensitiveAndPreservesUnknownYaml()
    {
        Dictionary<object, object>? root = ComposeYamlHelper.ParseMapping("""
            services:
              API:
                image: example/api:2
                x-company-setting: keep-me
            """);

        string serviceYaml = ComposeYamlHelper.GetServiceYaml(root, "api");

        Assert.Contains("image: example/api:2", serviceYaml, StringComparison.Ordinal);
        Assert.Contains("x-company-setting: keep-me", serviceYaml, StringComparison.Ordinal);
    }
}
