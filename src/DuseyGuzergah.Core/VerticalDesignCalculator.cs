namespace DuseyGuzergah;

public static class VerticalDesignCalculator
{
    private const double LiftClearance = 0.05;
    private const double MinCoverViolationTolerance = 0.05;
    private const double TargetDepthPenaltyWeight = 8.0;
    /// <summary>Ham zemin ustunde ust tampon (daha sig): hedef derinlik - ust tampon kadar burial.</summary>
    private const double ShallowBurialMargin = 0.20;
    /// <summary>Birlestirme: hedef gomuye gore izin verilen ek gomu ust siniri; koridor genisse bile bu ust sinir ile hedefe yakin kalinir.</summary>
    private const double MaxMergeBurialBeyondTargetCap = 0.42;
    /// <summary>Dar girinti köprülemede (nominal tıraş) uyarı eşiği; bu kadar nominal kaldırıldıysa çukurda dolgu kabulü.</summary>
    private const double NarrowBridgeLiftThreshold = 0.035;
    /// <summary>Köprülenen istasyonlarda minimum gömü uyarısı için ek tolerans (m); çukurda planlanan dolgu ile uyumlu.</summary>
    private const double NarrowDipBurialRelaxation = 0.42;

    /// <summary>Bir somedeki kurp teğeti, komşu bacağın en fazla bu oranını kullanır (KTB okuyucusundaki 0,49 sınırıyla aynı).</summary>
    private const double MaxTangentShare = 0.49;
    private const double CrossingRampSearchMax = 30.0;
    private const double CrossingRampStep = 0.10;
    private const int CoverRepairRounds = 3;

    public static DesignProfileResult Calculate(IReadOnlyList<ProfilePoint> terrainProfile, DesignParameters parameters)
    {
        var result = new DesignProfileResult();
        if (terrainProfile.Count == 0)
        {
            return result;
        }

        var orderedTerrain = terrainProfile.OrderBy(point => point.Station).ToList();

        var nominalBridgeLiftProfile = BuildCoverEnvelopeAndBridgeLift(
            orderedTerrain,
            parameters,
            result);
        var envelope = result.CoverEnvelope;

        var coarseDpBase = Math.Max(parameters.CoverTolerance * 4, 0.20);
        var dpTolerance = Math.Max(coarseDpBase * 1.28, 0.38);
        var majorBreakpoints = SimplifyTerrainProfile(orderedTerrain, dpTolerance);
        var maximumCover = Math.Max(parameters.MaximumCover, parameters.TargetCover);
        var minAcceptableBurialDepth = BaseMinBurial(parameters);
        var minimumRadius = EffectiveMinimumRadius(parameters);

        var (points, locked) = BuildAlignmentPoints(envelope, majorBreakpoints, parameters, maximumCover, minimumRadius);
        points = EnforceRadiusLimits(points, locked, envelope, parameters, minimumRadius);

        var pipeTopVertices = BuildVertices(points, parameters, minimumRadius).ToList();
        var pipeTopCurve = KtbReader.BuildVerticalCurve(pipeTopVertices);

        for (var round = 0; round < CoverRepairRounds; round++)
        {
            var issues = CheckBurialDepth(
                    pipeTopCurve,
                    envelope,
                    parameters,
                    minAcceptableBurialDepth,
                    nominalBridgeLiftProfile)
                .ToList();
            if (issues.Count == 0)
            {
                break;
            }

            var lowered = LowerOnlyShallowSpans(
                points,
                locked,
                issues,
                minAcceptableBurialDepth,
                nominalBridgeLiftProfile,
                parameters.TargetCover);
            if (!lowered.Where((point, index) => point.Elevation < points[index].Elevation - 1e-6).Any())
            {
                break;
            }

            points = EnforceRadiusLimits(lowered, locked, envelope, parameters, minimumRadius);
            pipeTopVertices = BuildVertices(points, parameters, minimumRadius).ToList();
            pipeTopCurve = KtbReader.BuildVerticalCurve(pipeTopVertices);
        }

        result.CoverIssues.AddRange(
            CheckBurialDepth(
                pipeTopCurve,
                envelope,
                parameters,
                minAcceptableBurialDepth,
                nominalBridgeLiftProfile));

        result.AxisOffset = Math.Max(0, parameters.PipeDiameter) / 2;
        result.Vertices.AddRange(pipeTopVertices.Select(vertex => vertex with { Elevation = vertex.Elevation - result.AxisOffset }));
        result.Curve.AddRange(KtbReader.BuildVerticalCurve(result.Vertices));
        result.Guides.AddRange(KtbReader.BuildVerticalCurveGuides(result.Vertices));

        return result;
    }

    /// <summary>
    /// Kurpların uyması gereken en küçük yarıçap: soğuk büküm tablosu ile boru boyu başına izin verilen
    /// sapma açısından (R = boru boyu / açı) büyük olanı.
    /// </summary>
    public static double EffectiveMinimumRadius(DesignParameters parameters)
    {
        var radius = Math.Max(0, parameters.MinimumRadius);
        if (parameters.MaxDeflectionDeg > 0 && parameters.PipeLength > 0)
        {
            radius = Math.Max(radius, parameters.PipeLength / (parameters.MaxDeflectionDeg * Math.PI / 180));
        }

        return radius;
    }

    private static (List<ProfilePoint> Points, HashSet<double> Locked) BuildAlignmentPoints(
        IReadOnlyList<CoverEnvelopePoint> envelope,
        IReadOnlyList<ProfilePoint> majorBreakpoints,
        DesignParameters parameters,
        double maximumCover,
        double minimumRadius)
    {
        var locked = new HashSet<double>();
        var points = new List<ProfilePoint>();
        ProfilePoint? spanStart = null;

        foreach (var plan in PlanCrossings(envelope, parameters, minimumRadius))
        {
            AppendDistinct(points, OptimizeSpan(envelope, majorBreakpoints, parameters, maximumCover, minimumRadius, spanStart, plan.LeftRampEnd ?? plan.LeftPi));
            AppendDistinct(points, [plan.LeftPi, plan.RightPi]);
            foreach (var point in new[] { plan.LeftRampEnd, plan.LeftPi, plan.RightPi, plan.RightRampEnd }.OfType<ProfilePoint>())
            {
                locked.Add(point.Station);
            }

            spanStart = plan.RightRampEnd ?? plan.RightPi;
        }

        AppendDistinct(points, OptimizeSpan(envelope, majorBreakpoints, parameters, maximumCover, minimumRadius, spanStart, null));
        return (points, locked);
    }

    private static void AppendDistinct(List<ProfilePoint> target, IEnumerable<ProfilePoint> source)
    {
        foreach (var point in source)
        {
            if (target.Count > 0 && point.Station <= target[^1].Station + 0.05)
            {
                continue;
            }

            target.Add(point);
        }
    }

