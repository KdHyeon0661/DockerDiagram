using System.Formats.Tar;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DockerDiagram.Infrastructure
{
    /// <summary>
    /// Dockerfile 빌드 컨텍스트를 .dockerignore 규칙에 맞춰 tar로 만듭니다.
    /// 빌드 폴더 전체를 무조건 전송하지 않아 큰 저장소/상위 폴더가 포함되는 일을 막습니다.
    /// </summary>
    internal static class DockerBuildContextArchive
    {
        public static void Create(
            string contextPath,
            string dockerfileRelativePath,
            string archivePath,
            CancellationToken cancellationToken)
        {
            string root = Path.GetFullPath(contextPath);
            var ignoreMatcher = DockerIgnoreMatcher.Load(root);
            string normalizedDockerfile = NormalizeRelativePath(dockerfileRelativePath);

            using var archive = File.Create(archivePath);
            using var writer = new TarWriter(archive, leaveOpen: false);

            AddDirectoryContents(
                writer,
                root,
                root,
                normalizedDockerfile,
                ignoreMatcher,
                cancellationToken);
        }

        private static void AddDirectoryContents(
            TarWriter writer,
            string root,
            string directory,
            string dockerfileRelativePath,
            DockerIgnoreMatcher ignoreMatcher,
            CancellationToken cancellationToken)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = NormalizeRelativePath(Path.GetRelativePath(root, path));
                FileAttributes attributes = File.GetAttributes(path);
                bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                bool isSymbolicLink = attributes.HasFlag(FileAttributes.ReparsePoint);
                bool requiredBuildFile = relativePath.Equals(dockerfileRelativePath, StringComparison.OrdinalIgnoreCase) ||
                                         relativePath.Equals(".dockerignore", StringComparison.OrdinalIgnoreCase);
                bool ignored = !requiredBuildFile && ignoreMatcher.IsIgnored(relativePath, isDirectory);

                if (!ignored)
                {
                    if (isSymbolicLink)
                    {
                        WriteSymbolicLink(writer, path, relativePath);
                    }
                    else if (isDirectory)
                    {
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, relativePath + "/"));
                    }
                    else
                    {
                        writer.WriteEntry(path, relativePath);
                    }
                }

                // 제외된 디렉터리 안의 ! 재포함 규칙도 적용할 수 있도록 탐색 자체는 계속합니다.
                // 재분석 지점은 링크 자체만 보관하고 따라가지 않아 순환 링크와 컨텍스트 이탈을 막습니다.
                if (isDirectory && !isSymbolicLink)
                {
                    AddDirectoryContents(
                        writer,
                        root,
                        path,
                        dockerfileRelativePath,
                        ignoreMatcher,
                        cancellationToken);
                }
            }
        }

        private static void WriteSymbolicLink(TarWriter writer, string path, string relativePath)
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            string? linkTarget = info.LinkTarget;
            if (string.IsNullOrWhiteSpace(linkTarget))
                return;

            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, relativePath)
            {
                LinkName = linkTarget
            });
        }

        private static string NormalizeRelativePath(string path) =>
            path.Replace('\\', '/').TrimStart('/');

        private sealed class DockerIgnoreMatcher
        {
            private readonly IReadOnlyList<IgnoreRule> _rules;

            private DockerIgnoreMatcher(IReadOnlyList<IgnoreRule> rules) => _rules = rules;

            public static DockerIgnoreMatcher Load(string root)
            {
                string ignoreFile = Path.Combine(root, ".dockerignore");
                if (!File.Exists(ignoreFile))
                    return new DockerIgnoreMatcher([]);

                var rules = new List<IgnoreRule>();
                foreach (string rawLine in File.ReadLines(ignoreFile))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line == "." || line.StartsWith('#'))
                        continue;

                    bool negated = line.StartsWith('!');
                    if (negated)
                        line = line[1..].Trim();
                    if (line.Length == 0)
                        continue;

                    line = line.Replace('\\', '/');
                    bool anchored = line.StartsWith('/');
                    line = line.TrimStart('/').TrimEnd('/');
                    if (line.Length == 0)
                        continue;

                    rules.Add(new IgnoreRule(negated, BuildPattern(line, anchored)));
                }

                return new DockerIgnoreMatcher(rules);
            }

            public bool IsIgnored(string relativePath, bool isDirectory)
            {
                string normalized = NormalizeRelativePath(relativePath).TrimEnd('/');
                bool ignored = false;
                foreach (IgnoreRule rule in _rules)
                {
                    if (rule.Pattern.IsMatch(normalized + (isDirectory ? "/" : string.Empty)))
                        ignored = !rule.Negated;
                }
                return ignored;
            }

            private static Regex BuildPattern(string glob, bool anchored)
            {
                bool containsSlash = glob.Contains('/');
                var expression = new StringBuilder(anchored || containsSlash ? "^" : "(?:^|/)");

                for (int index = 0; index < glob.Length; index++)
                {
                    char current = glob[index];
                    if (current == '*')
                    {
                        bool doubleStar = index + 1 < glob.Length && glob[index + 1] == '*';
                        if (doubleStar)
                        {
                            index++;
                            if (index + 1 < glob.Length && glob[index + 1] == '/')
                            {
                                index++;
                                expression.Append("(?:.*/)?");
                            }
                            else
                            {
                                expression.Append(".*");
                            }
                        }
                        else
                        {
                            expression.Append("[^/]*");
                        }
                    }
                    else if (current == '?')
                    {
                        expression.Append("[^/]");
                    }
                    else
                    {
                        expression.Append(Regex.Escape(current.ToString()));
                    }
                }

                // 파일/디렉터리 자체와 그 디렉터리 아래 항목을 모두 같은 규칙으로 처리합니다.
                expression.Append("(?:$|/.*$)");
                return new Regex(
                    expression.ToString(),
                    RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            }

            private sealed record IgnoreRule(bool Negated, Regex Pattern);
        }
    }
}
