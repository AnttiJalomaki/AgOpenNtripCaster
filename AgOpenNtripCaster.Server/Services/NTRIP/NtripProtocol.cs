using System.Text;

namespace AgOpenNtripCaster.Server.Services.NTRIP;

public static class NtripProtocol
{
    public const string CrLf = "\r\n";
    public const int MaxHeaderBytes = 8192;

    public static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
}

public sealed record NtripRequest(
    string Method,
    string Path,
    string? HttpVersion,
    Dictionary<string, string> Headers,
    string MountPoint,
    string? SourcePassword,
    string? BasicUsername,
    string? BasicPassword)
{
    public bool IsSourcetableRequest => Method == "GET" && string.IsNullOrEmpty(MountPoint);

    public bool IsChunked =>
        Headers.TryGetValue("Transfer-Encoding", out var encoding) &&
        encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);
}
