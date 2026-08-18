namespace Factory.Tests;

/// <summary>Installs the built fixture plugin into a plugins directory. The tests load a real
/// assembly rather than a mock: assembly loading is the part most likely to break.</summary>
internal static class PluginFixture
{
    public const string FileName = "Factory.TestPlugin.dll";

    public static void InstallInto(string pluginsDir)
    {
        Directory.CreateDirectory(pluginsDir);
        File.Copy(BuiltPluginPath(), Path.Combine(pluginsDir, FileName), overwrite: true);
    }

    /// <summary>Installs the plugin alongside a copy of <c>Factory.Core</c>, as a third-party
    /// plugin that packaged the contract assembly would ship. That sibling copy is what the
    /// load context has to refuse.</summary>
    public static void InstallWithContractAssemblyInto(string pluginsDir)
    {
        InstallInto(pluginsDir);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Factory.Core.dll"),
            Path.Combine(pluginsDir, "Factory.Core.dll"),
            overwrite: true);
    }

    private static string BuiltPluginPath()
    {
        // The test binary runs from bin/<configuration>/<framework>; the fixture builds to its own.
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var source = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "Factory.TestPlugin",
            "bin", output.Parent!.Name, output.Name, FileName));

        if (!File.Exists(source))
            throw new InvalidOperationException(
                $"Fixture plugin not built: {source}. Run 'dotnet build' at the solution root first.");

        return source;
    }
}
