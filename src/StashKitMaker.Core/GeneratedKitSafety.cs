namespace StashKitMaker.Core;

public static class GeneratedKitSafety
{
    public static bool IsGeneratedKit(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !Directory.Exists(path)) return false;
        return File.Exists(Path.Combine(path, "_metadata", ".stashkitmaker"));
    }
}
