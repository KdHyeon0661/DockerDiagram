using System.IO;
using DockerDiagram.Infrastructure;

namespace DockerDiagram.Tests;

public sealed class DockerContextServiceTests
{
    [Fact]
    public void ParseContextInspectReadsEndpointAndTlsMaterial()
    {
        const string json = """
            [
              {
                "Name": "production",
                "Endpoints": {
                  "docker": {
                    "Host": "tcp://docker.example.test:2376",
                    "SkipTLSVerify": false
                  }
                },
                "TLSMaterial": {
                  "docker": ["ca.pem", "cert.pem", "key.pem"]
                },
                "Storage": {
                  "TLSPath": "C:\\docker-contexts\\production"
                }
              }
            ]
            """;

        var context = DockerContextService.ParseContextInspect(json);

        Assert.Equal("production", context.Name);
        Assert.Equal("tcp://docker.example.test:2376", context.DockerEndpoint);
        Assert.True(context.TlsEnabled);
        Assert.False(context.TlsSkipVerify);
        Assert.Equal(3, context.TlsFiles.Count);
        Assert.Equal("C:\\docker-contexts\\production", context.TlsPath);
    }

    [Fact]
    public void ParseContextInspectPlainLocalContextDoesNotEnableTls()
    {
        const string json = """
            [{"Name":"default","Endpoints":{"docker":{"Host":"npipe:////./pipe/docker_engine","SkipTLSVerify":false}},"TLSMaterial":{},"Storage":{}}]
            """;

        var context = DockerContextService.ParseContextInspect(json);

        Assert.False(context.TlsEnabled);
        Assert.Empty(context.TlsFiles);
    }
}
