namespace DuseyGuzergah.Api.Sessions;

public sealed class DesignSession
{
    public required Guid Id { get; init; }

    public required string DirectoryPath { get; init; }

    public string? KtbTemplatePath { get; init; }

    public required List<ProfilePoint> Terrain { get; init; }

    public List<VerticalAlignmentVertex> KtbVertices { get; init; } = [];

    public List<ProfilePoint> KtbCurve { get; init; } = [];

    public List<VerticalCurveGuide> KtbGuides { get; init; } = [];

    public List<VerticalAlignmentVertex>? CalculatedVertices { get; set; }

    public DateTimeOffset LastAccessUtc { get; set; } = DateTimeOffset.UtcNow;

    public object Gate { get; } = new();
}
