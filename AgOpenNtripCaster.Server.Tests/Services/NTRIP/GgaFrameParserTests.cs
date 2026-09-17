using AgOpenNtripCaster.Server.Services.NTRIP;
using Xunit;

namespace AgOpenNtripCaster.Server.Tests.Services.NTRIP;

public class GgaFrameParserTests
{
    [Fact]
    public void TryParse_ExtractsFixQualitySatellitesHdopAltitudeCorrectionAge()
    {
        var ok = GgaFrameParser.TryParse(
            "$GPGGA,123519,4807.038,N,01131.000,E,4,12,0.8,545.4,M,46.9,M,1.5,0001*5A",
            out var frame);

        Assert.True(ok);
        Assert.NotNull(frame);
        Assert.True(frame!.IsPositionValid);
        Assert.Equal(48.1173, frame.Latitude!.Value, 4);
        Assert.Equal(11.5166667, frame.Longitude!.Value, 4);
        Assert.Equal(4, frame.FixQuality);
        Assert.Equal(12, frame.SatelliteCount);
        Assert.Equal(0.8, frame.Hdop);
        Assert.Equal(545.4, frame.AltitudeMeters);
        Assert.Equal(46.9, frame.GeoidSeparationMeters);
        Assert.Equal(1.5, frame.DifferentialAgeSeconds);
        Assert.Equal("0001", frame.DifferentialStationId);
    }

    [Theory]
    [InlineData("$GPGGA,123519,0000.000,N,00000.000,E,0,00,99.9,0.0,M,0.0,M,,*48")]
    [InlineData("$GPGGA,123519,,,,,0,00,99.9,,,,,,*48")]
    public void TryParse_ReturnsFrameButMarksInvalidCoordinates(string sentence)
    {
        var ok = GgaFrameParser.TryParse(sentence, out var frame);

        Assert.True(ok);
        Assert.NotNull(frame);
        Assert.False(frame!.IsPositionValid);
    }

    [Fact]
    public void TryParse_RejectsNonGgaLine()
    {
        Assert.False(GgaFrameParser.TryParse("$GPRMC,ignored", out var frame));
        Assert.Null(frame);
    }
}
