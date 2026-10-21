using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace Jellyfin.SemanticDiscovery.Plugin;

public static class NativeOnnxRuntimeResolver
{
    private static readonly Lock Sync = new();
    private static bool _installed;

    public static void Install()
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(
                typeof(InferenceSession).Assembly,
                Resolve);
            _installed = true;
        }
    }

    private static nint Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        string? fileName = GetFileName(libraryName);
        if (fileName is null)
        {
            return nint.Zero;
        }

        string assemblyDirectory =
            Path.GetDirectoryName(typeof(NativeOnnxRuntimeResolver).Assembly.Location)
            ?? AppContext.BaseDirectory;
        string path = GetRequiredPath(libraryName, assemblyDirectory);
        return NativeLibrary.Load(path, assembly, searchPath);
    }

    internal static string? GetFileName(string libraryName)
    {
        if (libraryName.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return null;
        }

        string normalized = libraryName;
        if (normalized.StartsWith("lib", StringComparison.Ordinal))
        {
            normalized = normalized[3..];
        }
        foreach (string suffix in new[] { ".dll", ".so", ".dylib" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }
        if (normalized is not ("onnxruntime" or "onnxruntime_providers_shared"))
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? libraryName
                : $"{libraryName}.dll";
        }

        if (OperatingSystem.IsLinux())
        {
            string fileName = libraryName.StartsWith("lib", StringComparison.Ordinal)
                ? libraryName
                : $"lib{libraryName}";
            return fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : $"{fileName}.so";
        }

        if (OperatingSystem.IsMacOS())
        {
            string fileName = libraryName.StartsWith("lib", StringComparison.Ordinal)
                ? libraryName
                : $"lib{libraryName}";
            return fileName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : $"{fileName}.dylib";
        }

        return null;
    }

    internal static string GetRequiredPath(
        string libraryName,
        string assemblyDirectory)
    {
        string fileName = GetFileName(libraryName) ??
            throw new ArgumentException(
                "The native import is not an expected ONNX Runtime library.",
                nameof(libraryName));
        string path = Path.Combine(
            Path.GetFullPath(assemblyDirectory),
            fileName);
        return File.Exists(path)
            ? path
            : throw new DllNotFoundException(
                $"Required plugin-local ONNX Runtime library '{fileName}' was not found.");
    }
}
