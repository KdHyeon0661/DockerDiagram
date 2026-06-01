using System;
using System.Collections.Generic;
using System.IO;

namespace DockerDiagram.Helpers
{
    public static class VolumeUndoBackupStore
    {
        private static readonly HashSet<string> ActiveBackupFiles = new(StringComparer.OrdinalIgnoreCase);
        public static string BackupDirectory => Path.Combine(Path.GetTempPath(), "DockerDiagramVolumeUndo");

        public static string CreateBackupPath(string volumeName)
        {
            Directory.CreateDirectory(BackupDirectory);
            string safeName = string.Join("_", volumeName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            string path = Path.Combine(BackupDirectory, $"{safeName}_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}.tar");
            ActiveBackupFiles.Add(path);
            return path;
        }

        public static void Forget(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            ActiveBackupFiles.Remove(path);
        }

        public static void DeleteFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Cleanup must never block app shutdown.
            }
            finally
            {
                ActiveBackupFiles.Remove(path);
            }
        }

        public static void CleanupActiveBackups()
        {
            foreach (var path in ActiveBackupFiles.ToArray())
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
            catch
            {
                // A previous crash may leave files locked briefly; ignore and try again on next startup.
            }
        }

        private static void DeleteDirectoryIfEmpty()
        {
            try
            {
                if (Directory.Exists(BackupDirectory) && Directory.GetFiles(BackupDirectory).Length == 0)
                    Directory.Delete(BackupDirectory);
            }
            catch
            {
            }
        }
    }
}
