using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DockerDiagram.ApplicationServices
{
    /// <summary>
    /// 새 Docker 리소스의 이름 충돌을 확인하고 사용자가 승인한 대체 이름을 계산합니다.
    /// </summary>
    public sealed class ResourceCreationNameService
    {
        private readonly IDialogService _dialogService;

        public ResourceCreationNameService(IDialogService dialogService)
        {
            _dialogService = dialogService;
        }

        public async Task<string?> ResolveContainerNameAsync(
            SheetViewModel sheet,
            IContainerService containerService,
            string requestedName)
        {
            var usedNames = sheet.Nodes
                .Where(node => node.Type == NodeType.Container)
                .Select(node => GetPendingResourceName(node.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            try
            {
                var containers = await containerService.GetContainersAsync();
                foreach (var container in containers)
                {
                    if (!string.IsNullOrWhiteSpace(container.Name))
                        usedNames.Add(container.Name.TrimStart('/'));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NameConflict] Container name scan failed: {ex.Message}");
            }

            return ConfirmUniqueResourceName(requestedName, "컨테이너", usedNames);
        }

        public async Task<(string DisplayName, string DockerName)?> ResolveVolumeNamesAsync(
            SheetViewModel sheet,
            IVolumeService volumeService,
            string requestedDisplayName,
            string requestedDockerName,
            bool external)
        {
            var usedDisplayNames = sheet.Nodes
                .Where(node => node.Type == NodeType.Volume)
                .Select(node => GetPendingResourceName(node.Name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (external)
            {
                string? displayName = ConfirmUniqueResourceName(
                    requestedDisplayName,
                    "볼륨 표시 이름",
                    usedDisplayNames);
                return displayName == null ? null : (displayName, requestedDockerName);
            }

            var usedDockerNames = sheet.Nodes
                .Where(node => node.Type == NodeType.Volume)
                .Select(node => node.EffectiveVolumeName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            try
            {
                var volumes = await volumeService.GetVolumesAsync();
                foreach (var volume in volumes)
                {
                    if (!string.IsNullOrWhiteSpace(volume.Name))
                        usedDockerNames.Add(volume.Name);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NameConflict] Volume name scan failed: {ex.Message}");
            }

            bool displayConflict = usedDisplayNames.Contains(requestedDisplayName);
            bool dockerConflict = usedDockerNames.Contains(requestedDockerName);
            if (!displayConflict && !dockerConflict)
                return (requestedDisplayName, requestedDockerName);

            string displayBase = StripNumericSuffix(requestedDisplayName, out int displayStart);
            string dockerBase = StripNumericSuffix(requestedDockerName, out int dockerStart);
            int suffix = Math.Max(displayStart, dockerStart);
            string displayCandidate;
            string dockerCandidate;
            do
            {
                displayCandidate = $"{displayBase}_{suffix}";
                dockerCandidate = $"{dockerBase}_{suffix}";
                suffix++;
            }
            while (usedDisplayNames.Contains(displayCandidate) || usedDockerNames.Contains(dockerCandidate));

            string conflictDescription = dockerConflict &&
                                         !string.Equals(requestedDisplayName, requestedDockerName, StringComparison.OrdinalIgnoreCase)
                ? $"실제 Docker 볼륨 이름 '{requestedDockerName}'이(가) 이미 존재합니다."
                : $"'{requestedDisplayName}' 이름의 볼륨이 이미 존재합니다.";
            string candidateDescription = string.Equals(displayCandidate, dockerCandidate, StringComparison.Ordinal)
                ? $"'{displayCandidate}' 이름"
                : $"표시 이름 '{displayCandidate}', Docker 이름 '{dockerCandidate}'";

            bool confirmed = _dialogService.ShowConfirm(
                $"{conflictDescription}\n\n{candidateDescription}으로 추가 생성하시겠습니까?",
                "볼륨 이름 중복");

            return confirmed ? (displayCandidate, dockerCandidate) : null;
        }

        public async Task<string?> ResolveNetworkNameAsync(
            SheetViewModel sheet,
            INetworkService networkService,
            string requestedName,
            bool external)
        {
            var usedNames = sheet.Groups
                .Where(group => group.Type == GroupType.Network)
                .Select(group => group.Title)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!external)
            {
                try
                {
                    var networks = await networkService.GetNetworksAsync();
                    foreach (var network in networks)
                    {
                        if (!string.IsNullOrWhiteSpace(network.Name))
                            usedNames.Add(network.Name);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NameConflict] Network name scan failed: {ex.Message}");
                }
            }

            return ConfirmUniqueResourceName(
                requestedName,
                external ? "네트워크 표시 이름" : "네트워크",
                usedNames);
        }

        private string? ConfirmUniqueResourceName(
            string requestedName,
            string resourceLabel,
            ISet<string> usedNames)
        {
            string normalizedName = requestedName.Trim();
            if (!usedNames.Contains(normalizedName)) return normalizedName;

            string candidate = FindAvailableResourceName(normalizedName, usedNames);
            bool confirmed = _dialogService.ShowConfirm(
                $"같은 {resourceLabel}이(가) 이미 존재합니다.\n\n" +
                $"현재 이름: {normalizedName}\n추가 생성 이름: {candidate}\n\n계속하시겠습니까?",
                $"{resourceLabel} 이름 중복");
            return confirmed ? candidate : null;
        }

        private static string FindAvailableResourceName(string requestedName, ISet<string> usedNames)
        {
            string baseName = StripNumericSuffix(requestedName, out int suffix);
            string candidate;
            do
            {
                candidate = $"{baseName}_{suffix}";
                suffix++;
            }
            while (usedNames.Contains(candidate));

            return candidate;
        }

        private static string StripNumericSuffix(string name, out int nextSuffix)
        {
            var match = System.Text.RegularExpressions.Regex.Match(name, @"^(.*)_(\d+)$");
            if (match.Success &&
                int.TryParse(match.Groups[2].Value, out int currentSuffix) &&
                currentSuffix < int.MaxValue)
            {
                nextSuffix = currentSuffix + 1;
                return match.Groups[1].Value;
            }

            nextSuffix = 1;
            return name;
        }

        private static string GetPendingResourceName(string name)
        {
            const string creatingSuffix = " (Creating...)";
            return name.EndsWith(creatingSuffix, StringComparison.Ordinal)
                ? name[..^creatingSuffix.Length]
                : name;
        }
    }
}
