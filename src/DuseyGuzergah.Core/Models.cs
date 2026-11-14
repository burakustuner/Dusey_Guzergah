namespace DuseyGuzergah;

public sealed record ProfilePoint(double Station, double Elevation);

public sealed record VerticalAlignmentVertex(
    double Station,
    double Elevation,
    double CurveLength,
    int CurveType,
    double TangentIn,
    double TangentOut,
    double Radius);

public sealed record VerticalCurveGuide(
    int VertexIndex,
    ProfilePoint Start,
    ProfilePoint Vertex,
    ProfilePoint End);

public sealed record CoverEnvelopePoint(
    double Station,
    double TerrainElevation,
    double StrippedElevation,
    /// <summary>Sadelesmis (siyah) zemin kotuna gore boru ust nominal kotu; hedef derinlik kadar asagida.</summary>
    double NominalPipeTopElevation,
    /// <summary>Koridor: en derine inebilir boru ust kotu (tampon alt).</summary>
    double PreferredLowerElevation,
    /// <summary>Koridor: en sig kalabilir boru ust kotu (tampon ust).</summary>
    double PreferredUpperElevation,
    double MinimumCoverUpperElevation);

public sealed record SpecialCrossing(
    string Name,
    string Type,
    double StartStation,
    double EndStation,
    double Depth);

public sealed record DesignParameters(
    double TargetCover,
    double CoverTolerance,
    double MinimumCover,
    double MaximumCover,
    double StrippingDepth,
    double PipeDiameter,
    double PipeLength,
    double MinimumSegmentLength,
    double MinimumRadius,
    double ColdBendMinRadius = 0,
    double ElasticRadiusFactor = 0,
    double HotBendRadiusFactor = 5,
    double HotBendMaxAngleDeg = 45,
    double HotBendTangentM = 1,
    double StraightEndM = 1.8,
    double MergeExtraBurial = 0.40,
    IReadOnlyList<SpecialCrossing>? Crossings = null,
    /// <summary>Boru boyu başına izin verilen en büyük sapma açısı (°); 0 ise yalnızca MinimumRadius geçerli.</summary>
    double MaxDeflectionDeg = 0,
    /// <summary>Özel geçişin iki yanında düz bırakılan mesafe (m).</summary>
    double CrossingBufferM = 6.0,
    /// <summary>Düz bölümden koridora bağlanan rampanın en kısa boyu (m).</summary>
    double CrossingRampMinM = 12.5);

public sealed class DesignProfileResult
{
    /// <summary>Zarf ve profil listeleri boru üstü kotundadır; Vertices/Curve/Guides eksen kotundadır (üst − bu değer).</summary>
    public double AxisOffset { get; set; }

    public List<CoverEnvelopePoint> CoverEnvelope { get; } = [];

    public List<VerticalAlignmentVertex> Vertices { get; } = [];

    public List<ProfilePoint> Curve { get; } = [];

    public List<VerticalCurveGuide> Guides { get; } = [];

    public List<ProfilePoint> TargetProfile { get; } = [];

    public List<ProfilePoint> PreferredLowerProfile { get; } = [];

    public List<ProfilePoint> PreferredUpperProfile { get; } = [];

    public List<ProfilePoint> MinimumCoverUpperProfile { get; } = [];

    public List<CoverIssue> CoverIssues { get; } = [];
}

public sealed record CoverIssue(double Station, double Cover, string Message);

public sealed record CrossSectionPoint(double Offset, double Elevation);

public sealed class CrossSection
{
    public CrossSection(int index, double station, string sourceType)
    {
        Index = index;
        Station = station;
        SourceType = sourceType;
    }

    public int Index { get; }

    public double Station { get; }

    public string SourceType { get; }

    public List<CrossSectionPoint> Points { get; } = [];

    public double? CenterElevation { get; set; }

    public ProfilePoint? ToCenterProfilePoint()
    {
        if (CenterElevation is double centerElevation)
        {
            return new ProfilePoint(Station, centerElevation);
        }

        if (Points.Count == 0)
        {
            return null;
        }

        var exact = Points.FirstOrDefault(point => Math.Abs(point.Offset) < 0.000001);
        if (exact is not null)
        {
            return new ProfilePoint(Station, exact.Elevation);
        }

        var ordered = Points.OrderBy(point => point.Offset).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var left = ordered[i - 1];
            var right = ordered[i];

            if (left.Offset <= 0 && right.Offset >= 0 && Math.Abs(right.Offset - left.Offset) > 0.000001)
            {
                var t = (0 - left.Offset) / (right.Offset - left.Offset);
                var elevation = left.Elevation + t * (right.Elevation - left.Elevation);
                return new ProfilePoint(Station, elevation);
            }
        }

        return null;
    }
}

public sealed class ProfileData
{
    public string? KsePath { get; set; }

    public string? KtbPath { get; set; }

    public List<CrossSection> Sections { get; } = [];

    public List<ProfilePoint> TerrainProfile { get; } = [];

    public List<VerticalAlignmentVertex> VerticalAlignmentVertices { get; } = [];

    public List<ProfilePoint> VerticalAlignmentCurve { get; } = [];

    public List<VerticalCurveGuide> VerticalCurveGuides { get; } = [];

    public DesignProfileResult? CalculatedDesign { get; set; }
}
