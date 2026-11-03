// Unhwp is available where its native library loads: from its NuGet package (win-x64, linux-x64, macOS) or from
// Filee's own build of the same release (linux-arm64, win-arm64; build/build-native.py, -p:UnhwpNativeLibrary).

using System.Reflection;
using Filee.Engines.Hwp;
using Filee.Engines.Infrastructure;
using Unhwp;

namespace Filee.Engines.Tests;

public class UnhwpNativeTests
{
    /// <summary>The library this build was given with -p:UnhwpNativeLibrary, or null.</summary>
    private static string? BuiltWith =>
        typeof(UnhwpNativeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "UnhwpNativeLibrary")?.Value is { Length: > 0 } path ? path : null;

    [Fact]
    public void Status_follows_whether_the_native_library_loads()
    {
        var status = new UnhwpConverter().GetStatus();
        if (EngineDownloads.RuntimeIdentifier is "win-x64" or "linux-x64" or "osx-x64" or "osx-arm64")
            Assert.True(status.IsAvailable, "Unhwp's package ships the library for this platform.");
        if (BuiltWith is { } library)
            Assert.True(status.IsAvailable, $"This build copied {library} next to the app, but it doesn't load.");

        if (status.IsAvailable)
        {
            // A self-built library must be the release the managed binding comes from.
            var managed = typeof(UnhwpDocument).Assembly.GetName().Version!;
            Assert.Equal($"{managed.Major}.{managed.Minor}.{managed.Build}", UnhwpDocument.Version);
        }
        else
        {
            Assert.Equal("engine.reason.unsupported_platform", status.ReasonKey);
        }
    }

    [Fact]
    public void A_shipped_library_that_does_not_load_counts_as_shipped()
    {
        var empty = Directory.CreateTempSubdirectory("filee-unhwp-").FullName;
        var shipped = Directory.CreateTempSubdirectory("filee-unhwp-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(shipped, "unhwp.dll"), "not a library");
            Assert.False(UnhwpConverter.NativeLibraryShipped([]));
            Assert.False(UnhwpConverter.NativeLibraryShipped([empty]));
            Assert.True(UnhwpConverter.NativeLibraryShipped([empty, shipped]));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
            Directory.Delete(shipped, recursive: true);
        }
    }
}
