using System.Globalization;
using System.Text;

namespace AgOpenNtripCaster.Server.Services.NTRIP;

public static class NtripChunkedStreamReader
{
    public static async Task ReadChunksAsync(
        Stream stream,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var sizeLine = await ReadAsciiLineAsync(stream, cancellationToken);
            if (sizeLine == null)
                return;

            if (sizeLine.Length == 0)
                continue;

            var sizeText = sizeLine.Split(';', 2)[0].Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize))
                throw new InvalidDataException($"Invalid NTRIP chunk size '{sizeLine}'");

            if (chunkSize == 0)
            {
                await ConsumeTrailerAsync(stream, cancellationToken);
                return;
            }

            var payload = new byte[chunkSize];
            await ReadExactlyAsync(stream, payload, cancellationToken);
            await onChunk(payload, cancellationToken);
            await ConsumeChunkTerminatorAsync(stream, cancellationToken);
        }
    }

    private static async Task<string?> ReadAsciiLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(16);
        var buffer = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }

            if (buffer[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                    bytes.RemoveAt(bytes.Count - 1);

                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add(buffer[0]);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await stream.ReadAsync(payload.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of stream while reading NTRIP chunk payload");

            offset += read;
        }
    }

    private static async Task ConsumeChunkTerminatorAsync(Stream stream, CancellationToken cancellationToken)
    {
        var first = await ReadByteOrThrowAsync(stream, cancellationToken);
        if (first == '\n')
            return;

        if (first != '\r')
            throw new InvalidDataException("NTRIP chunk payload was not followed by CRLF");

        var second = await ReadByteOrThrowAsync(stream, cancellationToken);
        if (second != '\n')
            throw new InvalidDataException("NTRIP chunk payload was not followed by CRLF");
    }

    private static async Task ConsumeTrailerAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            var trailerLine = await ReadAsciiLineAsync(stream, cancellationToken);
            if (trailerLine == null || trailerLine.Length == 0)
                return;
        }
    }

    private static async Task<char> ReadByteOrThrowAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        if (read == 0)
            throw new EndOfStreamException("Unexpected end of stream while reading NTRIP chunk terminator");

        return (char)buffer[0];
    }
}
