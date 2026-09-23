namespace DuseyGuzergah;

public static class ProfileInterpolation
{
    public static double InterpolateStationElevation(IReadOnlyList<ProfilePoint> profile, double station)
    {
        if (profile.Count == 0)
        {
            return 0;
        }

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
                var ratio = (station - left.Station) / (right.Station - left.Station);
                return left.Elevation + (right.Elevation - left.Elevation) * ratio;
            }
        }

        return profile[^1].Elevation;
    }
}
