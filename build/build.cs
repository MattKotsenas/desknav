const string installTarget = "Install-Kanata-Simulator";
const string kanataV1_11_0Revision =
    "4e6bec4d52d044bd13cfa01cea4e02dc2d246c65";

var target = Argument("target", installTarget);

Task(installTarget)
    .Does(() =>
{
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
                .Append(kanataV1_11_0Revision)
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

RunTarget(target);