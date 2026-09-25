using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DuseyGuzergah;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DuseyGuzergah.Core.Tests;

public sealed class DesignCalculationTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _factory;

    public DesignCalculationTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Api_vertices_match_core_calculator_and_export_roundtrips()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var ksePath = Path.Combine(fixtureDir, "ARAZI.KSE");
        var ktbPath = Path.Combine(fixtureDir, "yatay.KTB");
        Assert.True(File.Exists(ksePath), ksePath);
        Assert.True(File.Exists(ktbPath), ktbPath);

        var terrain = KseReader.ReadCenterProfile(ksePath);
        var parameters = DesktopDefaults();
        var direct = VerticalDesignCalculator.Calculate(terrain, parameters);
        Assert.True(direct.Vertices.Count >= 2);

        using var client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(File.OpenRead(ksePath)), "kse", "ARAZI.KSE");
        form.Add(new StreamContent(File.OpenRead(ktbPath)), "ktb", "yatay.KTB");

        var created = await client.PostAsync("/api/sessions", form);
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.OK, createdBody);

        var session = JsonSerializer.Deserialize<SessionPayload>(createdBody, JsonOptions);
        Assert.NotNull(session);
        Assert.True(session.HasKtbTemplate);
        Assert.Equal(terrain.Count, session.TerrainPointCount);

        var calculated = await client.PostAsJsonAsync($"/api/sessions/{session.SessionId}/calculate", parameters, JsonOptions);
        var calculatedBody = await calculated.Content.ReadAsStringAsync();
        Assert.True(calculated.StatusCode == HttpStatusCode.OK, calculatedBody);

        var result = JsonSerializer.Deserialize<DesignPayload>(calculatedBody, JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(direct.Vertices.Count, result.Vertices.Count);
        for (var i = 0; i < direct.Vertices.Count; i++)
        {
            Assert.Equal(direct.Vertices[i].Station, result.Vertices[i].Station, 6);
            Assert.Equal(direct.Vertices[i].Elevation, result.Vertices[i].Elevation, 6);
        }

        var exported = await client.PostAsync($"/api/sessions/{session.SessionId}/export", content: null);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        var bytes = await exported.Content.ReadAsByteArrayAsync();
        var exportPath = Path.Combine(Path.GetTempPath(), $"dusey-export-{Guid.NewGuid():N}.KTB");
        await File.WriteAllBytesAsync(exportPath, bytes);
        try
        {
            var written = KtbReader.ReadVerticalAlignmentVertices(exportPath);
            Assert.Equal(direct.Vertices.Count, written.Count);
            for (var i = 0; i < direct.Vertices.Count; i++)
            {
                Assert.True(Math.Abs(direct.Vertices[i].Station - written[i].Station) <= 0.00001);
                Assert.True(Math.Abs(direct.Vertices[i].Elevation - written[i].Elevation) <= 0.00001);
            }
        }
        finally
        {
            File.Delete(exportPath);
        }
    }

    [Fact]
    public void Crossing_keeps_nominal_at_required_depth()
    {
        var terrain = new List<ProfilePoint>();
        for (var station = 0; station <= 200; station += 10)
        {
            terrain.Add(new ProfilePoint(station, 100));
        }

        var parameters = DesktopDefaults() with
        {
            MinimumCover = 1.0,
            Crossings = [new SpecialCrossing("dere", "akarsu", 80, 120, 2.5)]
        };

        var result = VerticalDesignCalculator.Calculate(terrain, parameters);
        var inside = result.TargetProfile.Where(point => point.Station >= 80 && point.Station <= 120).ToList();
        Assert.NotEmpty(inside);
        Assert.All(inside, point => Assert.InRange(99.85 - point.Elevation, 2.45, 2.55));
    }

    private static DesignParameters DesktopDefaults() => new(
        TargetCover: 1.20,
        CoverTolerance: 0.05,
        MinimumCover: 1.20,
        MaximumCover: 4.20,
        StrippingDepth: 0.15,
        PipeDiameter: 12,
        PipeLength: 12,
        MinimumSegmentLength: 24,
        MinimumRadius: 60);

    private sealed record SessionPayload(Guid SessionId, int TerrainPointCount, bool HasKtbTemplate);

    private sealed record DesignPayload(List<VertexPayload> Vertices);

    private sealed record VertexPayload(double Station, double Elevation);
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _sessionRoot = Path.Combine(Path.GetTempPath(), "dusey-test-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sessions:RootPath"] = _sessionRoot,
                ["Sessions:TtlMinutes"] = "30"
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_sessionRoot))
        {
            Directory.Delete(_sessionRoot, recursive: true);
        }
    }
}
