using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmServiceTopologyProjectionReaderTests
{
    [Fact]
    public void Read_ProjectsSecretAndConfigFileReferencesWithoutPayloads()
    {
        const string raw = """
        {
          "ID": "service-id",
          "Version": { "Index": 7 },
          "Spec": {
            "Name": "web",
            "TaskTemplate": {
              "ContainerSpec": {
                "Image": "nginx:latest",
                "Secrets": [{
                  "SecretID": "secret-id",
                  "SecretName": "db_password",
                  "File": { "Name": "/run/secrets/db", "UID": "1000", "GID": "1000", "Mode": 256 }
                }],
                "Configs": [{
                  "ConfigID": "config-id",
                  "ConfigName": "nginx_conf",
                  "File": { "Name": "/etc/nginx/nginx.conf", "UID": "0", "GID": "0", "Mode": 292 }
                }]
              }
            },
            "Mode": { "Replicated": { "Replicas": 1 } }
          }
        }
        """;
        SwarmServiceEditSnapshot snapshot = SwarmServiceSpecReader.Read(Newtonsoft.Json.Linq.JObject.Parse(raw));

        SwarmServiceTopologyProjection result = SwarmServiceTopologyProjectionReader.Read(snapshot);

        Assert.Equal(2, result.DataReferences.Count);
        Assert.Contains(result.DataReferences, reference =>
            reference.Kind == SwarmDataResourceKind.Secret &&
            reference.ResourceId == "secret-id" &&
            reference.FileName == "/run/secrets/db" &&
            reference.Mode == 256U);
        Assert.Contains(result.DataReferences, reference =>
            reference.Kind == SwarmDataResourceKind.Config &&
            reference.ResourceName == "nginx_conf");
    }
}
