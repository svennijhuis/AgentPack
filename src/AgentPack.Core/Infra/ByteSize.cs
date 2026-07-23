namespace AgentPack.Core;

/// <summary>Human-scale sizes for file sets shown in previews and error messages.</summary>
public static class ByteSize
{
    public static string Format(long bytes) => bytes < 1024 * 1024
        ? $"{Math.Max(1, bytes / 1024)} KB"
        : $"{bytes / (1024d * 1024d):0.#} MB";
}
