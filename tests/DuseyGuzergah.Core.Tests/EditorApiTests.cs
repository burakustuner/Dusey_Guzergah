using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DuseyGuzergah;

namespace DuseyGuzergah.Core.Tests;

public sealed class EditorApiTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _factory;

    public EditorApiTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Curve_preview_matches_reader_and_editor_export_roundtrips()
    {
        var ksePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "ARAZI.KSE");
        var ktbPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "yatay.KTB");
        using var client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(File.OpenRead(ksePath)), "kse", "ARAZI.KSE");
        form.Add(new StreamContent(File.OpenRead(ktbPath)), "ktb", "yatay.KTB");
        var created = await client.PostAsync("/api/sessions", form);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var session = JsonSerializer.Deserialize<SessionPayload>(await created.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(session);

        var vertices = new[]
        {
            new { station = 0.0, elevation = 10.0, curveLength = 0.0, curveType = 0, tangentIn = 0.0, tangentOut = 0.0, radius = 0.0 },
            new { station = 40.0, elevation = 12.0, curveLength = 12.0, curveType = 1, tangentIn = 6.0, tangentOut = 6.0, radius = 60.0 },
            new { station = 80.0, elevation = 11.0, curveLength = 0.0, curveType = 0, tangentIn = 0.0, tangentOut = 0.0, radius = 0.0 }
        };

        var preview = await client.PostAsJsonAsync("/api/alignment/curve", vertices, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var curve = JsonSerializer.Deserialize<CurvePayload>(await preview.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(curve);
        Assert.True(curve.Curve.Count >= 2);

        var direct = KtbReader.BuildVerticalCurve(
        [
            new VerticalAlignmentVertex(0, 10, 0, 0, 0, 0, 0),
            new VerticalAlignmentVertex(40, 12, 12, 1, 6, 6, 60),
            new VerticalAlignmentVertex(80, 11, 0, 0, 0, 0, 0)
        ]);
        Assert.Equal(direct.Count, curve.Curve.Count);
        Assert.Equal(direct[0].Station, curve.Curve[0].Station, 6);
        Assert.Equal(direct[^1].Elevation, curve.Curve[^1].Elevation, 6);

        var warnings = await client.PostAsJsonAsync($"/api/sessions/{session.SessionId}/warnings", new
        {
            thresholds = new
            {
                minInteriorCurveLengthM = 5.0,
                minStrippedTerrainToAxisGapM = 0.45,
                warnRadiusBelowDesignMinimum = true,
                maxAbsGradePercent = 25.0
            },
            parameters = new
            {
                targetCover = 1.2,
                coverTolerance = 0.05,
                minimumCover = 1.2,
                maximumCover = 4.2,
                strippingDepth = 0.15,
                pipeDiameter = 0.3048,
                pipeLength = 12.0,
                minimumSegmentLength = 24.0,
                minimumRadius = 60.0
            },
            vertices
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, warnings.StatusCode);

        var exported = await client.PostAsJsonAsync($"/api/sessions/{session.SessionId}/export-editor", vertices, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        var bytes = await exported.Content.ReadAsByteArrayAsync();
        var exportPath = Path.Combine(Path.GetTempPath(), $"dusey-editor-{Guid.NewGuid():N}.KTB");
        await File.WriteAllBytesAsync(exportPath, bytes);
        try
        {
            var written = KtbReader.ReadVerticalAlignmentVertices(exportPath);
            Assert.Equal(3, written.Count);
            Assert.Equal(40, written[1].Station, 5);
            Assert.Equal(12, written[1].Elevation, 5);
        }
        finally
        {
            File.Delete(exportPath);
        }
    }

    private sealed record SessionPayload(Guid SessionId);

    private sealed record CurvePayload(List<PointPayload> Curve);

    private sealed record PointPayload(double Station, double Elevation);
}
