using System;

namespace DockerDiagram.Infrastructure
{
    public static class DockerImageReferenceParser
    {
        public static (string Repository, string Tag) Split(
            string imageReference,
            string defaultTag = "latest")
        {
            string value = imageReference.Trim();
            int lastSlash = value.LastIndexOf('/');
            int lastColon = value.LastIndexOf(':');

            if (lastColon > lastSlash && lastColon < value.Length - 1)
                return (value[..lastColon], value[(lastColon + 1)..]);

            return (value, string.IsNullOrWhiteSpace(defaultTag) ? "latest" : defaultTag);
        }
    }
}
