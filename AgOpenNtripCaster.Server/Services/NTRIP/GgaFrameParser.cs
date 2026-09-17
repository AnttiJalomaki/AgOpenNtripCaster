using System.Globalization;

namespace AgOpenNtripCaster.Server.Services.NTRIP;

public sealed record GgaFrame(
    string Raw,
    TimeOnly? FixTimeUtc,
    double? Latitude,
    double? Longitude,
    int? FixQuality,
    int? SatelliteCount,
    double? Hdop,
    double? AltitudeMeters,
    double? GeoidSeparationMeters,
    double? DifferentialAgeSeconds,
    string? DifferentialStationId)
{
    public bool IsPositionValid =>
        Latitude is >= -90 and <= 90 &&
        Longitude is >= -180 and <= 180 &&
        !(Latitude == 0 && Longitude == 0) &&
        FixQuality.GetValueOrDefault() > 0;
}

public static class GgaFrameParser
{
    public static bool TryParse(string? line, out GgaFrame? frame)
    {
        frame = null;
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("$GPGGA", StringComparison.OrdinalIgnoreCase))
            return false;

        var noChecksum = line.Split('*', 2)[0];
        var parts = noChecksum.Split(',');
        if (parts.Length < 10)
            return false;

        frame = new GgaFrame(
            Raw: line,
            FixTimeUtc: GetPart(parts, 1) is { } fixTime ? TryParseTime(fixTime) : null,
            Latitude: TryParseCoordinate(GetPart(parts, 2), GetPart(parts, 3), isLatitude: true),
            Longitude: TryParseCoordinate(GetPart(parts, 4), GetPart(parts, 5), isLatitude: false),
            FixQuality: GetPart(parts, 6) is { } fixQuality ? TryParseInt(fixQuality) : null,
            SatelliteCount: GetPart(parts, 7) is { } satelliteCount ? TryParseInt(satelliteCount) : null,
            Hdop: GetPart(parts, 8) is { } hdop ? TryParseDouble(hdop) : null,
            AltitudeMeters: GetPart(parts, 9) is { } altitude ? TryParseDouble(altitude) : null,
            GeoidSeparationMeters: GetPart(parts, 11) is { } geoid ? TryParseDouble(geoid) : null,
            DifferentialAgeSeconds: GetPart(parts, 13) is { } age ? TryParseDouble(age) : null,
            DifferentialStationId: GetPart(parts, 14) is { Length: > 0 } stationId ? stationId : null);

        return true;
    }

    private static string? GetPart(string[] parts, int index) =>
        index < parts.Length ? parts[index] : null;

    private static double? TryParseCoordinate(string? value, string? direction, bool isLatitude)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(direction))
            return null;

        if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var nmea))
            return null;

        if (direction is not ("N" or "S" or "E" or "W"))
            return null;

        var degrees = (int)(nmea / 100.0);
        var minutes = nmea - degrees * 100.0;
        var decimalDegrees = degrees + minutes / 60.0;

        if (direction is "S" or "W")
            decimalDegrees = -decimalDegrees;

        if (isLatitude && decimalDegrees is < -90 or > 90)
            return null;

        if (!isLatitude && decimalDegrees is < -180 or > 180)
            return null;

        return decimalDegrees;
    }

    private static TimeOnly? TryParseTime(string value)
    {
        if (value.Length < 6)
            return null;

        if (!int.TryParse(value[..2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(value.Substring(2, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute) ||
            !int.TryParse(value.Substring(4, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var second))
        {
            return null;
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
            return null;

        return new TimeOnly(hour, minute, second);
    }

    private static int? TryParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static double? TryParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
