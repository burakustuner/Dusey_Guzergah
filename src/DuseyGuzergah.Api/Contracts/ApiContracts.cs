namespace DuseyGuzergah.Api.Contracts;

public sealed record ValidationIssueDto(string Severity, string Source, string Message);

public sealed record ProfileBounds(
    double MinStation,
    double MaxStation,
    double MinElevation,
    double MaxElevation);

public sealed record SessionResponse(
    Guid SessionId,
    int SectionCount,
    int TerrainPointCount,
    IReadOnlyList<ProfilePoint> Terrain,
    IReadOnlyList<VerticalAlignmentVertex> KtbVertices,
    IReadOnlyList<ProfilePoint> KtbCurve,
    IReadOnlyList<VerticalCurveGuide> KtbGuides,
    IReadOnlyList<ValidationIssueDto> Issues,
    ProfileBounds Bounds,
    bool HasKtbTemplate);

public sealed record ApiMessage(string Message);

public sealed record CurvePreviewResponse(
    IReadOnlyList<ProfilePoint> Curve,
    IReadOnlyList<VerticalCurveGuide> Guides);

public sealed record WarningRequest(
    DesignWarningThresholds Thresholds,
    DesignParameters Parameters,
    List<VerticalAlignmentVertex> Vertices);
