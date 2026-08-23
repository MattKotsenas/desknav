const string installTarget = "Install-Kanata-Simulator";
const string verifyRuntimeTarget = "Verify-Kanata-Runtime";
const string packageRuntimeTarget = "Package-Runtime";
const string testTarget = "Test";
const string allTarget = "All";
const string runtimeArtifactName = "desknav-kanata-runtime.zip";
const string runtimeConfigurationPath =
    "./src/Desknav.Kanata/desknav.kbd";
const string runtimeManifestPath =
    "./src/Desknav.Kanata/runtime.json";

var target = Argument("target", allTarget);

Task(installTarget)
    .Does(() =>
{
    var runtime = ReadRuntimeManifest(runtimeManifestPath);
    var destination = MakeAbsolute(
        Directory("./artifacts/tools/kanata-sim"));
    var exitCode = StartProcess(
        "cargo",
        new ProcessSettings
        {
            Arguments = new ProcessArgumentBuilder()
                .Append("install")
                .Append("--git")
                .Append("https://github.com/jtroo/kanata.git")
                .Append("--rev")
                .Append(runtime.SourceRevision)
                .Append("--locked")
                .Append("--root")
                .AppendQuoted(destination.FullPath)
                .Append("kanata-sim"),
        });

    if (exitCode != 0)
    {
        throw new InvalidOperationException(
            $"Kanata simulator installation failed with exit code {exitCode}.");
    }

    var simulator = destination.CombineWithFilePath(
        "bin/kanata_simulated_input.exe");
    if (!FileExists(simulator))
    {
        throw new InvalidOperationException(
            $"Kanata simulator was not installed at '{simulator}'.");
    }

    Information("Kanata simulator: {0}", simulator);
});

Task(verifyRuntimeTarget)
    .Does(() =>
{
    var runtime = ReadRuntimeManifest(runtimeManifestPath);
    var archive = MakeAbsolute(
        File($"./artifacts/tools/kanata-runtime/{runtime.AssetName}"));
    EnsureDirectoryExists(archive.GetDirectory());

    if (FileExists(archive)
        && CalculateSha256(archive.FullPath) != runtime.Sha256)
    {
        DeleteFile(archive);
    }

    if (!FileExists(archive))
    {
        var exitCode = StartProcess(
            "curl.exe",
            new ProcessSettings
            {
                Arguments = new ProcessArgumentBuilder()
                    .Append("--fail")
                    .Append("--location")
                    .Append("--silent")
                    .Append("--show-error")
                    .Append("--output")
                    .AppendQuoted(archive.FullPath)
                    .AppendQuoted(runtime.Uri),
            });

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Kanata runtime download failed with exit code {exitCode}.");
        }
    }

    var actualHash = CalculateSha256(archive.FullPath);
    if (actualHash != runtime.Sha256)
    {
        throw new InvalidOperationException(
            $"Kanata runtime SHA-256 mismatch. Expected "
            + $"'{runtime.Sha256}', got '{actualHash}'.");
    }

    using var kanataArchive =
        System.IO.Compression.ZipFile.OpenRead(archive.FullPath);
    if (kanataArchive.GetEntry(runtime.Executable) is null)
    {
        throw new InvalidOperationException(
            $"Kanata runtime archive does not contain "
            + $"'{runtime.Executable}'.");
    }

    Information("Kanata runtime: {0}", archive);
});

Task(packageRuntimeTarget)
    .IsDependentOn(verifyRuntimeTarget)
    .Does(() =>
{
    var runtime = ReadRuntimeManifest(runtimeManifestPath);
    var sourceConfigurationName =
        System.IO.Path.GetFileName(runtimeConfigurationPath);
    if (runtime.Configuration != sourceConfigurationName)
    {
        throw new InvalidOperationException(
            $"Runtime configuration '{runtime.Configuration}' does not match "
            + $"source file '{sourceConfigurationName}'.");
    }

    var packageDirectory = MakeAbsolute(Directory("./artifacts/package"));
    EnsureDirectoryExists(packageDirectory);
    CleanDirectory(packageDirectory);

    var artifact = packageDirectory.CombineWithFilePath(runtimeArtifactName);
    CreateRuntimeArtifact(artifact.FullPath, runtime.Configuration);
    var artifactHash = CalculateSha256(artifact.FullPath);
    System.IO.File.WriteAllText(
        $"{artifact.FullPath}.sha256",
        $"{artifactHash}  {runtimeArtifactName}\n",
        new System.Text.UTF8Encoding(false));

    Information("Runtime artifact: {0}", artifact);
});

Task(testTarget)
    .IsDependentOn(installTarget)
    .IsDependentOn(packageRuntimeTarget)
    .Does(() =>
{
    var exitCode = StartProcess(
        "dotnet",
        new ProcessSettings
        {
            Arguments = new ProcessArgumentBuilder()
                .Append("test")
                .Append("--solution")
                .AppendQuoted("desknav.slnx")
                .Append("--configuration")
                .Append("Release"),
        });

    if (exitCode != 0)
    {
        throw new InvalidOperationException(
            $"Tests failed with exit code {exitCode}.");
    }
});

Task(allTarget)
    .IsDependentOn(testTarget);

RunTarget(target);

void CreateRuntimeArtifact(string outputPath, string configurationName)
{
    using var output = System.IO.File.Create(outputPath);
    using var archive = new System.IO.Compression.ZipArchive(
        output,
        System.IO.Compression.ZipArchiveMode.Create);

    AddRuntimeEntry(archive, "runtime.json", runtimeManifestPath);
    AddRuntimeEntry(archive, configurationName, runtimeConfigurationPath);
}

void AddRuntimeEntry(
    System.IO.Compression.ZipArchive archive,
    string entryName,
    string sourcePath)
{
    var entry = archive.CreateEntry(
        entryName,
        System.IO.Compression.CompressionLevel.Optimal);
    entry.LastWriteTime = new DateTimeOffset(
        1980,
        1,
        1,
        0,
        0,
        0,
        TimeSpan.Zero);

    using var source = System.IO.File.OpenRead(sourcePath);
    using var destination = entry.Open();
    source.CopyTo(destination);
}

string CalculateSha256(string path)
{
    using var stream = System.IO.File.OpenRead(path);
    return Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(stream))
        .ToLowerInvariant();
}

(string Configuration, string SourceRevision, string Uri, string AssetName,
    string Sha256, string Executable)
    ReadRuntimeManifest(string path)
{
    using var document = System.Text.Json.JsonDocument.Parse(
        System.IO.File.ReadAllBytes(path));
    var root = document.RootElement;
    var configuration = root.GetProperty("configuration").GetString()
        ?? throw new InvalidOperationException(
            "Runtime configuration is required.");
    var kanata = root.GetProperty("kanata");
    var sourceRevision = kanata.GetProperty("sourceRevision").GetString()
        ?? throw new InvalidOperationException(
            "Kanata source revision is required.");
    var uri = kanata.GetProperty("uri").GetString()
        ?? throw new InvalidOperationException(
            "Kanata runtime URI is required.");
    var assetName = System.IO.Path.GetFileName(new Uri(uri).AbsolutePath);
    var sha256 = kanata.GetProperty("sha256").GetString()
        ?? throw new InvalidOperationException(
            "Kanata runtime SHA-256 is required.");
    var executable = kanata.GetProperty("executable").GetString()
        ?? throw new InvalidOperationException(
            "Kanata runtime executable is required.");

    return (
        configuration,
        sourceRevision,
        uri,
        assetName,
        sha256,
        executable);
}