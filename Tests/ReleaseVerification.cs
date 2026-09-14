using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

internal static class ReleaseVerification
{
    internal static void Verify(string publishDirectory)
    {
        string directory = Path.GetFullPath(publishDirectory);
        var assembly = Assembly.LoadFile(Path.Combine(directory, "LumaSearch.dll"));
        string? configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        if (configuration != "Release") throw new InvalidOperationException($"Installer payload is {configuration}, not Release.");
        if (assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true)
            throw new InvalidOperationException("Installer payload has JIT optimization disabled.");
        foreach (string file in new[] { "LumaSearch.exe", "coreclr.dll", "hostfxr.dll", "PresentationFramework.dll" })
            if (!File.Exists(Path.Combine(directory, file))) throw new FileNotFoundException("Incomplete self-contained payload: " + file);
        if (Directory.EnumerateFiles(directory, "*.pdb", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("Debug symbol files were found in the installer payload.");
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "LumaSearch.runtimeconfig.json")));
        if (!runtime.RootElement.GetProperty("runtimeOptions").TryGetProperty("includedFrameworks", out _))
            throw new InvalidOperationException("The installer payload is not self-contained.");
        Console.WriteLine($"Verified Release {assembly.GetName().Version}: optimized, self-contained, no debug symbols.");
    }
}
