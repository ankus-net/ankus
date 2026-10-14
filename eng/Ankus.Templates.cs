#:property TargetFramework=net10.0
#:property PackAsTool=false
#:property PublishAot=false

using System.Text;

if (args.Length != 4)
{
    throw new ArgumentException("Specify the shared template root, configuration, staging directory and package version.");
}

string sourceRoot = Path.GetFullPath(args[0]);
string configuration = File.ReadAllText(args[1]);
string hostConfiguration = File.ReadAllText(Path.Join(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "dotnetcli.host.json"));
string destination = Path.GetFullPath(args[2]);
string version = args[3];
Directory.CreateDirectory(destination);
bool[] variants = [false, true];
foreach (bool worker in variants)
{
    string name = worker ? "ankus-worker" : "ankus";
    string root = Path.Join(destination, "content", name);
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }

    string[] sources = worker
        ? ["Extension", "BackgroundWorker", "Frameworks"]
        : ["Extension", "Frameworks"];
    foreach (string source in sources)
    {
        string directory = Path.Join(sourceRoot, source);
        foreach (string path in Directory.EnumerateFiles(directory, "*.template", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, path)[..^".template".Length];
            if (source == "Frameworks")
            {
                relative = Path.Join("Frameworks", relative);
            }

            string target = Path.Join(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string text = File.ReadAllText(path)
                .Replace("__VERSION__", version, StringComparison.Ordinal)
                .Replace("__PRELOAD__", worker ? "true" : "false", StringComparison.Ordinal)
                .Replace("__WORKER_STATE__", "ankus.__WORKER_ID__", StringComparison.Ordinal);
            File.WriteAllText(target, text, new UTF8Encoding(false));
        }
    }

    string configDirectory = Path.Join(root, ".template.config");
    Directory.CreateDirectory(configDirectory);
    File.WriteAllText(Path.Join(configDirectory, "template.json"), configuration
        .Replace("__SHORT_NAME__", name, StringComparison.Ordinal)
        .Replace("__IDENTITY__", worker ? "BackgroundWorker" : "Extension", StringComparison.Ordinal)
        .Replace("__WORKER__", worker ? "true" : "false", StringComparison.Ordinal)
        .Replace("__DISPLAY_NAME__", worker ? "Ankus PostgreSQL background worker" : "Ankus PostgreSQL extension", StringComparison.Ordinal),
        new UTF8Encoding(false));
    File.WriteAllText(Path.Join(configDirectory, "dotnetcli.host.json"), hostConfiguration, new UTF8Encoding(false));
}

File.WriteAllText(Path.Join(destination, "complete.txt"), version, new UTF8Encoding(false));
