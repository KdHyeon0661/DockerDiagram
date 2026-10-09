using DockerDiagram.ApplicationServices;

namespace DockerDiagram.Tests;

public sealed class ComposeYamlHelperTests
{
    [Fact]
    public void ParseMapping_InvalidYamlReturnsNull()
    {
        Assert.Null(ComposeYamlHelper.ParseMapping("services: [unterminated"));
    }

    [Fact]
    public void EnvironmentMapping_ConvertsValuesAndNullEntries()
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
    public void LongPortSyntax_ConvertsToDockerDisplayForm()
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

        Assert.Equal(new[] { "127.0.0.1:8080:80/tcp" }, ports);
    }

    [Fact]
    public void VolumeSyntax_HandlesShortModeAndLongForm()
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

        Assert.Equal(
            new[]
            {
                new VolumeMountInfo("data", "/var/lib/app"),
                new VolumeMountInfo("./config", "/etc/app")
            },
            mounts);
    }

    [Fact]
    public void NetworkOptions_PreserveAddressesAliasesAndDriverOptions()
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
        Assert.Equal(new[] { "api", "internal-api" }, options.Aliases);
        Assert.Equal("enabled", options.DriverOptions["com.example.option"]);
    }

    [Fact]
    public void ServiceLookup_IsCaseInsensitiveAndPreservesUnknownYaml()
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
