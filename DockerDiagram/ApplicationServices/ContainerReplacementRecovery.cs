using System.IO;
using Docker.DotNet.Models;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.ViewModels;

namespace DockerDiagram.ApplicationServices
{
    internal readonly record struct ContainerRecoveryResult(
        bool KeepBackup,
        string UserMessage);

    /// <summary>
    /// Shared rollback and cleanup policy for mount changes that replace a container.
    /// </summary>
    internal static class ContainerReplacementRecovery
    {
        public static async Task<ContainerRecoveryResult> RecoverAsync(
            NodeViewModel containerNode,
            IContainerService containerService,
            string originalContainerId,
            ContainerInspectResponse? inspect,
            bool originalRemoved,
            bool wasRunning,
            string? replacementId)
        {
            if (originalRemoved && inspect != null)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(replacementId))
                    {
                        try
                        {
                            await containerService.RemoveContainerAsync(replacementId);
                        }
                        catch (Exception cleanupException)
                        {
                            AppLog.Warning(
                                $"실패한 교체 컨테이너 '{replacementId}' 정리 실패",
                                cleanupException);
                        }
                    }

                    string restoredId = await containerService.RecreateContainerFromInspectAsync(
                        inspect.Name,
                        inspect,
                        inspect.HostConfig?.Binds?.ToList() ?? [],
                        wasRunning);
                    containerNode.ContainerId = restoredId;
                    await containerNode.RefreshDetailsAsync();
                    return new ContainerRecoveryResult(
                        KeepBackup: false,
                        UserMessage: "\n원본 컨테이너 설정은 복구했습니다.");
                }
                catch (Exception recoveryException)
                {
                    AppLog.Error("원본 컨테이너 자동 복구 실패", recoveryException);
                    return new ContainerRecoveryResult(
                        KeepBackup: true,
                        UserMessage:
                            $"\n원본 컨테이너 자동 복구도 실패했습니다: " +
                            recoveryException.GetBaseException().Message);
                }
            }

            if (wasRunning)
            {
                try
                {
                    await containerService.StartContainerAsync(originalContainerId);
                }
                catch (Exception restartException)
                {
                    AppLog.Warning(
                        $"원본 컨테이너 '{originalContainerId}' 재시작 실패",
                        restartException);
                    return new ContainerRecoveryResult(
                        KeepBackup: true,
                        UserMessage:
                            $"\n원본 컨테이너 재시작도 실패했습니다: " +
                            restartException.GetBaseException().Message);
                }
            }

            return new ContainerRecoveryResult(KeepBackup: true, UserMessage: string.Empty);
        }

        public static void DeleteTemporaryDirectory(string path)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception cleanupException)
            {
                AppLog.Warning($"임시 백업 폴더 정리 실패: {path}", cleanupException);
            }
        }
    }
}
