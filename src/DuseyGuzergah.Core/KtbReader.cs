using System.Globalization;
using System.IO;

namespace DuseyGuzergah;

public static class KtbReader
{
    public static List<VerticalAlignmentVertex> ReadVerticalAlignmentVertices(string path)
    {
        var vertices = new List<VerticalAlignmentVertex>();
        var inVerticalSection = false;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Equals("$DUSEY", StringComparison.OrdinalIgnoreCase))
            {
                inVerticalSection = true;
                continue;
            }

            if (inVerticalSection && line.StartsWith('$'))
            {
                break;
            }

            if (!inVerticalSection)
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            if (TryParseDouble(parts[0], out var station) && TryParseDouble(parts[1], out var elevation))
            {
                vertices.Add(new VerticalAlignmentVertex(
                    station,
                    elevation,
                    ParseDoubleOrDefault(parts, 2),
                    ParseIntOrDefault(parts, 3),
                    ParseDoubleOrDefault(parts, 4),
                    ParseDoubleOrDefault(parts, 5),
                    ParseDoubleOrDefault(parts, 6)));
            }
        }

        return vertices.OrderBy(point => point.Station).ToList();
    }

    /// <summary>
    /// <c>$DUSEY</c> satırlarındaki ilk sütunu (KM) dosyada görülen sırayla döndürür — sıralama uygulanmaz.
    /// </summary>
    public static List<double> TryReadVerticalStationOrderRaw(string path)
    {
        var stations = new List<double>();
        var inVerticalSection = false;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Equals("$DUSEY", StringComparison.OrdinalIgnoreCase))
            {
                inVerticalSection = true;
                continue;
            }

            if (inVerticalSection && line.StartsWith('$'))
            {
                break;
            }

            if (!inVerticalSection)
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            if (TryParseDouble(parts[0], out var station) && TryParseDouble(parts[1], out _))
            {
                stations.Add(station);
            }
        }

        return stations;
    }

    public static List<ProfilePoint> BuildVerticalCurve(IReadOnlyList<VerticalAlignmentVertex> vertices)
    {
        if (vertices.Count == 0)
        {
            return [];
        }

        if (vertices.Count == 1)
        {
            return [new ProfilePoint(vertices[0].Station, vertices[0].Elevation)];
        }

        var curveRanges = BuildCurveRanges(vertices);
        var result = new List<ProfilePoint>
        {
            new(vertices[0].Station, vertices[0].Elevation)
        };

        for (var i = 0; i < vertices.Count - 1; i++)
        {
            var current = vertices[i];
            var next = vertices[i + 1];

            var tangentStartStation = curveRanges.TryGetValue(i, out var outgoingCurveTail)
                ? outgoingCurveTail.EndStation
                : current.Station;
            var tangentEndStation = curveRanges.TryGetValue(i + 1, out var incomingCurveHead)
                ? incomingCurveHead.StartStation
                : next.Station;

            if (tangentEndStation > tangentStartStation && TryTangentEndpoints(
                    current,
                    next,
                    tangentStartStation,
                    tangentEndStation,
                    curveRanges,
                    i,
                    out var tangentStartEl,
                    out var tangentEndEl))
            {
                AddIfDistinct(result, new ProfilePoint(tangentStartStation, tangentStartEl));
                AddIfDistinct(result, new ProfilePoint(tangentEndStation, tangentEndEl));
            }

            /* Kurp bu aradaki PI'da (vertices[i + 1]) merkezlidir — once teğet sonra eğri. */
            if (incomingCurveHead is not null)
            {
                AddCurveSamples(result, incomingCurveHead);
            }
        }

        return result.OrderBy(point => point.Station).ToList();
    }

    /// <summary>
    /// Teğet parçası: bir önceki BC/EC veya PI kotundan geçen düz aliyuman; PI-PI kirisi degil.
    /// </summary>
    private static bool TryTangentEndpoints(
        VerticalAlignmentVertex leftPi,
        VerticalAlignmentVertex rightPi,
        double tangentStartStation,
        double tangentEndStation,
        IReadOnlyDictionary<int, VerticalCurveRange> ranges,
        int segmentLeftIndex,
        out double tangentStartElevation,
        out double tangentEndElevation)
    {
        var spanSta = rightPi.Station - leftPi.Station;
        if (spanSta <= 0)
        {
            tangentStartElevation = 0;
            tangentEndElevation = 0;
            return false;
        }

        /* Girdi: sığim ve çıkım teğeti (soldan PI kotuna, sagdan PI kotuna uzatilir). */
        var gradeIn = (rightPi.Elevation - leftPi.Elevation) / spanSta;

        tangentStartElevation = ranges.TryGetValue(segmentLeftIndex, out var tail)
            ? tail.EndElevation
            : leftPi.Elevation + gradeIn * (tangentStartStation - leftPi.Station);

        tangentEndElevation = ranges.TryGetValue(segmentLeftIndex + 1, out var head)
            ? head.StartElevation
            : leftPi.Elevation + gradeIn * (tangentEndStation - leftPi.Station);

        return true;
    }

    public static List<VerticalCurveGuide> BuildVerticalCurveGuides(IReadOnlyList<VerticalAlignmentVertex> vertices)
    {
        var curveRanges = BuildCurveRanges(vertices);
        var guides = new List<VerticalCurveGuide>();

        foreach (var (index, range) in curveRanges)
        {
            var vertex = vertices[index];
            guides.Add(new VerticalCurveGuide(
                index,
                new ProfilePoint(range.StartStation, range.StartElevation),
                new ProfilePoint(vertex.Station, vertex.Elevation),
                new ProfilePoint(range.EndStation, range.EndElevation)));
        }

        return guides;
    }

    private static Dictionary<int, VerticalCurveRange> BuildCurveRanges(IReadOnlyList<VerticalAlignmentVertex> vertices)
    {
        var ranges = new Dictionary<int, VerticalCurveRange>();

        for (var i = 1; i < vertices.Count - 1; i++)
        {
            var previous = vertices[i - 1];
            var vertex = vertices[i];
            var next = vertices[i + 1];
            var tangentIn = vertex.TangentIn > 0 ? vertex.TangentIn : vertex.CurveLength / 2;
            var tangentOut = vertex.TangentOut > 0 ? vertex.TangentOut : vertex.CurveLength / 2;

            if (tangentIn <= 0 || tangentOut <= 0)
            {
                continue;
            }

            tangentIn = Math.Min(tangentIn, Math.Max(0, (vertex.Station - previous.Station) * 0.49));
            tangentOut = Math.Min(tangentOut, Math.Max(0, (next.Station - vertex.Station) * 0.49));

            if (tangentIn <= 0 || tangentOut <= 0)
            {
                continue;
            }

            var incomingSlope = (vertex.Elevation - previous.Elevation) / (vertex.Station - previous.Station);
            var outgoingSlope = (next.Elevation - vertex.Elevation) / (next.Station - vertex.Station);
            var startStation = vertex.Station - tangentIn;
            var endStation = vertex.Station + tangentOut;
            var startElevation = vertex.Elevation - incomingSlope * tangentIn;
            var endElevation = vertex.Elevation + outgoingSlope * tangentOut;

            ranges[i] = new VerticalCurveRange(
                startStation,
                endStation,
                startElevation,
                endElevation,
                incomingSlope,
                outgoingSlope,
                vertex.CurveType,
                vertex.Radius);
        }

        return ranges;
    }

    private static void AddCurveSamples(List<ProfilePoint> result, VerticalCurveRange range)
    {
        var length = range.EndStation - range.StartStation;
        var sampleCount = Math.Clamp((int)Math.Ceiling(length / 1.5), 12, 80);

        for (var i = 0; i <= sampleCount; i++)
        {
            var t = (double)i / sampleCount;
            var station = range.StartStation + length * t;
            var elevation = range is { CurveType: 2, Radius: > 1e-9 } && TryElevationCircularArc(range, station, out var elCirc)
                ? elCirc
                : ElevationParabolicRobust(range, station);

            AddIfDistinct(result, new ProfilePoint(station, elevation));
        }
    }

    /// <summary>
    /// Kurt (2011) parabolik düşey kurp: dört sınır koşulunun (konum+2 eğim)
    /// en küçük karelar çözümü — klasik y = ax²+bx+c ile tam tutarlı olmayan girdilerde de kararlı.
    /// </summary>
    private static double ElevationParabolicRobust(VerticalCurveRange range, double station)
    {
        var s0 = range.StartStation;
        var L = range.EndStation - s0;
        if (L <= 1e-12)
        {
            return range.StartElevation;
        }

        var (a, b, c) = SolveParabolicCoefficientsRobust(
            L,
            range.StartElevation,
            range.EndElevation,
            range.StartSlope,
            range.EndSlope);

        var x = station - s0;
        return a * x * x + b * x + c;
    }

    /// <summary>
    /// Denklem (15)-(16) ile uyumlu: A p ≈ b için normal denklemler (AᵀA)p = Aᵀb.
    /// </summary>
    private static (double A, double B, double C) SolveParabolicCoefficientsRobust(
        double L,
        double hO,
        double hF,
        double gO,
        double gF)
    {
        /* Satırlar: y(0), y'(0), y(L), y'(L) — bilinmeyenler a,b,c (y = ax²+bx+c). */
        var a11 = 0.0;
        var a12 = 0.0;
        var a13 = 0.0;
        var a22 = 0.0;
        var a23 = 0.0;
        var a33 = 0.0;
        var r1 = 0.0;
        var r2 = 0.0;
        var r3 = 0.0;

        void Accumulate(double ra, double rb, double rc, double rhs)
        {
            a11 += ra * ra;
            a12 += ra * rb;
            a13 += ra * rc;
            a22 += rb * rb;
            a23 += rb * rc;
            a33 += rc * rc;
            r1 += ra * rhs;
            r2 += rb * rhs;
            r3 += rc * rhs;
        }

        Accumulate(0, 0, 1, hO);
        Accumulate(0, 1, 0, gO);
        Accumulate(L * L, L, 1, hF);
        Accumulate(2 * L, 1, 0, gF);

        /* Cholesky (simetrik pozitif tanımlı beklenir) */
        var l11 = Math.Sqrt(Math.Max(a11, 1e-30));
        var l21 = a12 / l11;
        var l31 = a13 / l11;
        var l22 = Math.Sqrt(Math.Max(a22 - l21 * l21, 1e-30));
        var l32 = (a23 - l21 * l31) / l22;
        var l33 = Math.Sqrt(Math.Max(a33 - l31 * l31 - l32 * l32, 1e-30));

        var z1 = r1 / l11;
        var z2 = (r2 - l21 * z1) / l22;
        var z3 = (r3 - l31 * z1 - l32 * z2) / l33;

        var c = z3 / l33;
        var b = (z2 - l32 * c) / l22;
        var a = (z1 - l21 * b - l31 * c) / l11;

        return (a, b, c);
    }

    /// <summary>
    /// Dairesel düşey kurp: PVC–PVT uçları ve R ile çember üzerinden kot (Kurt Şekil 2, (8)).
    /// Merkez, |C-O|=|C-F|=R iki-çember kesişimi; dal, başlangıç kotuyla seçilir.
    /// </summary>
    private static bool TryElevationCircularArc(VerticalCurveRange range, double station, out double elevation)
    {
        elevation = 0;
        var r = range.Radius;
        if (r <= 1e-9)
        {
            return false;
        }

        var xO = range.StartStation;
        var yO = range.StartElevation;
        var xF = range.EndStation;
        var yF = range.EndElevation;
        var dx0 = xF - xO;
        var dy0 = yF - yO;
        var d = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
        if (d <= 1e-9 || d > 2 * r + 1e-6)
        {
            return false;
        }

        var mx = (xO + xF) * 0.5;
        var my = (yO + yF) * 0.5;
        var ux = dx0 / d;
        var uy = dy0 / d;
        /* Kirişe dik iki yön — merkez adayları */
        var px = -uy;
        var py = ux;
        var h = Math.Sqrt(Math.Max(0, r * r - (d * 0.5) * (d * 0.5)));

        var cx1 = mx + h * px;
        var cy1 = my + h * py;
        var cx2 = mx - h * px;
        var cy2 = my - h * py;

        var err1 = SlopeErrorAtO(cx1, cy1, xO, yO, range.StartSlope);
        var err2 = SlopeErrorAtO(cx2, cy2, xO, yO, range.StartSlope);
        double cx;
        double cy;
        if (err1 <= err2)
        {
            cx = cx1;
            cy = cy1;
        }
        else
        {
            cx = cx2;
            cy = cy2;
        }

        elevation = ElevationOnCircleBranch(station, cx, cy, r, xO, yO);
        return true;
    }

    private static double SlopeErrorAtO(double cx, double cy, double xO, double yO, double gO)
    {
        var dyo = yO - cy;
        if (Math.Abs(dyo) < 1e-12)
        {
            return double.PositiveInfinity;
        }

        /* Örtük türev: (x-cx) + (y-cy)*y' = 0 → y' = -(x-cx)/(y-cy) */
        var g = -(xO - cx) / dyo;
        return Math.Abs(g - gO);
    }

    private static double ElevationOnCircleBranch(double x, double cx, double cy, double r, double xRef, double yRef)
    {
        var dx = x - cx;
        var rd = r * r - dx * dx;
        if (rd < 0)
        {
            rd = 0;
        }

        var root = Math.Sqrt(rd);
        var yPlus = cy + root;
        var yMinus = cy - root;
        var refDx = xRef - cx;
        var refRd = r * r - refDx * refDx;
        if (refRd < 0)
        {
            refRd = 0;
        }

        var refRoot = Math.Sqrt(refRd);
        var errPlus = Math.Abs(yRef - (cy + refRoot));
        var errMinus = Math.Abs(yRef - (cy - refRoot));
        return errPlus <= errMinus ? yPlus : yMinus;
    }

    private static void AddIfDistinct(List<ProfilePoint> points, ProfilePoint point)
    {
        if (points.Count == 0 || Math.Abs(points[^1].Station - point.Station) > 0.000001)
        {
            points.Add(point);
        }
    }

    private static double ParseDoubleOrDefault(string[] parts, int index)
    {
        return index < parts.Length && TryParseDouble(parts[index], out var value) ? value : 0;
    }

    private static int ParseIntOrDefault(string[] parts, int index)
    {
        return index < parts.Length && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static bool TryParseDouble(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private sealed record VerticalCurveRange(
        double StartStation,
        double EndStation,
        double StartElevation,
        double EndElevation,
        double StartSlope,
        double EndSlope,
        int CurveType,
        double Radius);
}
