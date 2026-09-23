using System.Globalization;
using System.IO;
using System.Text;

namespace DuseyGuzergah;

public static class KtbWriter
{
    public static void WriteWithVerticalAlignment(
        string templatePath,
        string outputPath,
        IReadOnlyList<VerticalAlignmentVertex> vertices)
    {
        var lines = File.ReadAllLines(templatePath, Encoding.Default).ToList();
        var verticalStart = FindLine(lines, "$DUSEY");

        if (verticalStart >= 0)
        {
            var verticalEnd = FindSectionEnd(lines, verticalStart + 1);
            lines.RemoveRange(verticalStart, verticalEnd - verticalStart + 1);
        }

        var insertIndex = FindLine(lines, "$PROFILPARAMS");
        if (insertIndex >= 0)
        {
            insertIndex = FindSectionEnd(lines, insertIndex + 1) + 1;
        }
        else
        {
            insertIndex = lines.Count;
        }

        var verticalLines = new List<string> { "$DUSEY" };
        verticalLines.AddRange(vertices.Select(FormatVertex));
        verticalLines.Add("$SON");
        lines.InsertRange(insertIndex, verticalLines);

        File.WriteAllLines(outputPath, lines, Encoding.Default);
    }

    private static string FormatVertex(VerticalAlignmentVertex vertex)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0,11:0.00000} {1,10:0.00000} {2,10:0.00000} {3,4} {4,10:0.00000} {5,10:0.00000} {6,10:0.00000}",
            vertex.Station,
            vertex.Elevation,
            vertex.CurveLength,
            vertex.CurveType,
            vertex.TangentIn,
            vertex.TangentOut,
            vertex.Radius);
    }

    private static int FindLine(IReadOnlyList<string> lines, string marker)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals(marker, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindSectionEnd(IReadOnlyList<string> lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals("$SON", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return lines.Count - 1;
    }
}