    /// <summary>
    /// Serbest aralık için mevcut sadeleştirme zinciri. Uçlar verilmişse (özel geçiş rampası) o kotlara sabitlenir.
    /// </summary>
    private static List<ProfilePoint> OptimizeSpan(
        IReadOnlyList<CoverEnvelopePoint> envelope,
        IReadOnlyList<ProfilePoint> majorBreakpoints,
        DesignParameters parameters,
        double maximumCover,
        double minimumRadius,
        ProfilePoint? start,
        ProfilePoint? end)
    {
        var startStation = start?.Station ?? envelope[0].Station;
        var endStation = end?.Station ?? envelope[^1].Station;
        if (endStation - startStation < 0.5)
        {
            return new[] { start, end }.OfType<ProfilePoint>().ToList();
        }

        var span = BuildSpanEnvelope(envelope, startStation, endStation, start?.Elevation, end?.Elevation);
        var breakpoints = majorBreakpoints
            .Where(point => point.Station > startStation && point.Station < endStation)
            .ToList();
        var chordBurialSlack = ComputeChordBurialSlack(parameters.TargetCover, maximumCover);

        var points = SelectPrimaryIntersections(
            span,
            breakpoints,
            parameters.TargetCover,
            maximumCover,
            chordBurialSlack);
        points = LiftToShallowestAllowed(points, span);
        points = CollapseRedundantVertices(
            points,
            span,
            parameters.MinimumSegmentLength,
            parameters.TargetCover,
            maximumCover,
            parameters.CoverTolerance);
        points = RefinePolylineAgainstNominalBurial(
            points,
            span,
            parameters.TargetCover,
            Math.Max(0.10, chordBurialSlack - 0.08),
            parameters.MinimumSegmentLength);
        points = RefinePolylineAgainstNominalBurial(
            points,
            span,
            parameters.TargetCover,
            0.08,
            parameters.MinimumSegmentLength);
        points = LiftToShallowestAllowed(points, span);
        points = SnapPolylineUsingVerticalCurveBurial(
            points,
            span,
            parameters,
            minimumRadius,
            burialSlackBeyondTarget: 0.10);
        points = PruneDenseVertices(points, span, parameters, maximumCover);
        points = RestoreCutCrests(points, span);

        if (start is not null)
        {
            points[0] = start;
        }

        if (end is not null)
        {
            points[^1] = end;
        }

        return points;
    }

    private static List<CoverEnvelopePoint> BuildSpanEnvelope(
        IReadOnlyList<CoverEnvelopePoint> envelope,
        double startStation,
        double endStation,
        double? startElevation,
        double? endElevation)
    {
        var span = new List<CoverEnvelopePoint> { PinEnvelope(EnvelopeAt(envelope, startStation) with { Station = startStation }, startElevation) };
        span.AddRange(envelope.Where(point => point.Station > startStation + 1e-6 && point.Station < endStation - 1e-6));
        span.Add(PinEnvelope(EnvelopeAt(envelope, endStation) with { Station = endStation }, endElevation));
        return span;
    }

    private static CoverEnvelopePoint PinEnvelope(CoverEnvelopePoint point, double? elevation)
    {
        if (elevation is not double z)
        {
            return point;
        }

        return point with
        {
            NominalPipeTopElevation = z,
            PreferredLowerElevation = Math.Min(point.PreferredLowerElevation, z),
            PreferredUpperElevation = Math.Max(point.PreferredUpperElevation, z),
            MinimumCoverUpperElevation = Math.Max(point.MinimumCoverUpperElevation, z + LiftClearance)
        };
    }

    private sealed record CrossingPlan(
        ProfilePoint? LeftRampEnd,
        ProfilePoint LeftPi,
        ProfilePoint RightPi,
        ProfilePoint? RightRampEnd);

    /// <summary>
    /// Her özel geçiş için: geçiş ve iki yanındaki tampon boyunca düz boru, iki uçta koridora bağlanan
    /// en az <see cref="DesignParameters.CrossingRampMinM"/> uzunluğunda rampa. Rampalar birbirine binecek kadar
    /// yakın geçişler tek düz bölümde birleştirilir. Düz bölüm uçları ve rampa uçları kilitlidir.
    /// </summary>
    private static List<CrossingPlan> PlanCrossings(
        IReadOnlyList<CoverEnvelopePoint> envelope,
        DesignParameters parameters,
        double minimumRadius)
    {
        var plans = new List<CrossingPlan>();
        if (parameters.Crossings is not { Count: > 0 } crossings || envelope.Count < 2)
        {
            return plans;
        }

        var first = envelope[0].Station;
        var last = envelope[^1].Station;
        var buffer = Math.Max(0, parameters.CrossingBufferM);
        var mergeGap = 2 * (CrossingRampSearchMax + parameters.PipeLength);

        var zones = new List<(double Start, double End)>();
        foreach (var crossing in crossings
                     .Select(item => (Start: Math.Min(item.StartStation, item.EndStation) - buffer, End: Math.Max(item.StartStation, item.EndStation) + buffer))
                     .Where(item => item.End > first && item.Start < last)
                     .OrderBy(item => item.Start))
        {
            if (zones.Count > 0 && crossing.Start - zones[^1].End < mergeGap)
            {
                zones[^1] = (zones[^1].Start, Math.Max(zones[^1].End, crossing.End));
                continue;
            }

            zones.Add(crossing);
        }

        var boundary = first;
        foreach (var zone in zones)
        {
            var flatStart = Math.Max(zone.Start, first);
            var flatEnd = Math.Min(zone.End, last);
            var flatTop = FlatCrossingTop(envelope, flatStart, flatEnd);

            var (leftPi, leftRamp) = flatStart <= first + 1e-6
                ? (new ProfilePoint(first, flatTop), null)
                : PlanRamp(envelope, flatTop, flatStart, -1, boundary, parameters, minimumRadius);
            var (rightPi, rightRamp) = flatEnd >= last - 1e-6
                ? (new ProfilePoint(last, flatTop), null)
                : PlanRamp(envelope, flatTop, flatEnd, 1, last, parameters, minimumRadius);

            plans.Add(new CrossingPlan(leftRamp, leftPi, rightPi, rightRamp));
            boundary = (rightRamp ?? rightPi).Station;
        }

        return plans;
    }

    private static double FlatCrossingTop(IReadOnlyList<CoverEnvelopePoint> envelope, double start, double end)
    {
        var top = Math.Min(
            EnvelopeAt(envelope, start).MinimumCoverUpperElevation,
            EnvelopeAt(envelope, end).MinimumCoverUpperElevation);
        for (var i = LowerBoundStation(envelope, start); i < envelope.Count && envelope[i].Station <= end; i++)
        {
            top = Math.Min(top, envelope[i].MinimumCoverUpperElevation);
        }

        return top - LiftClearance;
    }

    /// <summary>
    /// Düz bölüm ucundaki kilitli someyi ve koridora bağlanan rampa ucunu bulur. Python V33.1 ile aynı arama:
    /// rampa en az asgari boyda başlar, eğim izin verilen sapma açısına inince durur; inmezse en yatık nokta alınır.
    /// Kilitli some, kurbu düz bölüme taşmayacak kadar dışarı (kurp teğeti kadar) konur.
    /// </summary>
    private static (ProfilePoint Pi, ProfilePoint? RampEnd) PlanRamp(
        IReadOnlyList<CoverEnvelopePoint> envelope,
        double flatTop,
        double flatEdge,
        int direction,
        double boundary,
        DesignParameters parameters,
        double minimumRadius)
    {
        var rampMin = Math.Max(1.0, parameters.CrossingRampMinM);
        var maxSlope = parameters.MaxDeflectionDeg > 0
            ? Math.Tan(parameters.MaxDeflectionDeg * Math.PI / 180)
            : double.PositiveInfinity;
        var tangent = parameters.PipeLength / 2;
        var pi = new ProfilePoint(flatEdge, flatTop);
        ProfilePoint? rampEnd = null;

        for (var pass = 0; pass < 4; pass++)
        {
            var piStation = flatEdge + direction * tangent;
            if (direction < 0 ? piStation <= boundary + 1 : piStation >= boundary - 1)
            {
                return (new ProfilePoint(flatEdge, flatTop), null);
            }

            pi = new ProfilePoint(piStation, flatTop);
            var dh = Math.Abs(flatTop - CorridorLevelNear(envelope, piStation, flatTop));
            var theoretical = double.IsPositiveInfinity(maxSlope) ? rampMin : dh / maxSlope;
            var searchMax = Math.Max(rampMin + 2, Math.Min(CrossingRampSearchMax, theoretical + 6));

            ProfilePoint? best = null;
            var bestSlope = double.PositiveInfinity;
            for (var length = rampMin; length <= searchMax + 1e-9; length += CrossingRampStep)
            {
                var station = piStation + direction * length;
                if (direction < 0 ? station <= boundary + 1 : station >= boundary - 1)
                {
                    break;
                }

                var level = CorridorLevelNear(envelope, station, flatTop);
                var slope = Math.Abs(flatTop - level) / length;
                if (slope < bestSlope)
                {
                    bestSlope = slope;
                    best = new ProfilePoint(station, level);
                }

                if (slope <= maxSlope + 1e-9)
                {
                    break;
                }
            }

            if (best is null)
            {
                return (pi, null);
            }

            rampEnd = best;
            var nextTangent = Math.Max(parameters.PipeLength, minimumRadius * bestSlope) / 2;
            if (Math.Abs(nextTangent - tangent) < 0.01)
            {
                break;
            }

            tangent = nextTangent;
        }

        return (pi, rampEnd);
    }

