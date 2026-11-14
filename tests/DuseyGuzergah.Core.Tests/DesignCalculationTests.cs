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
    public void Example_profile_stays_sparser_than_the_unpruned_run()
    {
        var ksePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "ARAZI.KSE");
        var terrain = KseReader.ReadCenterProfile(ksePath);
        var parameters = DesktopDefaults() with
        {
            MinimumCover = 1.0,
            MinimumRadius = 18.288
        };
        var result = VerticalDesignCalculator.Calculate(terrain, parameters);
        Assert.InRange(result.Vertices.Count, 1100, 1450);
    }

    [Fact]
    public void Crest_is_kept_close_to_the_ground()
    {
        var terrain = new List<ProfilePoint>();
        for (var station = 0d; station <= 600; station += 5)
        {
            var rise = 0d;
            if (station is > 200 and < 400)
            {
                rise = station <= 300 ? (station - 200) * 0.08 : (400 - station) * 0.08;
            }

            terrain.Add(new ProfilePoint(station, 100 + rise));
        }

        var result = VerticalDesignCalculator.Calculate(
            terrain,
            DesktopDefaults() with { MinimumCover = 1.0, MergeExtraBurial = 2 });
        var peak = result.Curve.OrderBy(point => Math.Abs(point.Station - 300)).First();
        Assert.InRange(108 - peak.Elevation, 0.9, 2.4);
    }

    [Fact]
    public void Crest_curve_stays_an_arc_instead_of_a_flat_top()
    {
        var terrain = new List<ProfilePoint>();
        for (var station = 0d; station <= 400; station += 5)
        {
            var rise = station <= 200 ? station * 0.08 : (400 - station) * 0.08;
            terrain.Add(new ProfilePoint(station, 100 + rise));
        }

        var result = VerticalDesignCalculator.Calculate(terrain, DesktopDefaults());
        var crest = result.Curve.Where(point => point.Station is > 170 and < 230).ToList();
        Assert.True(crest.Count >= 8);
        var flatRun = 1;
        var longestFlat = 1;
        for (var i = 1; i < crest.Count; i++)
        {
            flatRun = Math.Abs(crest[i].Elevation - crest[i - 1].Elevation) < 0.002 ? flatRun + 1 : 1;
            longestFlat = Math.Max(longestFlat, flatRun);
        }

        Assert.True(longestFlat < 4, $"düz tepe örnek sayısı {longestFlat}");
    }

    [Fact]
    public void Prune_removes_vertices_that_only_follow_small_ripples()
    {
        var terrain = new List<ProfilePoint>();
        for (var station = 0d; station <= 2000; station += 5)
        {
            terrain.Add(new ProfilePoint(station, 100 + 0.3 * Math.Sin(station / 8)));
        }

        var result = VerticalDesignCalculator.Calculate(terrain, DesktopDefaults());
        Assert.InRange(result.Vertices.Count, 3, 120);
        for (var i = 1; i < result.Vertices.Count; i++)
        {
            Assert.True(result.Vertices[i].Station - result.Vertices[i - 1].Station > 0.05);
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

    [Fact]
    public void Vertices_are_written_at_pipe_axis()
    {
        var terrain = new List<ProfilePoint>();
        for (var station = 0; station <= 400; station += 10)
        {
            terrain.Add(new ProfilePoint(station, 100));
        }

        var result = VerticalDesignCalculator.Calculate(terrain, DesktopDefaults() with { MinimumCover = 1.0 });
        Assert.Equal(0.1524, result.AxisOffset, 4);
        Assert.All(result.Vertices, vertex => Assert.InRange(99.85 - (vertex.Elevation + result.AxisOffset), 1.0, 1.45));
        Assert.All(result.Vertices, vertex => Assert.True(vertex.Elevation < 99.85 - 1.0 - result.AxisOffset + 1e-6));
    }

    [Fact]
    public void Every_curve_respects_the_effective_minimum_radius()
    {
        var terrain = new List<ProfilePoint>();
        for (var station = 0d; station <= 800; station += 5)
        {
            var z = 100d;
            if (station is > 200 and <= 260)
            {
                z -= (station - 200) * 0.25;
            }
            else if (station is > 260 and < 320)
            {
                z -= (320 - station) * 0.25;
            }
            else if (station is > 500 and < 600)
            {
                z += 6 - Math.Abs(station - 550) * 0.12;
            }

            terrain.Add(new ProfilePoint(station, z));
        }

        var parameters = DesktopDefaults() with { MinimumCover = 1.0, MaxDeflectionDeg = 7.2 };
        var radius = VerticalDesignCalculator.EffectiveMinimumRadius(parameters);
        Assert.InRange(radius, 95.4, 95.6);

        var result = VerticalDesignCalculator.Calculate(terrain, parameters);
        var vertices = result.Vertices;
        for (var i = 1; i < vertices.Count - 1; i++)
        {
            var gradeIn = (vertices[i].Elevation - vertices[i - 1].Elevation) / (vertices[i].Station - vertices[i - 1].Station);
            var gradeOut = (vertices[i + 1].Elevation - vertices[i].Elevation) / (vertices[i + 1].Station - vertices[i].Station);
            var required = radius * Math.Abs(gradeOut - gradeIn);
            Assert.True(
                vertices[i].CurveLength >= required - 1e-3,
                $"KM {vertices[i].Station:0.00}: L={vertices[i].CurveLength:0.00} < R·Δg={required:0.00}");
        }
    }

    [Fact]
    public void Crossing_is_flat_through_buffers_and_ramps_out_at_least_minimum_length()
    {
        var terrain = new List<ProfilePoint>();
        for (var station = 0; station <= 400; station += 5)
        {
            terrain.Add(new ProfilePoint(station, 100));
        }

        var parameters = DesktopDefaults() with
        {
            MinimumCover = 1.0,
            MaxDeflectionDeg = 7.2,
            Crossings = [new SpecialCrossing("yol", "karayolu", 180, 220, 5.0)]
        };

        var result = VerticalDesignCalculator.Calculate(terrain, parameters);
        var flat = result.Curve.Where(point => point.Station >= 174 && point.Station <= 226).ToList();
        Assert.NotEmpty(flat);
        var level = flat[0].Elevation;
        Assert.All(flat, point => Assert.Equal(level, point.Elevation, 3));
        Assert.True(level + result.AxisOffset <= 99.85 - 5.0 + 1e-6);

        var vertices = result.Vertices;
        var leftPi = vertices.Last(vertex => vertex.Station < 180);
        var rightPi = vertices.First(vertex => vertex.Station > 220);
        Assert.Equal(level, leftPi.Elevation, 3);
        Assert.Equal(level, rightPi.Elevation, 3);
        Assert.True(leftPi.Station + leftPi.TangentOut <= 174 + 1e-3);
        Assert.True(rightPi.Station - rightPi.TangentIn >= 226 - 1e-3);

        var beforeLeft = vertices[vertices.IndexOf(leftPi) - 1];
        var afterRight = vertices[vertices.IndexOf(rightPi) + 1];
        Assert.True(leftPi.Station - beforeLeft.Station >= 12.5 - 1e-6);
        Assert.True(afterRight.Station - rightPi.Station >= 12.5 - 1e-6);
    }

    private static DesignParameters DesktopDefaults() => new(
        TargetCover: 1.20,
        CoverTolerance: 0.05,
        MinimumCover: 1.20,
        MaximumCover: 4.20,
        StrippingDepth: 0.15,
        PipeDiameter: 0.3048,
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
