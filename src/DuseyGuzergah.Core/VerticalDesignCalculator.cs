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

        var coarseDpBase = Math.Max(parameters.CoverTolerance * 4, 0.20);
        var dpTolerance = Math.Max(coarseDpBase * 1.28, 0.38);
        var majorBreakpoints = SimplifyTerrainProfile(orderedTerrain, dpTolerance);
        var maximumCover = Math.Max(parameters.MaximumCover, parameters.TargetCover);

        var chordBurialSlack = ComputeChordBurialSlack(parameters.TargetCover, maximumCover);

        var minAcceptableBurialDepth = BaseMinBurial(parameters);

        var simplifiedPoints = SelectPrimaryIntersections(
            result.CoverEnvelope,
            majorBreakpoints,
            parameters.TargetCover,
            maximumCover,
            chordBurialSlack);
        simplifiedPoints = LiftToShallowestAllowed(simplifiedPoints, result.CoverEnvelope);
        simplifiedPoints = CollapseRedundantVertices(
            simplifiedPoints,
            result.CoverEnvelope,
            parameters.MinimumSegmentLength,
            parameters.TargetCover,
            maximumCover,
            parameters.CoverTolerance);
        simplifiedPoints = RefinePolylineAgainstNominalBurial(
            simplifiedPoints,
            result.CoverEnvelope,
            parameters.TargetCover,
            Math.Max(0.10, chordBurialSlack - 0.08),
            parameters.MinimumSegmentLength);
        simplifiedPoints = RefinePolylineAgainstNominalBurial(
            simplifiedPoints,
            result.CoverEnvelope,
            parameters.TargetCover,
            0.08,
            parameters.MinimumSegmentLength);
        simplifiedPoints = LiftToShallowestAllowed(simplifiedPoints, result.CoverEnvelope);

        simplifiedPoints = SnapPolylineUsingVerticalCurveBurial(
            simplifiedPoints,
            result.CoverEnvelope,
            parameters,
            burialSlackBeyondTarget: 0.10);

        result.Vertices.AddRange(BuildVertices(simplifiedPoints, parameters));
        result.Curve.AddRange(KtbReader.BuildVerticalCurve(result.Vertices));
        result.Guides.AddRange(KtbReader.BuildVerticalCurveGuides(result.Vertices));
        ClampDesignCurveToNominalBaseline(result);

        var initialIssues = CheckBurialDepth(
                result.Curve,
                result.CoverEnvelope,
                parameters,
                minAcceptableBurialDepth,
                nominalBridgeLiftProfile)
            .ToList();
        if (initialIssues.Count > 0)
        {
            var requestedShift = initialIssues.Max(
                                     issue => EffectiveMinBurialForStation(
                                                  issue.Station,
                                                  minAcceptableBurialDepth,
                                                  nominalBridgeLiftProfile)
                                              - issue.Cover)
                                 + LiftClearance;
            var downwardShift = Math.Min(Math.Max(0, requestedShift), parameters.TargetCover);
            if (downwardShift > 0)
            {
                result.Vertices.Clear();
                result.Curve.Clear();
                result.Guides.Clear();

                var loweredPoints = simplifiedPoints
                    .Select(point => new ProfilePoint(point.Station, point.Elevation - downwardShift))
                    .ToList();
                result.Vertices.AddRange(BuildVertices(loweredPoints, parameters));
                result.Curve.AddRange(KtbReader.BuildVerticalCurve(result.Vertices));
                result.Guides.AddRange(KtbReader.BuildVerticalCurveGuides(result.Vertices));
                ClampDesignCurveToNominalBaseline(result);
            }
        }

        result.CoverIssues.AddRange(
            CheckBurialDepth(
                result.Curve,
                result.CoverEnvelope,
                parameters,
                minAcceptableBurialDepth,
                nominalBridgeLiftProfile));

        return result;
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

    private static void ClampDesignCurveToNominalBaseline(DesignProfileResult result)
    {
        var curve = result.Curve;
        var envelope = result.CoverEnvelope;
        if (curve.Count == 0 || envelope.Count == 0)
        {
            return;
        }

        const double nominalUndershootAllowance = 0.055;
        for (var i = 0; i < curve.Count; i++)
        {
            var pt = curve[i];
            var nominal = InterpolateEnvelopeValue(envelope, pt.Station, e => e.NominalPipeTopElevation);
            var deepFloor = InterpolateEnvelopeValue(envelope, pt.Station, e => e.PreferredLowerElevation);
            var shallowCeiling =
                InterpolateEnvelopeValue(envelope, pt.Station, e => e.PreferredUpperElevation) - LiftClearance;
            var zAdjusted = Math.Clamp(
                Math.Max(pt.Elevation, nominal - nominalUndershootAllowance),
                deepFloor,
                shallowCeiling);
            curve[i] = new ProfilePoint(pt.Station, zAdjusted);
        }

        curve.Sort(static (a, b) => a.Station.CompareTo(b.Station));
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
            var vertices = BuildVertices(work, parameters).ToList();
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

    private static IEnumerable<VerticalAlignmentVertex> BuildVertices(
        IReadOnlyList<ProfilePoint> points,
        DesignParameters parameters)
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
                    Math.Max(parameters.PipeLength, parameters.MinimumRadius * slopeDelta),
                    Math.Min(incomingLength, outgoingLength) * 0.8);

                if (curveLength < parameters.PipeLength && Math.Min(incomingLength, outgoingLength) >= parameters.PipeLength)
                {
                    curveLength = parameters.PipeLength;
                }

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