    private static double CorridorLevelNear(IReadOnlyList<CoverEnvelopePoint> envelope, double station, double elevation)
    {
        var point = EnvelopeAt(envelope, station);
        var upper = point.MinimumCoverUpperElevation - LiftClearance;
        var lower = Math.Min(point.PreferredLowerElevation, upper);
        return Math.Clamp(elevation, lower, upper);
    }

    /// <summary>
    /// Önce sapma açısından gelen yarıçap zorlanır; arazinin izin vermediği yerlerde (sıcak büküm adayı) en azından
    /// soğuk büküm tablosundaki yarıçap korunur.
    /// </summary>
    private static List<ProfilePoint> EnforceRadiusLimits(
        List<ProfilePoint> points,
        HashSet<double> locked,
        IReadOnlyList<CoverEnvelopePoint> envelope,
        DesignParameters parameters,
        double minimumRadius)
    {
        points = EnforceMinimumRadius(points, locked, envelope, minimumRadius);
        var coldBendRadius = Math.Max(0, parameters.MinimumRadius);
        return coldBendRadius > 0 && coldBendRadius < minimumRadius - 1e-6
            ? EnforceMinimumRadius(points, locked, envelope, coldBendRadius)
            : points;
    }

    /// <summary>
    /// Her ara somede R·Δg / 2 teğetinin iki bacağa da sığmasını zorlar. Önce some silinir (komşular arası kiriş
    /// minimum örtü ile en derin koridor arasında kalıyorsa); silinemezse someler yalnızca aşağı itilir: tepede
    /// some, çukurda komşular. Kilitli (özel geçiş) somelerin kotu değişmez.
    /// </summary>
    private static List<ProfilePoint> EnforceMinimumRadius(
        List<ProfilePoint> points,
        HashSet<double> locked,
        IReadOnlyList<CoverEnvelopePoint> envelope,
        double minimumRadius)
    {
        if (minimumRadius <= 0 || points.Count < 3)
        {
            return points;
        }

        var work = points.ToList();

        for (var pass = 0; pass < 80; pass++)
        {
            var changed = false;
            for (var i = 1; i < work.Count - 1; i++)
            {
                var prev = work[i - 1];
                var curr = work[i];
                var next = work[i + 1];
                var legIn = curr.Station - prev.Station;
                var legOut = next.Station - curr.Station;
                if (legIn <= 1e-6 || legOut <= 1e-6)
                {
                    continue;
                }

                var delta = (next.Elevation - curr.Elevation) / legOut - (curr.Elevation - prev.Elevation) / legIn;
                var allowed = 2 * MaxTangentShare * Math.Min(legIn, legOut) / minimumRadius;
                var excess = Math.Abs(delta) - allowed;
                if (excess <= 1e-6)
                {
                    continue;
                }

                if (!locked.Contains(curr.Station) && IsLineInsideCorridor(prev, next, envelope))
                {
                    work.RemoveAt(i);
                    i--;
                    changed = true;
                    continue;
                }

                if (delta < 0)
                {
                    if (!locked.Contains(curr.Station))
                    {
                        var drop = Math.Min(excess / (1 / legIn + 1 / legOut), curr.Elevation - FloorAt(envelope, curr.Station));
                        if (drop > 1e-4)
                        {
                            work[i] = curr with { Elevation = curr.Elevation - drop };
                            changed = true;
                            continue;
                        }
                    }
                }
                else
                {
                    var canPrev = !locked.Contains(prev.Station);
                    var canNext = !locked.Contains(next.Station);
                    var weight = (canPrev ? 1 / legIn : 0) + (canNext ? 1 / legOut : 0);
                    if (weight > 0)
                    {
                        var drop = excess / weight;
                        var moved = false;
                        if (canPrev)
                        {
                            var d = Math.Min(drop, prev.Elevation - FloorAt(envelope, prev.Station));
                            if (d > 1e-4)
                            {
                                work[i - 1] = prev with { Elevation = prev.Elevation - d };
                                moved = true;
                            }
                        }

                        if (canNext)
                        {
                            var d = Math.Min(drop, next.Elevation - FloorAt(envelope, next.Station));
                            if (d > 1e-4)
                            {
                                work[i + 1] = next with { Elevation = next.Elevation - d };
                                moved = true;
                            }
                        }

                        if (moved)
                        {
                            changed = true;
                        }
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return work;
    }

    private static double FloorAt(IReadOnlyList<CoverEnvelopePoint> envelope, double station)
    {
        return EnvelopeAt(envelope, station).PreferredLowerElevation;
    }

    /// <summary>
    /// Ham nominal (sıyırılmış zemin − hedef) üzerinde istasyon doğrultusunda yarı-genlik
    /// <see cref="ComputeNarrowIndentBridgeHalfWidth"/> ile rolling-max: dar oyukların tabanı yan yamacın nominal kotuna
    /// çekilir; geniş vadiler (&gt;~2×yarı-genlik) neredeyse etkilenmez. Hedef profil buna göre güncellenir.
    /// </summary>
    private static List<ProfilePoint> BuildCoverEnvelopeAndBridgeLift(
        IReadOnlyList<ProfilePoint> orderedTerrain,
        DesignParameters parameters,
        DesignProfileResult result)
    {
        var minBurialDepth = BaseMinBurial(parameters);
        var maxBurialDepth = Math.Max(parameters.MaximumCover, parameters.TargetCover);

        var n = orderedTerrain.Count;
        var stations = new double[n];
        var rawNominals = new double[n];
        var strippedVals = new double[n];

        for (var i = 0; i < n; i++)
        {
            var terrain = orderedTerrain[i];
            stations[i] = terrain.Station;
            strippedVals[i] = terrain.Elevation - parameters.StrippingDepth;
            rawNominals[i] = strippedVals[i] - parameters.TargetCover;
        }

        var bridgeHalfWidth = ComputeNarrowIndentBridgeHalfWidth(parameters);
        var bridgedNominals = RollingMaxNominalByStation(stations, rawNominals, bridgeHalfWidth);
        bridgedNominals = RollingMaxNominalByStation(stations, bridgedNominals, bridgeHalfWidth);
        var liftProfile = new List<ProfilePoint>(n);

        for (var i = 0; i < n; i++)
        {
            var terrainElevation = orderedTerrain[i].Elevation;
            var strippedRaw = strippedVals[i];
            var crossingCover = CrossingCoverAt(stations[i], parameters);
            var stationMinBurial = Math.Max(minBurialDepth, crossingCover);
            var stationMaxBurial = Math.Max(maxBurialDepth, crossingCover);
            var shallowPipeTopCeiling = strippedRaw - stationMinBurial;
            var deepPipeTopFloor = strippedRaw - stationMaxBurial;
            var zNom = Math.Clamp(bridgedNominals[i], deepPipeTopFloor, shallowPipeTopCeiling);
            if (crossingCover > 0)
            {
                zNom = Math.Min(zNom, strippedRaw - crossingCover);
            }
            var bridgeLift = Math.Max(0, bridgedNominals[i] - rawNominals[i]);

            result.CoverEnvelope.Add(new CoverEnvelopePoint(
                stations[i],
                terrainElevation,
                strippedRaw,
                zNom,
                deepPipeTopFloor,
                shallowPipeTopCeiling,
                shallowPipeTopCeiling));
            liftProfile.Add(new ProfilePoint(stations[i], bridgeLift));
            result.TargetProfile.Add(new ProfilePoint(stations[i], zNom));
            result.PreferredLowerProfile.Add(new ProfilePoint(stations[i], deepPipeTopFloor));
            result.PreferredUpperProfile.Add(new ProfilePoint(stations[i], shallowPipeTopCeiling));
            result.MinimumCoverUpperProfile.Add(new ProfilePoint(stations[i], shallowPipeTopCeiling));
        }

        return liftProfile;
    }

    private static double ComputeNarrowIndentBridgeHalfWidth(DesignParameters parameters)
    {
        var fromSeg = Math.Max(10.0, parameters.MinimumSegmentLength * 0.82);
        var fromPipe = parameters.PipeLength * 2.75;
        var fromRadiusCap = Math.Max(32.0, parameters.MinimumRadius * 0.38);
        return Math.Clamp(Math.Max(fromSeg, fromPipe), 14.0, fromRadiusCap);
    }

    private static double[] RollingMaxNominalByStation(double[] stations, double[] nominal, double bridgeHalfWidth)
    {
        var n = nominal.Length;
        var outNom = new double[n];
        for (var i = 0; i < n; i++)
        {
            var s0 = stations[i];
            var best = nominal[i];
            for (var j = i; j >= 0 && s0 - stations[j] <= bridgeHalfWidth; j--)
            {
                best = Math.Max(best, nominal[j]);
            }

            for (var j = i + 1; j < n && stations[j] - s0 <= bridgeHalfWidth; j++)
            {
                best = Math.Max(best, nominal[j]);
            }

            outNom[i] = best;
        }

        return outNom;
    }

    private static double EffectiveMinBurialForStation(
        double station,
        double baseMinimumBurial,
        IReadOnlyList<ProfilePoint>? nominalBridgeLiftProfile)
    {
        if (nominalBridgeLiftProfile is null || nominalBridgeLiftProfile.Count == 0)
        {
            return baseMinimumBurial;
        }

        var bridgeLift = InterpolateProfileElevation(nominalBridgeLiftProfile, station);

        if (bridgeLift >= NarrowBridgeLiftThreshold)
        {
            return Math.Max(0.35, baseMinimumBurial - NarrowDipBurialRelaxation);
        }

        return baseMinimumBurial;
    }

    private static List<ProfilePoint> SimplifyTerrainProfile(IReadOnlyList<ProfilePoint> terrain, double tolerance)
    {
        if (terrain.Count <= 2)
        {
            return terrain.ToList();
        }

        var keep = new bool[terrain.Count];
        keep[0] = true;
        keep[^1] = true;
        SimplifyTerrainRange(terrain, 0, terrain.Count - 1, tolerance, keep);

        return terrain.Where((_, index) => keep[index]).ToList();
    }

    private static void SimplifyTerrainRange(
        IReadOnlyList<ProfilePoint> terrain,
        int startIndex,
        int endIndex,
        double tolerance,
        bool[] keep)
    {
        if (endIndex <= startIndex + 1)
        {
            return;
        }

        var start = terrain[startIndex];
        var end = terrain[endIndex];
        var maxDistance = 0d;
        var maxIndex = -1;

        for (var i = startIndex + 1; i < endIndex; i++)
        {
            var expectedElevation = Interpolate(start, end, terrain[i].Station);
            var distance = Math.Abs(terrain[i].Elevation - expectedElevation);
            if (distance > maxDistance)
            {
                maxDistance = distance;
                maxIndex = i;
            }
        }

        if (maxIndex >= 0 && maxDistance > tolerance)
        {
            keep[maxIndex] = true;
            SimplifyTerrainRange(terrain, startIndex, maxIndex, tolerance, keep);
            SimplifyTerrainRange(terrain, maxIndex, endIndex, tolerance, keep);
        }
    }

    private static double InterpolateProfileElevation(IReadOnlyList<ProfilePoint> profile, double station)
    {
        if (station <= profile[0].Station)
        {
            return profile[0].Elevation;
        }

        if (station >= profile[^1].Station)
        {
            return profile[^1].Elevation;
        }

        for (var i = 1; i < profile.Count; i++)
        {
            var left = profile[i - 1];
            var right = profile[i];
            if (station <= right.Station)
            {
                return Interpolate(left, right, station);
            }
        }

        return profile[^1].Elevation;
    }

    private static double Interpolate(ProfilePoint start, ProfilePoint end, double station)
    {
        var ratio = (station - start.Station) / (end.Station - start.Station);
        return start.Elevation + (end.Elevation - start.Elevation) * ratio;
    }

    private static double ComputeChordBurialSlack(double targetBurialDepth, double maximumBurialDepth)
    {
        var extraBurialReserve = Math.Max(0.01, maximumBurialDepth - targetBurialDepth);
        return Math.Clamp(
            0.30 - 0.045 * extraBurialReserve,
            0.14,
            0.29);
    }

    /// <summary>
    /// Kiriş (PI-duzlari) hakkinda uygun görünürken Hermite düşey kurpta ve uzun aliymanda ara noktalarda
    /// gomuler hedef+gönyegene fazla inebilir. Ornek egriye göre nominal kotu zorlayarak PI ekleriz.
    /// </summary>
    private static List<ProfilePoint> SnapPolylineUsingVerticalCurveBurial(
        IReadOnlyList<ProfilePoint> points,
        IReadOnlyList<CoverEnvelopePoint> envelope,
        DesignParameters parameters,
        double minimumRadius,
        double burialSlackBeyondTarget)
    {
        if (points.Count < 2 || envelope.Count == 0)
        {
            return points.ToList();
        }

        var work = points.OrderBy(point => point.Station).Select(point => new ProfilePoint(point.Station, point.Elevation)).ToList();
        const int maxIterations = 90;
        var minNeighborGap = Math.Max(2.5, parameters.PipeLength * 0.18);

        for (var iter = 0; iter < maxIterations; iter++)
        {
            var vertices = BuildVertices(work, parameters, minimumRadius).ToList();
            var curveSamples = KtbReader.BuildVerticalCurve(vertices);
            if (curveSamples.Count == 0)
            {
                break;
            }

            var worstStation = double.NaN;
            var worstViolation = 0d;

            foreach (var sample in curveSamples)
            {
                var designNominal = InterpolateEnvelopeValue(envelope, sample.Station, p => p.NominalPipeTopElevation);
                var violation = designNominal - sample.Elevation - burialSlackBeyondTarget;

                if (violation > worstViolation && violation > MinCoverViolationTolerance)
                {
                    worstViolation = violation;
                    worstStation = sample.Station;
                }
            }

            if (double.IsNaN(worstStation))
            {
                break;
            }

            var nominalZ = InterpolateEnvelopeValue(envelope, worstStation, point => point.NominalPipeTopElevation);
            var lowerZ = InterpolateEnvelopeValue(envelope, worstStation, point => point.PreferredLowerElevation);
            var upperZ = InterpolateEnvelopeValue(envelope, worstStation, point => point.PreferredUpperElevation);
            var z = Math.Clamp(nominalZ, lowerZ, upperZ);

            if (work.Any(p => Math.Abs(p.Station - worstStation) < minNeighborGap))
            {
                var nearest = work.OrderBy(p => Math.Abs(p.Station - worstStation)).First();
                var idx = work.IndexOf(nearest);
                work[idx] = new ProfilePoint(nearest.Station, Math.Max(nearest.Elevation, z));
            }
            else
            {
                work.Add(new ProfilePoint(worstStation, z));
                work.Sort((a, b) => a.Station.CompareTo(b.Station));
            }

            work = LiftToShallowestAllowed(work, envelope);
        }

        return work;
    }

    private static List<ProfilePoint> RefinePolylineAgainstNominalBurial(
        IReadOnlyList<ProfilePoint> points,
        IReadOnlyList<CoverEnvelopePoint> envelope,
        double targetBurialDepth,
        double burialSlackBeyondTarget,
        double requestedMinimumSegmentLength)
    {
        if (points.Count < 2 || envelope.Count == 0)
        {
            return points.ToList();
        }

        var minLeg = Math.Max(10.5, requestedMinimumSegmentLength * 0.45);
        var result = points.ToList();
        var safety = 0;
        while (safety++ < 4000)
        {
            var changed = false;
            for (var i = 0; i < result.Count - 1; i++)
            {
                var a = result[i];
                var b = result[i + 1];
                var worst = FindWorstBurialExcessAlongActualChord(a, b, envelope, burialSlackBeyondTarget);
                if (worst.EnvelopeIndex < 0)
                {
                    continue;
                }

                var ep = envelope[worst.EnvelopeIndex];
                if (ep.Station - a.Station < minLeg || b.Station - ep.Station < minLeg)
                {
                    continue;
                }

                var z = Math.Clamp(ep.NominalPipeTopElevation, ep.PreferredLowerElevation, ep.PreferredUpperElevation);
                result.Insert(i + 1, new ProfilePoint(ep.Station, z));
                changed = true;
                break;
            }

            if (!changed)
            {
                break;
            }
        }

        return result;
    }

    private static (int EnvelopeIndex, double Violation) FindWorstBurialExcessAlongActualChord(
        ProfilePoint a,
        ProfilePoint b,
        IReadOnlyList<CoverEnvelopePoint> envelope,
        double burialSlackBeyondTarget)
    {
        var range = b.Station - a.Station;
        if (range <= 0)
        {
            return (-1, 0);
        }

        var worstIndex = -1;
        var worstViolation = 0d;

        for (var i = 0; i < envelope.Count; i++)
        {
            var ep = envelope[i];
            if (ep.Station <= a.Station || ep.Station >= b.Station)
            {
                continue;
            }

            var t = (ep.Station - a.Station) / range;
            var chordZ = a.Elevation + (b.Elevation - a.Elevation) * t;
            var violation = ep.NominalPipeTopElevation - chordZ - burialSlackBeyondTarget;

            if (violation > worstViolation)
            {
                worstViolation = violation;
                worstIndex = i;
            }
        }

        return worstViolation > MinCoverViolationTolerance ? (worstIndex, worstViolation) : (-1, 0);
    }

    private static List<ProfilePoint> SelectPrimaryIntersections(
        IReadOnlyList<CoverEnvelopePoint> envelope,
        IReadOnlyList<ProfilePoint> majorBreakpoints,
        double targetBurialDepth,
        double maximumBurialDepth,
        double chordBurialSlack)
    {
        if (envelope.Count == 0)
        {
            return [];
        }

        var keep = new bool[envelope.Count];
        keep[0] = true;
        keep[^1] = true;

        foreach (var breakpoint in majorBreakpoints)
        {
            var idx = FindEnvelopeIndexAtStation(envelope, breakpoint.Station);
            if (idx >= 0)
            {
                keep[idx] = true;
            }
        }

        var changed = true;
        var safetyCounter = 0;
        while (changed && safetyCounter < 1000)
        {
            changed = false;
            safetyCounter++;

            var keptIndices = new List<int>();
            for (var i = 0; i < envelope.Count; i++)
            {
                if (keep[i])
                {
                    keptIndices.Add(i);
                }
            }

            for (var k = 0; k < keptIndices.Count - 1; k++)
            {
                var startIdx = keptIndices[k];
                var endIdx = keptIndices[k + 1];
                var corridor = FindWorstCorridorChordViolation(envelope, startIdx, endIdx);
                var depthBudget = FindWorstChordBurialExcessViolation(
                    envelope,
                    startIdx,
                    endIdx,
                    chordBurialSlack);

                var corridorScore = corridor.Violation;
                var depthScore = depthBudget.Violation;
                const double depthViolationWeight = 1.55;

                var tol = MinCoverViolationTolerance;
                var preferDepth = depthScore * depthViolationWeight >= corridorScore;

                if (preferDepth)
                {
                    if (depthBudget.Index >= 0 && depthBudget.Violation > tol)
                    {
                        keep[depthBudget.Index] = true;
                        changed = true;
                    }
                    else if (corridor.Index >= 0 && corridor.Violation > tol)
                    {
                        keep[corridor.Index] = true;
                        changed = true;
                    }
                }
                else
                {
                    if (corridor.Index >= 0 && corridor.Violation > tol)
                    {
                        keep[corridor.Index] = true;
                        changed = true;
                    }
                    else if (depthBudget.Index >= 0 && depthBudget.Violation > tol)
                    {
                        keep[depthBudget.Index] = true;
                        changed = true;
                    }
                }
            }
        }

        var result = new List<ProfilePoint>();
        for (var i = 0; i < envelope.Count; i++)
        {
            if (keep[i])
            {
                var ep = envelope[i];
                var z = Math.Clamp(ep.NominalPipeTopElevation, ep.PreferredLowerElevation, ep.PreferredUpperElevation);
                result.Add(new ProfilePoint(ep.Station, z));
            }
        }

        return result;
    }

    private static (int Index, double Violation) FindWorstCorridorChordViolation(
        IReadOnlyList<CoverEnvelopePoint> envelope,
        int startIndex,
        int endIndex)
    {
        if (endIndex - startIndex <= 1)
        {
            return (-1, 0);
        }

        var start = envelope[startIndex];
        var end = envelope[endIndex];
        var range = end.Station - start.Station;
        if (range <= 0)
        {
            return (-1, 0);
        }

        var z0 = Math.Clamp(start.NominalPipeTopElevation, start.PreferredLowerElevation, start.PreferredUpperElevation);
        var z1 = Math.Clamp(end.NominalPipeTopElevation, end.PreferredLowerElevation, end.PreferredUpperElevation);

        var worstIndex = -1;
        var worstViolation = 0d;

        for (var i = startIndex + 1; i < endIndex; i++)
        {
            var point = envelope[i];
            var ratio = (point.Station - start.Station) / range;
            var segmentElevation = z0 + (z1 - z0) * ratio;

            var shallowCap = point.MinimumCoverUpperElevation - LiftClearance;
            var deepFloor = point.PreferredLowerElevation;

            var tooShallow = segmentElevation - shallowCap;
            var tooDeep = deepFloor - segmentElevation;
            var violation = Math.Max(tooShallow > 0 ? tooShallow : 0, tooDeep > 0 ? tooDeep : 0);

            if (violation > worstViolation)
            {
                worstViolation = violation;
                worstIndex = i;
            }
        }

        return worstViolation > MinCoverViolationTolerance ? (worstIndex, worstViolation) : (-1, 0);
    }

    /// <summary>
    /// İki some arası kiriş, köprülenmiş nominal hedef paraleline göre ne kadar asagida kaliyor — ham zemin oyugundan degil ölçülür.
    /// </summary>
    private static (int Index, double Violation) FindWorstChordBurialExcessViolation(
        IReadOnlyList<CoverEnvelopePoint> envelope,
        int startIndex,
        int endIndex,
        double burialSlackBeyondTarget)
    {
        if (endIndex - startIndex <= 1)
        {
            return (-1, 0);
        }

        var start = envelope[startIndex];
        var end = envelope[endIndex];
        var range = end.Station - start.Station;
        if (range <= 0)
        {
            return (-1, 0);
        }

        var z0 = Math.Clamp(start.NominalPipeTopElevation, start.PreferredLowerElevation, start.PreferredUpperElevation);
        var z1 = Math.Clamp(end.NominalPipeTopElevation, end.PreferredLowerElevation, end.PreferredUpperElevation);

        var worstIndex = -1;
        var worstViolation = 0d;

        for (var i = startIndex + 1; i < endIndex; i++)
        {
            var point = envelope[i];
            var ratio = (point.Station - start.Station) / range;
            var chordZ = z0 + (z1 - z0) * ratio;
            var violation = point.NominalPipeTopElevation - chordZ - burialSlackBeyondTarget;

            if (violation > worstViolation)
            {
                worstViolation = violation;
                worstIndex = i;
            }
        }

        return worstViolation > MinCoverViolationTolerance ? (worstIndex, worstViolation) : (-1, 0);
    }

    private static int FindEnvelopeIndexAtStation(IReadOnlyList<CoverEnvelopePoint> envelope, double station)
    {
        var bestIdx = -1;
        var bestDistance = double.PositiveInfinity;
        for (var i = 0; i < envelope.Count; i++)
        {
            var d = Math.Abs(envelope[i].Station - station);
            if (d < bestDistance)
            {
                bestDistance = d;
                bestIdx = i;
            }
        }

        return bestDistance < 0.001 ? bestIdx : -1;
    }

    private static List<ProfilePoint> LiftToShallowestAllowed(
        IReadOnlyList<ProfilePoint> points,
        IReadOnlyList<CoverEnvelopePoint> envelope)
    {
        var result = points.Select(point => new ProfilePoint(point.Station, point.Elevation)).ToList();
        if (result.Count == 0)
        {
            return result;
        }

        var changed = true;
        var iterations = 0;
        while (changed && iterations < 30)
        {
            changed = false;
            iterations++;

            for (var i = 0; i < result.Count; i++)
            {
                var lift = ComputeMaxLift(result, envelope, i);
                if (lift > 0.001)
                {
                    result[i] = new ProfilePoint(result[i].Station, result[i].Elevation + lift);
                    changed = true;
                }
            }
        }

        return result;
    }

    private static double ComputeMaxLift(
        IReadOnlyList<ProfilePoint> points,
        IReadOnlyList<CoverEnvelopePoint> envelope,
        int index)
    {
        var current = points[index];
        var nominal = InterpolateEnvelopeValue(envelope, current.Station, point => point.NominalPipeTopElevation);
        var lift = nominal - current.Elevation;
        if (lift <= 0)
        {
            return 0;
        }

        if (index > 0)
        {
            lift = Math.Min(lift, ComputeMaxLiftForRightEnd(points[index - 1], current, envelope));
        }

        if (index < points.Count - 1)
        {
            lift = Math.Min(lift, ComputeMaxLiftForLeftEnd(current, points[index + 1], envelope));
        }

        return Math.Max(0, lift);
    }

    private static double ComputeMaxLiftForRightEnd(
        ProfilePoint start,
        ProfilePoint end,
        IReadOnlyList<CoverEnvelopePoint> envelope)
    {
        var range = end.Station - start.Station;
        if (range <= 0)
        {
            return double.PositiveInfinity;
        }

        var lift = double.PositiveInfinity;
        foreach (var ep in envelope)
        {
            if (ep.Station <= start.Station || ep.Station >= end.Station)
            {
                continue;
            }

            var t = (ep.Station - start.Station) / range;
            if (t < 0.0001)
            {
                continue;
            }

            var currentElevation = start.Elevation + t * (end.Elevation - start.Elevation);
            var maxAllowed = ep.MinimumCoverUpperElevation - LiftClearance;
            lift = Math.Min(lift, (maxAllowed - currentElevation) / t);
        }

        return lift;
    }

    private static double ComputeMaxLiftForLeftEnd(
        ProfilePoint start,
        ProfilePoint end,
        IReadOnlyList<CoverEnvelopePoint> envelope)
    {
        var range = end.Station - start.Station;
        if (range <= 0)
        {
            return double.PositiveInfinity;
        }

        var lift = double.PositiveInfinity;
        foreach (var ep in envelope)
        {
            if (ep.Station <= start.Station || ep.Station >= end.Station)
            {
                continue;
            }

            var t = (ep.Station - start.Station) / range;
            var oneMinusT = 1 - t;
            if (oneMinusT < 0.0001)
            {
                continue;
            }

            var currentElevation = start.Elevation + t * (end.Elevation - start.Elevation);
            var maxAllowed = ep.MinimumCoverUpperElevation - LiftClearance;
            lift = Math.Min(lift, (maxAllowed - currentElevation) / oneMinusT);
        }

        return lift;
    }

    private static double InterpolateEnvelopeValue(
        IReadOnlyList<CoverEnvelopePoint> envelope,
        double station,
        Func<CoverEnvelopePoint, double> selector)
    {
        if (station <= envelope[0].Station)
        {
            return selector(envelope[0]);
        }

        if (station >= envelope[^1].Station)
        {
            return selector(envelope[^1]);
        }

        for (var i = 1; i < envelope.Count; i++)
        {
            var left = envelope[i - 1];
            var right = envelope[i];
            if (station <= right.Station)
            {
                var ratio = (station - left.Station) / (right.Station - left.Station);
                return selector(left) + (selector(right) - selector(left)) * ratio;
            }
        }

        return selector(envelope[^1]);
    }

    private static List<ProfilePoint> CollapseRedundantVertices(
        IReadOnlyList<ProfilePoint> points,
        IReadOnlyList<CoverEnvelopePoint> envelope,
        double minimumSegmentLength,
        double targetCover,
        double maximumBurialDepth,
        double coverTolerance)
    {
        var result = points.ToList();
        var changed = true;

        while (changed && result.Count > 2)
        {
            changed = false;
            var bestIndex = -1;
            var bestScore = double.PositiveInfinity;

            for (var i = 1; i < result.Count - 1; i++)
            {
                var prev = result[i - 1];
                var curr = result[i];
                var next = result[i + 1];

                var incomingLength = curr.Station - prev.Station;
                var outgoingLength = next.Station - curr.Station;
                if (incomingLength <= 0 || outgoingLength <= 0)
                {
                    continue;
                }

                if (next.Station - prev.Station < minimumSegmentLength)
                {
                    continue;
                }

                if (!IsLineInsideCorridor(prev, next, envelope))
                {
                    continue;
                }

                var slopeIn = (curr.Elevation - prev.Elevation) / incomingLength;
                var slopeOut = (next.Elevation - curr.Elevation) / outgoingLength;
                var slopeDelta = Math.Abs(slopeOut - slopeIn);
                var extraDepth = ComputeExtraDepthVsBridgedNominal(prev, next, envelope);
                var mergeBurialBeyondTarget = Math.Min(MaxMergeBurialBeyondTargetCap, maximumBurialDepth - targetCover);
                var allowedExtraDepth = Math.Max(coverTolerance, mergeBurialBeyondTarget);

                if (extraDepth > allowedExtraDepth)
                {
                    continue;
                }

                var score = slopeDelta + extraDepth * TargetDepthPenaltyWeight;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
            {
                result.RemoveAt(bestIndex);
                changed = true;
            }
        }

        return result;
    }

    private static double ComputeExtraDepthVsBridgedNominal(
        ProfilePoint start,
        ProfilePoint end,
        IReadOnlyList<CoverEnvelopePoint> envelope)
    {
        var range = end.Station - start.Station;
        if (range <= 0)
        {
            return double.PositiveInfinity;
        }

        var maxExtraDepth = 0d;
        foreach (var ep in envelope)
        {
            if (ep.Station <= start.Station || ep.Station >= end.Station)
            {
                continue;
            }

            var t = (ep.Station - start.Station) / range;
            var elevation = start.Elevation + t * (end.Elevation - start.Elevation);
            var extraDepth = ep.NominalPipeTopElevation - elevation;
            if (extraDepth > maxExtraDepth)
            {
                maxExtraDepth = extraDepth;
            }
        }

        return maxExtraDepth;
    }

    private static bool IsLineInsideCorridor(
        ProfilePoint start,
        ProfilePoint end,
        IReadOnlyList<CoverEnvelopePoint> envelope)
    {
        var range = end.Station - start.Station;
        if (range <= 0)
        {
            return false;
        }

        foreach (var ep in envelope)
        {
            if (ep.Station <= start.Station || ep.Station >= end.Station)
            {
                continue;
            }

            var t = (ep.Station - start.Station) / range;
            var elevation = start.Elevation + t * (end.Elevation - start.Elevation);
            var maxAllowed = ep.MinimumCoverUpperElevation - LiftClearance + MinCoverViolationTolerance;

            if (elevation > maxAllowed)
            {
                return false;
            }

            if (elevation < ep.PreferredLowerElevation - MinCoverViolationTolerance)
            {
                return false;
            }
        }

        return true;
    }

    private const double CrestShortcutLimit = 0.45;

    private static List<ProfilePoint> RestoreCutCrests(
        List<ProfilePoint> points,
        IReadOnlyList<CoverEnvelopePoint> envelope)
    {
        var result = points.ToList();
        for (var pass = 0; pass < 8; pass++)
        {
            var inserted = false;
            for (var i = 0; i < result.Count - 1; i++)
            {
                var start = result[i];
                var end = result[i + 1];
                var range = end.Station - start.Station;
                if (range < 12)
                {
                    continue;
                }

                var worst = -1;
                var worstDepth = CrestShortcutLimit;
                var first = LowerBoundStation(envelope, start.Station);
                var last = LowerBoundStation(envelope, end.Station);
                for (var k = first; k < last; k++)
                {
                    var ep = envelope[k];
                    if (ep.Station <= start.Station + 4 || ep.Station >= end.Station - 4)
                    {
                        continue;
                    }

                    var t = (ep.Station - start.Station) / range;
                    var chord = start.Elevation + t * (end.Elevation - start.Elevation);
                    var extra = ep.NominalPipeTopElevation - chord;
                    if (extra > worstDepth)
                    {
                        worstDepth = extra;
                        worst = k;
                    }
                }

                if (worst < 0)
                {
                    continue;
                }

                var at = envelope[worst];
                var elevation = Math.Clamp(at.NominalPipeTopElevation, at.PreferredLowerElevation, at.MinimumCoverUpperElevation);
                result.Insert(i + 1, new ProfilePoint(at.Station, elevation));
                inserted = true;
                i++;
            }

            if (!inserted)
            {
                break;
            }
        }

        return result;
    }

    private static List<ProfilePoint> PruneDenseVertices(
        List<ProfilePoint> points,
        IReadOnlyList<CoverEnvelopePoint> envelope,
        DesignParameters parameters,
        double maximumCover)
    {
        var result = DedupStations(points);
        if (result.Count < 3 || envelope.Count < 2)
        {
            return result;
        }

        var allowedExtra = AllowedMergeExtra(parameters, maximumCover);
        var minLeg = Math.Max(0.5, parameters.MinimumSegmentLength);

        for (var pass = 0; pass < 24 && result.Count > 2; pass++)
        {
            var candidates = new List<(int Index, double Score)>();
            for (var i = 1; i < result.Count - 1; i++)
            {
                var prev = result[i - 1];
                var curr = result[i];
                var next = result[i + 1];
                var span = next.Station - prev.Station;
                if (span <= 0.05 || curr.Station - prev.Station <= 0.05)
                {
                    candidates.Add((i, -1_000_000));
                    continue;
                }

                var crest = curr.Elevation > prev.Elevation + 0.25 && curr.Elevation > next.Elevation + 0.25;
                var budget = crest ? Math.Min(allowedExtra, 0.40) : allowedExtra;
                if (!ChordFits(prev, next, envelope, budget))
                {
                    continue;
                }

                var slopeIn = (curr.Elevation - prev.Elevation) / Math.Max(curr.Station - prev.Station, 1e-6);
                var slopeOut = (next.Elevation - curr.Elevation) / Math.Max(next.Station - curr.Station, 1e-6);
                var shortBias = span < minLeg ? 0 : 1_000;
                candidates.Add((i, shortBias + Math.Abs(slopeOut - slopeIn)));
            }

            if (candidates.Count == 0)
            {
                break;
            }

            candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
            var remove = new bool[result.Count];
            var blocked = new bool[result.Count];
            var any = false;
            foreach (var candidate in candidates)
            {
                var index = candidate.Index;
                if (blocked[index] || blocked[index - 1] || blocked[index + 1])
                {
                    continue;
                }

                remove[index] = true;
                blocked[index] = true;
                blocked[index - 1] = true;
                blocked[index + 1] = true;
                any = true;
            }

            if (!any)
            {
                break;
            }

            var kept = new List<ProfilePoint>(result.Count);
            for (var i = 0; i < result.Count; i++)
            {
                if (!remove[i])
                {
                    kept.Add(result[i]);
                }
            }

            result = kept;
        }

        return ShiftVerticesAlongCorridor(result, envelope, allowedExtra);
    }

    private static List<ProfilePoint> DedupStations(List<ProfilePoint> points)
    {
        if (points.Count == 0)
        {
            return points;
        }

        var ordered = points.OrderBy(point => point.Station).ToList();
        var result = new List<ProfilePoint> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = result[^1];
            var current = ordered[i];
            if (current.Station - previous.Station <= 0.05)
            {
                if (current.Elevation < previous.Elevation)
                {
                    result[^1] = current;
                }

                continue;
            }

            result.Add(current);
        }

        return result;
    }

    private static double AllowedMergeExtra(DesignParameters parameters, double maximumCover)
    {
        var requested = parameters.MergeExtraBurial;
        var room = Math.Max(parameters.CoverTolerance, maximumCover - parameters.TargetCover);
        return Math.Min(Math.Max(0, requested), room);
    }

    private static bool ChordFits(
        ProfilePoint start,
        ProfilePoint end,
        IReadOnlyList<CoverEnvelopePoint> envelope,
        double allowedExtra)
    {
        var range = end.Station - start.Station;
        if (range <= 0.05)
        {
            return false;
        }

        var first = LowerBoundStation(envelope, start.Station);
        var last = LowerBoundStation(envelope, end.Station);
        var maxExtra = 0d;
        for (var i = first; i < last; i++)
        {
            var ep = envelope[i];
            if (ep.Station <= start.Station || ep.Station >= end.Station)
            {
                continue;
            }

            var t = (ep.Station - start.Station) / range;
            var elevation = start.Elevation + t * (end.Elevation - start.Elevation);
            var maxAllowed = ep.MinimumCoverUpperElevation - LiftClearance + MinCoverViolationTolerance;
            if (elevation > maxAllowed || elevation < ep.PreferredLowerElevation - MinCoverViolationTolerance)
            {
                return false;
            }

            var extra = ep.NominalPipeTopElevation - elevation;
            if (extra > maxExtra)
            {
                maxExtra = extra;
            }
        }

        return maxExtra <= allowedExtra + 1e-6;
    }

    private static List<ProfilePoint> ShiftVerticesAlongCorridor(
        List<ProfilePoint> points,
        IReadOnlyList<CoverEnvelopePoint> envelope,
        double allowedExtra)
    {
        if (points.Count < 3)
        {
            return points;
        }

        var result = points.ToList();
        for (var i = 1; i < result.Count - 1; i++)
        {
            var prev = result[i - 1];
            var curr = result[i];
            var next = result[i + 1];
            var spanIn = curr.Station - prev.Station;
            var spanOut = next.Station - curr.Station;
            if (spanIn <= 0.5 || spanOut <= 0.5)
            {
                continue;
            }

            var best = curr;
            var bestScore = Math.Abs(
                ((curr.Elevation - prev.Elevation) / spanIn) -
                ((next.Elevation - curr.Elevation) / spanOut));
            var low = prev.Station + 0.5;
            var high = next.Station - 0.5;
            if (high - low < 1)
            {
                continue;
            }

            for (var step = 1; step <= 6; step++)
            {
                var station = low + (high - low) * step / 7.0;
                var envelopePoint = EnvelopeAt(envelope, station);
                var straight = prev.Elevation + (next.Elevation - prev.Elevation) * ((station - prev.Station) / (next.Station - prev.Station));
                var samples = new[] { curr.Elevation, envelopePoint.NominalPipeTopElevation, straight };
                foreach (var elevation in samples)
                {
                    var trial = new ProfilePoint(station, elevation);
                    if (!ChordFits(prev, trial, envelope, allowedExtra) || !ChordFits(trial, next, envelope, allowedExtra))
                    {
                        continue;
                    }

                    var score = Math.Abs(
                        ((trial.Elevation - prev.Elevation) / (trial.Station - prev.Station)) -
                        ((next.Elevation - trial.Elevation) / (next.Station - trial.Station)));
                    if (score + 1e-4 < bestScore)
                    {
                        best = trial;
                        bestScore = score;
                    }
                }
            }

            result[i] = best;
        }

        return DedupStations(result);
    }

    private static CoverEnvelopePoint EnvelopeAt(IReadOnlyList<CoverEnvelopePoint> envelope, double station)
    {
        var index = LowerBoundStation(envelope, station);
        if (index <= 0)
        {
            return envelope[0];
        }

        if (index >= envelope.Count)
        {
            return envelope[^1];
        }

        var right = envelope[index];
        var left = envelope[index - 1];
        var range = right.Station - left.Station;
        if (range <= 1e-9)
        {
            return right;
        }

        var t = (station - left.Station) / range;
        return new CoverEnvelopePoint(
            station,
            left.TerrainElevation + (right.TerrainElevation - left.TerrainElevation) * t,
            left.StrippedElevation + (right.StrippedElevation - left.StrippedElevation) * t,
            left.NominalPipeTopElevation + (right.NominalPipeTopElevation - left.NominalPipeTopElevation) * t,
            left.PreferredLowerElevation + (right.PreferredLowerElevation - left.PreferredLowerElevation) * t,
            left.PreferredUpperElevation + (right.PreferredUpperElevation - left.PreferredUpperElevation) * t,
            left.MinimumCoverUpperElevation + (right.MinimumCoverUpperElevation - left.MinimumCoverUpperElevation) * t);
    }

    private static int LowerBoundStation(IReadOnlyList<CoverEnvelopePoint> envelope, double station)
    {
        var low = 0;
        var high = envelope.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (envelope[mid].Station < station)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static IEnumerable<VerticalAlignmentVertex> BuildVertices(
        IReadOnlyList<ProfilePoint> points,
        DesignParameters parameters,
        double minimumRadius)
    {
        if (points.Count == 0)
        {
            yield break;
        }

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var curveLength = 0d;
            var radius = 0d;

            if (i > 0 && i < points.Count - 1)
            {
                var previous = points[i - 1];
                var next = points[i + 1];
                var incomingLength = point.Station - previous.Station;
                var outgoingLength = next.Station - point.Station;
                var incomingSlope = (point.Elevation - previous.Elevation) / incomingLength;
                var outgoingSlope = (next.Elevation - point.Elevation) / outgoingLength;
                var slopeDelta = Math.Abs(outgoingSlope - incomingSlope);

                curveLength = Math.Min(
                    Math.Max(parameters.PipeLength, minimumRadius * slopeDelta),
                    Math.Min(incomingLength, outgoingLength) * 2 * MaxTangentShare);

                if (curveLength > 0 && slopeDelta > 0.000001)
                {
                    radius = curveLength / slopeDelta;
                }
            }

            yield return new VerticalAlignmentVertex(
                point.Station,
                point.Elevation,
                curveLength,
                curveLength > 0 ? 1 : 0,
                curveLength / 2,
                curveLength / 2,
                radius);
        }
    }

    private static List<ProfilePoint> LowerOnlyShallowSpans(
        List<ProfilePoint> points,
        HashSet<double> locked,
        IReadOnlyList<CoverIssue> issues,
        double minimumBurialDepth,
        IReadOnlyList<ProfilePoint>? nominalBridgeLiftProfile,
        double maximumDrop)
    {
        var lowered = points.Select(point => new ProfilePoint(point.Station, point.Elevation)).ToList();
        if (lowered.Count == 0 || issues.Count == 0)
        {
            return lowered;
        }

        var drop = new double[lowered.Count];
        foreach (var issue in issues)
        {
            var deficit = EffectiveMinBurialForStation(issue.Station, minimumBurialDepth, nominalBridgeLiftProfile)
                          - issue.Cover
                          + LiftClearance;
            deficit = Math.Min(Math.Max(0, deficit), Math.Max(0, maximumDrop));
            if (deficit <= 0)
            {
                continue;
            }

            var index = 0;
            while (index < lowered.Count - 1 && lowered[index + 1].Station < issue.Station)
            {
                index++;
            }

            drop[index] = Math.Max(drop[index], deficit);
            if (index + 1 < lowered.Count)
            {
                drop[index + 1] = Math.Max(drop[index + 1], deficit);
            }
        }

        for (var i = 0; i < lowered.Count; i++)
        {
            if (drop[i] > 0 && !locked.Contains(lowered[i].Station))
            {
                lowered[i] = new ProfilePoint(lowered[i].Station, lowered[i].Elevation - drop[i]);
            }
        }

        return lowered;
    }

    private static double BaseMinBurial(DesignParameters parameters)
    {
        if (parameters.MinimumCover + 1e-6 >= parameters.TargetCover)
        {
            return Math.Max(0.05, parameters.TargetCover - ShallowBurialMargin);
        }

        return Math.Max(0.05, parameters.MinimumCover);
    }

    private static double CrossingCoverAt(double station, DesignParameters parameters)
    {
        var crossings = parameters.Crossings;
        if (crossings is null || crossings.Count == 0)
        {
            return 0;
        }

        var cover = 0d;
        foreach (var crossing in crossings)
        {
            var start = Math.Min(crossing.StartStation, crossing.EndStation);
            var end = Math.Max(crossing.StartStation, crossing.EndStation);
            if (station + 1e-6 >= start && station - 1e-6 <= end)
            {
                cover = Math.Max(cover, crossing.Depth);
            }
        }

        return cover;
    }

    private static IEnumerable<CoverIssue> CheckBurialDepth(
        IReadOnlyList<ProfilePoint> designCurve,
        IReadOnlyList<CoverEnvelopePoint> envelope,
        DesignParameters parameters,
        double minimumBurialDepth,
        IReadOnlyList<ProfilePoint>? nominalBridgeLiftProfile = null)
    {
        foreach (var point in designCurve)
        {
            var surface = InterpolateStrippedElevation(envelope, point.Station);
            var burial = surface - point.Elevation;
            var need = EffectiveMinBurialForStation(point.Station, minimumBurialDepth, nominalBridgeLiftProfile);
            need = Math.Max(need, CrossingCoverAt(point.Station, parameters));
            if (burial < need)
            {
                yield return new CoverIssue(point.Station, burial, "Ust tampon dahilinde minimum derinlik saglanmiyor");
            }
        }
    }

    private static double InterpolateStrippedElevation(IReadOnlyList<CoverEnvelopePoint> envelope, double station)
    {
        if (station <= envelope[0].Station)
        {
            return envelope[0].StrippedElevation;
        }

        if (station >= envelope[^1].Station)
        {
            return envelope[^1].StrippedElevation;
        }

        for (var i = 1; i < envelope.Count; i++)
        {
            var left = envelope[i - 1];
            var right = envelope[i];
            if (station <= right.Station)
            {
                var ratio = (station - left.Station) / (right.Station - left.Station);
                return left.StrippedElevation + (right.StrippedElevation - left.StrippedElevation) * ratio;
            }
        }

        return envelope[^1].StrippedElevation;
    }
}
