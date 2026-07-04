namespace Sonulab.Core.Protocol;

public static class SonuCommands
{
    public static string Read(string path) => $"read {path}";
    public static string Browse(string path) => $"browse {path}";
    public static string Write(string path, string json) => $"write {path}:{json}";
    public static string WriteValue(string path, string jsonValue) => $"write {path}:{{\"value\":{jsonValue}}}";
    public static string Save(string path, string name) => $"write {path}:{{\"value\":{JsonString.Quote(name)},\"save\":\"save\"}}";
    public static string DRead(string path, int index, int chunk) => $"dread {path}:{{\"index\":{index},\"chunk\":{chunk}}}";
    public static string DWrite(string path, int index, int chunk, string hex) =>
        $"dwrite {path}:{{\"index\":{index},\"chunk\":{chunk},\"value\":\"{hex}\"}}";
}
