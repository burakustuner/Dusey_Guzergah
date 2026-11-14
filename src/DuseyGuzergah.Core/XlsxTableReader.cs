using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace DuseyGuzergah;

/// <summary>.xlsx dosyasının ilk çalışma sayfasını hücre metinleri olarak okur (biçim ve formül yok, değerler).</summary>
public static class XlsxTableReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace DocumentRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<List<string>> ReadFirstSheet(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var sheet = Load(archive, FirstSheetPath(archive))
                    ?? throw new InvalidDataException("Excel dosyasında çalışma sayfası bulunamadı.");
        var sharedStrings = ReadSharedStrings(archive);

        var rows = new SortedDictionary<int, SortedDictionary<int, string>>();
        var nextRow = 0;
        foreach (var row in sheet.Descendants(Main + "row"))
        {
            var rowIndex = int.TryParse((string?)row.Attribute("r"), out var number) ? number - 1 : nextRow;
            nextRow = rowIndex + 1;

            var cells = new SortedDictionary<int, string>();
            var nextColumn = 0;
            foreach (var cell in row.Elements(Main + "c"))
            {
                var column = ColumnIndex((string?)cell.Attribute("r")) ?? nextColumn;
                nextColumn = column + 1;
                var text = CellText(cell, sharedStrings).Trim();
                if (text.Length > 0)
                {
                    cells[column] = text;
                }
            }

            if (cells.Count > 0)
            {
                rows[rowIndex] = cells;
            }
        }

        return rows.Values
            .Select(cells =>
            {
                var values = Enumerable.Repeat(string.Empty, cells.Keys.Max() + 1).ToList();
                foreach (var (column, text) in cells)
                {
                    values[column] = text;
                }

                return values;
            })
            .ToList();
    }

    private static string FirstSheetPath(ZipArchive archive)
    {
        const string fallback = "xl/worksheets/sheet1.xml";
        var relationId = (string?)Load(archive, "xl/workbook.xml")?
            .Descendants(Main + "sheet")
            .FirstOrDefault()?
            .Attribute(DocumentRelationships + "id");
        if (relationId is null)
        {
            return fallback;
        }

        var target = (string?)Load(archive, "xl/_rels/workbook.xml.rels")?
            .Descendants(PackageRelationships + "Relationship")
            .FirstOrDefault(relation => (string?)relation.Attribute("Id") == relationId)?
            .Attribute("Target");
        if (string.IsNullOrWhiteSpace(target))
        {
            return fallback;
        }

        return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        return Load(archive, "xl/sharedStrings.xml")?
                   .Root?
                   .Elements(Main + "si")
                   .Select(item => string.Concat(item.Descendants(Main + "t").Select(text => text.Value)))
                   .ToList()
               ?? [];
    }

    private static string CellText(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(Main + "t").Select(text => text.Value));
        }

        var value = (string?)cell.Element(Main + "v") ?? string.Empty;
        if (type == "s")
        {
            return int.TryParse(value, out var index) && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index]
                : string.Empty;
        }

        return value;
    }

    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var index = 0;
        var letters = 0;
        foreach (var character in reference)
        {
            if (character is < 'A' or > 'Z')
            {
                break;
            }

            index = index * 26 + (character - 'A' + 1);
            letters++;
        }

        return letters == 0 ? null : index - 1;
    }

    private static XDocument? Load(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
}
