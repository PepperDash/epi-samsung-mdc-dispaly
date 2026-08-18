using System.Reflection;
using System.Text.Json;

namespace PepperDashPluginSamsungMdcDisplay.Tests;

public static class AssemblyFixture
{
    private static readonly Lazy<MetadataLoadContext> LazyContext = new(CreateContext);
    private static readonly Lazy<Assembly> LazyAssembly = new(LoadPluginAssembly);

    static AssemblyFixture()
    {
        // MetadataLoadContext is IDisposable; release file handles (and free the plugin DLLs for
        // rebuilds) when the test process exits.
        System.AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (LazyContext.IsValueCreated)
                LazyContext.Value.Dispose();
        };
    }

    private static string Configuration
    {
        get
        {
            // Derive from test output path: tests/bin/{Configuration}/net8.0/
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var parts = baseDir.Split(Path.DirectorySeparatorChar);
            return parts[^2]; // net8.0 is last, Configuration is second-to-last
        }
    }

    // This plugin outputs to src/4Series/bin/{Config}/net8/ (OutputPath = 4Series\bin\$(Configuration)\).
    private static string PluginDllPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "src", "4Series", "bin", Configuration, "net8",
            "Pepperdash.Essentials.Plugins.Display.Samsung.MDC.dll"));

    private static string PluginOutputDir => Path.GetDirectoryName(PluginDllPath)!;

    public static MetadataLoadContext Context => LazyContext.Value;
    public static Assembly PluginAssembly => LazyAssembly.Value;

    private static MetadataLoadContext CreateContext()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var dllByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Fail clearly if the plugin hasn't been built yet, rather than letting
        // Directory.GetFiles throw a less actionable DirectoryNotFoundException below.
        if (!File.Exists(PluginDllPath))
            throw new FileNotFoundException(
                $"Plugin DLL not found at '{PluginDllPath}'. Build the plugin first.", PluginDllPath);

        // Priority 1: Plugin output dir (correct versions win)
        foreach (var dll in Directory.GetFiles(PluginOutputDir, "*.dll"))
            dllByName[Path.GetFileName(dll)] = dll;

        // Priority 2: .NET runtime
        foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
            dllByName.TryAdd(Path.GetFileName(dll), dll);

        // Priority 3: project.assets.json resolution for transitive packages.
        // NOT deps.json - the plugin csproj uses <ExcludeAssets>runtime</ExcludeAssets> on the
        // PepperDashEssentials reference (Essentials provides those DLLs on the processor at
        // runtime), so the plugin's own deps.json omits every dependency entirely and this
        // resolver would find nothing. project.assets.json has the full pre-exclusion
        // dependency graph and is present after any restore/build.
        var assetsJsonPath = Path.Combine(SourceDirectory, "obj", "project.assets.json");
        if (File.Exists(assetsJsonPath))
        {
            foreach (var path in ResolveProjectAssetsAssemblies(assetsJsonPath))
                dllByName.TryAdd(Path.GetFileName(path), path);
        }

        return new MetadataLoadContext(new PathAssemblyResolver(dllByName.Values));
    }

    private static IEnumerable<string> ResolveProjectAssetsAssemblies(string assetsJsonPath)
    {
        // Honor NUGET_PACKAGES (common in CI / enterprise setups); fall back to the default.
        var nugetDir = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(nugetDir))
            nugetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");

        using var stream = File.OpenRead(assetsJsonPath);
        using var doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("targets", out var targets))
            yield break;

        // Exactly one TFM per build - take whichever key is present rather than hard-coding one.
        var target = targets.EnumerateObject().FirstOrDefault();
        if (target.Value.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var lib in target.Value.EnumerateObject())
        {
            // Library key is "PackageId/Version"
            var slash = lib.Name.LastIndexOf('/');
            if (slash < 0) continue;
            var packageId = lib.Name[..slash].ToLowerInvariant();
            var version = lib.Name[(slash + 1)..];

            if (!lib.Value.TryGetProperty("compile", out var assets) &&
                !lib.Value.TryGetProperty("runtime", out assets))
                continue;

            foreach (var asset in assets.EnumerateObject())
            {
                // "_._" is NuGet's placeholder for "no asset of this kind" - skip it.
                if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dllPath = Path.Combine(nugetDir, packageId, version,
                    asset.Name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(dllPath))
                    yield return dllPath;
            }
        }
    }

    private static Assembly LoadPluginAssembly()
    {
        if (!File.Exists(PluginDllPath))
            throw new FileNotFoundException(
                $"Plugin DLL not found at '{PluginDllPath}'. Build the plugin first.", PluginDllPath);
        return Context.LoadFromAssemblyPath(PluginDllPath);
    }

    public static List<Type> FindFactoryTypes(string baseTypePrefix = "EssentialsPluginDeviceFactory")
    {
        return PluginAssembly.GetTypes()
            .Where(t => !t.IsAbstract
                && t.BaseType is { IsGenericType: true }
                && t.BaseType.GetGenericTypeDefinition().Name.StartsWith(baseTypePrefix))
            .ToList();
    }

    public static string SourceDirectory =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "src"));
}
