using System;
using System.Collections.Generic;
using System.IO;
using DockerDiagram.Common;

namespace DockerDiagram.ApplicationServices
{
    public static class VolumeUndoBackupStore
    {
        private static readonly HashSet<string> ActiveBackupFiles = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Sync = new();
        public static string BackupDirectory => Path.Combine(Path.GetTempPath(), "DockerDiagramVolumeUndo");

        public static string CreateBackupPath(string volumeName)
        {
            Directory.CreateDirectory(BackupDirectory);
            string safeName = string.Join("_", volumeName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            string path = Path.Combine(BackupDirectory, $"{safeName}_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}.tar");
            lock (Sync)
                ActiveBackupFiles.Add(path);
            return path;
        }

        public static void Forget(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            lock (Sync)
                ActiveBackupFiles.Remove(path);
        }

        public static void DeleteFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception cleanupException)
            {
                AppLog.Warning($"볼륨 실행 취소 백업 파일 정리 실패: {path}", cleanupException);
            }
            finally
            {
                lock (Sync)
                    ActiveBackupFiles.Remove(path);
            }
        }

        public static void CleanupActiveBackups()
        {
            string[] paths;
            lock (Sync)
                paths = ActiveBackupFiles.ToArray();

            foreach (var path in paths)
                DeleteFile(path);

            DeleteDirectoryIfEmpty();
        }

        public static void CleanupOrphanBackups()
        {
            try
            {
                if (Directory.Exists(BackupDirectory))
                    Directory.Delete(BackupDirectory, recursive: true);
            }
            catch (Exception cleanupException)
            {
                AppLog.Warning("이전 실행의 볼륨 백업 폴더 정리 실패", cleanupException);
            }
        }

        private static void DeleteDirectoryIfEmpty()
        {
            try
            {
                if (Directory.Exists(BackupDirectory) && Directory.GetFiles(BackupDirectory).Length == 0)
                    Directory.Delete(BackupDirectory);
            }
            catch (Exception cleanupException)
            {
                AppLog.Warning("빈 볼륨 백업 폴더 정리 실패", cleanupException);
            }
        }
    }
}
