using DockerDiagram.Contracts;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DockerDiagram.Infrastructure
{
    public partial class DockerApiService
    {
        // =========================================================
        // 3. IImageService 구현
        // =========================================================

        /// <summary>
        /// 다운로드되어 있는 로컬 도커 이미지 목록을 가져오며, 하나의 이미지가 여러 태그를 가졌을 경우 분리하여 반환합니다.
        /// </summary>
        public async Task<List<DockerImage>> GetImagesAsync()
        {
            var images = await _client.Images.ListImagesAsync(new ImagesListParameters { All = false });
            var result = new List<DockerImage>();

            foreach (var img in images)
            {
                // 이미지의 각 repository:tag 조합을 별도 항목으로 표시합니다.
                if (img.RepoTags != null && img.RepoTags.Count > 0)
                {
                    foreach (var repoTag in img.RepoTags)
                    {
                        int lastColonIndex = repoTag.LastIndexOf(':');
                        string repository = lastColonIndex > 0 ? repoTag.Substring(0, lastColonIndex) : repoTag;
                        string tag = lastColonIndex > 0 ? repoTag.Substring(lastColonIndex + 1) : "<none>";

                        result.Add(new DockerImage
                        {
                            Id = repoTag,
                            Repository = repository,
                            Tag = tag,
                            Size = img.Size
                        });
                    }
                }
                else
                {
                    // 태그가 벗겨진 진짜 좀비 이미지 (<none>:<none>)
                    result.Add(new DockerImage
                    {
                        Id = img.ID, // 태그가 없으므로 도커 고유의 해시 ID(sha256)를 발급
                        Repository = "<none>",
                        Tag = "<none>",
                        Size = img.Size
                    });
                }
            }
            return result;
        }

        // =========================================================
        // 4. IVolumeService 구현
        // =========================================================

        /// <summary>
        /// 도커 엔진에 생성된 물리적 볼륨(Volume) 목록을 조회합니다.
        /// </summary>
        public async Task<List<DockerVolume>> GetVolumesAsync()
        {
            var volumes = await _client.Volumes.ListAsync();
            return volumes.Volumes.Select(v =>
            {
                var labels = CopyLabels(v.Labels);
                return new DockerVolume
                {
                    Name = v.Name,
                    Id = v.Name,
                    Labels = labels,
                    ComposeProjectName = FirstNonEmpty(
                        GetLabel(labels, "com.docker.compose.project"),
                        GetLabel(labels, "com.dockerdiagram.project")),
                    ComposeResourceName = FirstNonEmpty(
                        GetLabel(labels, "com.docker.compose.volume"),
                        GetLabel(labels, "com.dockerdiagram.resource"),
                        v.Name),
                    ProjectSource = string.IsNullOrWhiteSpace(GetLabel(labels, "com.docker.compose.project"))
                        ? (string.IsNullOrWhiteSpace(GetLabel(labels, "com.dockerdiagram.project")) ? string.Empty : "Template")
                        : "Compose"
                };
            }).ToList();
        }

        // =========================================================
        // 5. INetworkService 구현
        // =========================================================

        /// <summary>
        /// 도커 엔진에 생성된 가상 네트워크 그룹 목록을 조회합니다.
        /// </summary>
        public async Task<List<DockerNetworkGroup>> GetNetworksAsync() // 반환 타입 변경!
        {
            var networks = await _client.Networks.ListNetworksAsync();
            return networks.Select(n =>
            {
                var labels = CopyLabels(n.Labels);
                return new DockerNetworkGroup
                {
                    Name = n.Name,
                    Id = n.ID,
                    Driver = n.Driver,
                    Labels = labels,
                    ComposeProjectName = FirstNonEmpty(
                        GetLabel(labels, "com.docker.compose.project"),
                        GetLabel(labels, "com.dockerdiagram.project")),
                    ComposeResourceName = FirstNonEmpty(
                        GetLabel(labels, "com.docker.compose.network"),
                        GetLabel(labels, "com.dockerdiagram.resource"),
                        n.Name),
                    ProjectSource = string.IsNullOrWhiteSpace(GetLabel(labels, "com.docker.compose.project"))
                        ? (string.IsNullOrWhiteSpace(GetLabel(labels, "com.dockerdiagram.project")) ? string.Empty : "Template")
                        : "Compose"
                };
            }).ToList();
        }

        private static Dictionary<string, string> CopyLabels(IDictionary<string, string>? labels)
        {
            return labels == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(labels, StringComparer.OrdinalIgnoreCase);
        }

        private static string GetLabel(IReadOnlyDictionary<string, string> labels, string key)
        {
            return labels.TryGetValue(key, out string? value) ? value ?? string.Empty : string.Empty;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        private static int ParseComposeContainerNumber(string value)
        {
            return int.TryParse(value, out int number) && number > 0 ? number : 0;
        }
    }
}
