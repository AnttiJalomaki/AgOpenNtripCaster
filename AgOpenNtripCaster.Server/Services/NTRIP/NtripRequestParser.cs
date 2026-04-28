using System.Text;
using System.Text.RegularExpressions;

namespace AgOpenNtripCaster.Server.Services.NTRIP;

public static partial class NtripRequestParser
{
    public static async Task<NtripRequest> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken,
        int maxHeaderBytes = NtripProtocol.MaxHeaderBytes)
    {
        var bytes = new List<byte>(512);
        var buffer = new byte[1];

        while (bytes.Count < maxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            bytes.Add(buffer[0]);

            if (EndsWith(bytes, "\r\n\r\n"u8) || EndsWith(bytes, "\n\n"u8))
                return Parse(Encoding.ASCII.GetString(bytes.ToArray()));
        }

        if (bytes.Count >= maxHeaderBytes)
            throw new InvalidDataException($"NTRIP header exceeds {maxHeaderBytes} bytes");

        if (bytes.Count == 0)
            throw new InvalidDataException("Empty NTRIP request");

        return Parse(Encoding.ASCII.GetString(bytes.ToArray()));
    }

    public static NtripRequest Parse(string headerText)
    {
        var lines = NormalizeLineEndings(headerText).Split('\n');
        var requestLine = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (string.IsNullOrWhiteSpace(requestLine))
            throw new InvalidDataException("Missing NTRIP request line");

        var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requestParts.Length == 0)
            throw new InvalidDataException("Invalid NTRIP request line");

        var method = requestParts[0].ToUpperInvariant();
        var headers = ParseHeaders(lines.Skip(1));
        var (basicUsername, basicPassword) = ExtractBasicAuth(headers);

        string path;
        string? httpVersion = null;
        string? sourcePassword = null;

        if (method == "SOURCE")
        {
            if (requestParts.Length < 3)
                throw new InvalidDataException("SOURCE request must include password and mountpoint");

            sourcePassword = requestParts[1];
            path = requestParts[2];
            if (requestParts.Length >= 4)
                httpVersion = requestParts[3];
        }
        else
        {
            if (requestParts.Length < 2)
                throw new InvalidDataException($"{method} request must include a path");

            path = requestParts[1];
            if (requestParts.Length >= 3)
                httpVersion = requestParts[2];
        }

        var mountPoint = NormalizeMountPoint(path);
        if (!string.IsNullOrEmpty(mountPoint) && !IsValidMountPoint(mountPoint))
            throw new InvalidDataException($"Invalid mountpoint '{mountPoint}'");

        return new NtripRequest(
            method,
            path,
            httpVersion,
            headers,
            mountPoint,
            sourcePassword,
            basicUsername,
            basicPassword);
    }

    public static string NormalizeMountPoint(string value)
    {
        value = value.Trim();

        if (value.StartsWith('/'))
            value = value[1..];

        var queryIndex = value.IndexOf('?');
        if (queryIndex >= 0)
            value = value[..queryIndex];

        return value.Trim('/');
    }

    public static bool IsValidMountPoint(string value) => MountPointRegex().IsMatch(value);

    private static Dictionary<string, string> ParseHeaders(IEnumerable<string> lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                break;

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
                continue;

            var name = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            headers[name] = value;
        }

        return headers;
    }

    private static (string? Username, string? Password) ExtractBasicAuth(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Authorization", out var authHeader))
            return (null, null);

        const string prefix = "Basic ";
        if (!authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return (null, null);

        try
        {
            var base64 = authHeader[prefix.Length..].Trim();
            var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(base64));
            var separatorIndex = decoded.IndexOf(':');
            if (separatorIndex < 0)
                return (null, null);

            return (decoded[..separatorIndex], decoded[(separatorIndex + 1)..]);
        }
        catch (FormatException)
        {
            return (null, null);
        }
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static bool EndsWith(List<byte> bytes, ReadOnlySpan<byte> suffix)
    {
        if (bytes.Count < suffix.Length)
            return false;

        for (var i = 0; i < suffix.Length; i++)
        {
            if (bytes[bytes.Count - suffix.Length + i] != suffix[i])
                return false;
        }

        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MountPointRegex();
}
