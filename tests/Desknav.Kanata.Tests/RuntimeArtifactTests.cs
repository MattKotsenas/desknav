using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Desknav.Kanata.Tests;

public sealed class RuntimeArtifactTests
{
    [Fact]
    public void ArtifactPinsKanataAndContainsTestedConfiguration()
    {
        var repositoryRoot = FindRepositoryRoot();
        var packageDirectory = Path.Combine(
            repositoryRoot,
            "artifacts",
            "package");
        var artifactPath = Assert.Single(
            Directory.GetFiles(packageDirectory, "*.zip"));
        var artifactName = Path.GetFileName(artifactPath);

        var sourceManifest = File.ReadAllBytes(
            Path.Combine(
                repositoryRoot,
                "src",
                "Desknav.Kanata",
                "runtime.json"));
        using var manifest = JsonDocument.Parse(sourceManifest);
        var configurationName = manifest.RootElement
            .GetProperty("configuration")
            .GetString();
        Assert.NotNull(configurationName);

        using var archive = ZipFile.OpenRead(artifactPath);
        Assert.Equal(
            new[] { configurationName, "runtime.json" }
                .Order(StringComparer.Ordinal),
            archive.Entries
                .Select(entry => entry.FullName)
                .Order(StringComparer.Ordinal));

        var packagedConfiguration = ReadEntry(archive, configurationName);
        var sourceConfiguration = File.ReadAllBytes(
            Path.Combine(
                repositoryRoot,
                "src",
                "Desknav.Kanata",
                configurationName));
        Assert.Equal(sourceConfiguration, packagedConfiguration);

        var packagedManifest = ReadEntry(archive, "runtime.json");
        Assert.Equal(sourceManifest, packagedManifest);

        var actualHash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(artifactPath)))
            .ToLowerInvariant();
        var checksum = File.ReadAllText($"{artifactPath}.sha256")
            .TrimEnd('\r', '\n');
        Assert.Equal($"{actualHash}  {artifactName}", checksum);
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);

        using var stream = entry.Open();
        using var content = new MemoryStream();
        stream.CopyTo(content);
        return content.ToArray();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "desknav.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the Desknav repository root.");
    }
}
