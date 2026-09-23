namespace DuseyGuzergah;

/// <summary>Uyarı sekmesi eşikleri — kullanıcı arayüzünden atanır.</summary>
public sealed class DesignWarningThresholds
{
    /// <summary>Kurp köşesi (ara PI): L şu değerin altına inerse uyarı (m).</summary>
    public double MinInteriorCurveLengthM { get; set; } = 5;

    /// <summary>Sıyırılmış arazi kotası ile eksen kotu arasındaki düşey fark uyarısı (m); daha küçük = zemine yakın.</summary>
    public double MinStrippedTerrainToAxisGapM { get; set; } = 0.45;

    /// <summary>Hesaptaki Minimum R parametresinden küçük yarıçap uyarısı (sadece R&gt;0 ve tip 2 için anlamlı).</summary>
    public bool WarnRadiusBelowDesignMinimum { get; set; } = true;

    /// <summary>Mutlak giren yüzey eğimi (%, |g|×100) bu değeri aşınca uyarı.</summary>
    public double MaxAbsGradePercent { get; set; } = 25;
}

public static class DesignWarningsEvaluator
{
    public static List<string> Evaluate(
        DesignWarningThresholds thresholds,
        DesignParameters designParameters,
        IReadOnlyList<ProfilePoint>? terrainNatural,
        IReadOnlyList<VerticalAlignmentVertex> verticesOrdered,
        IReadOnlyList<ProfilePoint>? alignmentSamples)
    {
        var msgs = new List<string>();

        var n = verticesOrdered.Count;

        /* Ara kurp parametreleri + eğim */
        for (var i = 0; i < n; i++)
        {
            var v = verticesOrdered[i];

            double? gradeInPercent = null;
            if (i > 0)
            {
                var prev = verticesOrdered[i - 1];
                var ds = v.Station - prev.Station;
                if (Math.Abs(ds) > 1e-12)
                {
                    gradeInPercent = (v.Elevation - prev.Elevation) / ds * 100.0;
                    if (Math.Abs(gradeInPercent.Value) > thresholds.MaxAbsGradePercent && i < n - 1)
                    {
                        msgs.Add($"KM {v.Station:0.###}: Çok keskin yaklaşım eğimi ~{gradeInPercent.Value:0.#}% (≥ {thresholds.MaxAbsGradePercent}).");
                    }
                }
            }

            if (i > 0 && i < n - 1 &&
                thresholds.MinInteriorCurveLengthM > 0 &&
                v.CurveLength > 1e-6 &&
                v.CurveLength < thresholds.MinInteriorCurveLengthM - 1e-6)
            {
                msgs.Add($"KM {v.Station:0.###}: Kurp uzunlığı çok kısa (L≈{v.CurveLength:0.##} m < {thresholds.MinInteriorCurveLengthM}).");
            }

            if (thresholds.WarnRadiusBelowDesignMinimum &&
                v.Radius > 1e-3 &&
                v.Radius + 1e-6 < designParameters.MinimumRadius)
            {
                msgs.Add($"KM {v.Station:0.###}: Yaricap R≈{v.Radius:0.#} m, hesaptaki Minimum R ({designParameters.MinimumRadius}) altında.");
            }
        }

        if (terrainNatural is null ||
            terrainNatural.Count < 2 ||
            thresholds.MinStrippedTerrainToAxisGapM <= 0)
        {
            return DistinctPreserveOrder(msgs);
        }

        var strip = designParameters.StrippingDepth;

        foreach (var v in verticesOrdered)
        {
            var stripped = SampleStrippedTerrain(terrainNatural, v.Station) - strip;
            var gap = stripped - v.Elevation;
            if (gap < thresholds.MinStrippedTerrainToAxisGapM)
            {
                msgs.Add($"KM {v.Station:0.###}: Sıyırılmış zemine eksen yakın (~{gap:0.##} m); eşik {thresholds.MinStrippedTerrainToAxisGapM} m.");
            }
        }

        if (alignmentSamples is { Count: > 0 })
        {
            ProfilePoint? lastWarn = null;
            foreach (var p in alignmentSamples)
            {
                var stripped = SampleStrippedTerrain(terrainNatural, p.Station) - strip;
                var gap = stripped - p.Elevation;
                if (gap >= thresholds.MinStrippedTerrainToAxisGapM ||
                    gap < -50)
                {
                    continue;
                }

                /* Komşu tekrarlardan kaçın */
                if (lastWarn is not null &&
                    Math.Abs(lastWarn.Station - p.Station) < 2)
                {
                    continue;
                }

                msgs.Add($"KM {p.Station:0.###}: Kot çizgisi sıyırılmış araziye yakın (~{gap:0.##} m).");
                lastWarn = p;
            }
        }

        return DistinctPreserveOrder(msgs);
    }

    private static double SampleStrippedTerrain(IReadOnlyList<ProfilePoint> terrain, double station)
    {
        return ProfileInterpolation.InterpolateStationElevation(terrain, station);
    }

    private static List<string> DistinctPreserveOrder(List<string> msgs)
    {
        var seen = new HashSet<string>();
        var outList = new List<string>(msgs.Count);
        foreach (var m in msgs)
        {
            if (seen.Add(m))
            {
                outList.Add(m);
            }
        }

        return outList;
    }
}
