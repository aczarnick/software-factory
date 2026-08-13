using System.Text;

namespace Factory.Runtime;

/// <summary>
/// A bounded summary of a repository for stations that need orientation but not contents.
///
/// This is the context-budgeting mechanism: planning stations get a digest measured in
/// hundreds of tokens instead of a file tree measured in tens of thousands. Only the
/// implementation station, which actually edits code, is given tools to read files — and it
/// reads what it needs rather than being handed everything up front.
/// </summary>
public static class RepoDigest
{
    private static readonly string[] IgnoredDirs =
    [
        ".git", "node_modules", "bin", "obj", "dist", "build", "target", ".factory",
        "__pycache__", ".venv", "venv", ".next", ".nuxt", "vendor", ".gradle", "Pods"
    ];

    private static readonly (string File, string Kind, string Build)[] Signatures =
    [
        ("package.json", "node", "npm test"),
        ("Cargo.toml", "rust", "cargo test"),
        ("go.mod", "go", "go test ./..."),
        ("pyproject.toml", "python", "pytest"),
        ("requirements.txt", "python", "pytest"),
        ("pom.xml", "java/maven", "mvn -q test"),
        ("build.gradle", "java/gradle", "gradle test"),
        ("Gemfile", "ruby", "bundle exec rspec"),
        ("composer.json", "php", "composer test")
    ];

    public static string Build(string repoRoot, int byteCap = 8000)
    {
        var sb = new StringBuilder();

        var kind = DetectKind(repoRoot, out var buildCmd);
        sb.AppendLine($"Project type: {kind}");
        if (buildCmd is not null) sb.AppendLine($"Likely test command: {buildCmd}");

        if (ReadHead(Path.Combine(repoRoot, "README.md"), 15) is { Length: > 0 } readme)
        {
            sb.AppendLine("\nREADME (head):");
            sb.AppendLine(readme);
        }

        sb.AppendLine("\nTree:");
        var budget = Math.Max(500, byteCap - sb.Length - 200);
        sb.Append(Tree(repoRoot, budget));

        var text = sb.ToString();
        return text.Length <= byteCap ? text : text[..byteCap] + "\n… (digest truncated)";
    }

    private static string DetectKind(string root, out string? buildCommand)
    {
        foreach (var (file, kind, build) in Signatures)
        {
            if (File.Exists(Path.Combine(root, file)))
            {
                buildCommand = build;
                return kind;
            }
        }

        if (Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).Any() ||
            Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Take(1).Any())
        {
            buildCommand = "dotnet test";
            return "dotnet";
        }

        buildCommand = null;
        return "unknown";
    }

    private static string? ReadHead(string path, int lines)
    {
        if (!File.Exists(path)) return null;
        try { return string.Join("\n", File.ReadLines(path).Take(lines)); }
        catch (IOException) { return null; }
    }

    private static string Tree(string root, int byteCap, int maxDepth = 3)
    {
        var sb = new StringBuilder();
        Walk(new DirectoryInfo(root), "", 0);
        return sb.ToString();

        void Walk(DirectoryInfo dir, string prefix, int depth)
        {
            if (depth > maxDepth || sb.Length > byteCap) return;

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos()
                    .Where(e => !e.Name.StartsWith('.') || e.Name is ".github")
                    .Where(e => !IgnoredDirs.Contains(e.Name))
                    .OrderBy(e => e is FileInfo)
                    .ThenBy(e => e.Name)
                    .Take(40);
            }
            catch (UnauthorizedAccessException) { return; }

            foreach (var entry in entries)
            {
                if (sb.Length > byteCap) { sb.AppendLine($"{prefix}… (truncated)"); return; }

                if (entry is DirectoryInfo sub)
                {
                    sb.AppendLine($"{prefix}{sub.Name}/");
                    Walk(sub, prefix + "  ", depth + 1);
                }
                else
                {
                    sb.AppendLine($"{prefix}{entry.Name}");
                }
            }
        }
    }
}
