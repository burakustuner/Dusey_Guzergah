using System.Globalization;
using System.IO;

namespace DuseyGuzergah;

public static class KseReader
{
    public static List<CrossSection> ReadSections(string path)
    {
        var sections = new List<CrossSection>();
        CrossSection? currentSection = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("2 ", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            if (parts[0] == "1" && parts.Length >= 4 && TryParseDouble(parts[2], out var station))
            {
                var index = TryParseInt(parts[1], out var parsedIndex) ? parsedIndex : sections.Count + 1;
                currentSection = new CrossSection(index, station, parts[3]);
                sections.Add(currentSection);
                continue;
            }

            if (currentSection is null || parts[0] != "0")
            {
                continue;
            }

            if (!TryParseDouble(parts[1], out var offset) || !TryParseDouble(parts[2], out var elevation))
            {
                continue;
            }

            currentSection.Points.Add(new CrossSectionPoint(offset, elevation));

            if (parts.Any(part => part.Equals("EKS", StringComparison.OrdinalIgnoreCase)))
            {
                currentSection.CenterElevation = elevation;
            }
        }

        return sections;
    }

    public static List<ProfilePoint> ReadCenterProfile(string path)
    {
        return ReadSections(path)
            .Select(section => section.ToCenterProfilePoint())
            .OfType<ProfilePoint>()
            .OrderBy(point => point.Station)
            .ToList();
    }

    private static bool TryParseDouble(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseInt(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}
