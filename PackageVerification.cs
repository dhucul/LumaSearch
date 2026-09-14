using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace LumaSearch;

public static class PackageVerification
{
    public static void Verify(string directory)
    {
        directory = Path.GetFullPath(directory);
        var assembly = Assembly.LoadFile(Path.Combine(directory, "LumaSearch.dll"));
        if (ExecutableManifest.GetExecutionLevel(Path.Combine(directory, "LumaSearch.exe")) != "requireAdministrator")
            throw new InvalidOperationException("The application must request administrator privileges.");
        if (assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration != "Release" ||
            assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true)
            throw new InvalidOperationException("An optimized Release build is required.");
        foreach (string file in new[] { "LumaSearch.exe", "coreclr.dll", "hostfxr.dll", "PresentationFramework.dll" })
            if (!File.Exists(Path.Combine(directory, file))) throw new FileNotFoundException("Incomplete installer payload: " + file);
        if (Directory.EnumerateFiles(directory, "*.pdb", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("Debug symbols cannot be included in the installer payload.");
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "LumaSearch.runtimeconfig.json")));
        if (!runtime.RootElement.GetProperty("runtimeOptions").TryGetProperty("includedFrameworks", out _))
            throw new InvalidOperationException("A self-contained installer payload is required.");
    }
}
