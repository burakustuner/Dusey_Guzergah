using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using DuseyGuzergah;

namespace DuseyGuzergah.Core.Tests;

public sealed class CrossingImportTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CrossingImportTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Reads_first_sheet_with_shared_inline_and_numeric_cells()
    {
        using var stream = BuildWorkbook();
        var rows = XlsxTableReader.ReadFirstSheet(stream);

        Assert.Equal(3, rows.Count);
        Assert.Equal(["KM", "CİNSİ", "DERİNLİK"], rows[0]);
        Assert.Equal(["1250.5", "Karayolu", "2.5"], rows[1]);
        Assert.Equal(["1290", "", "3"], rows[2]);
    }

    [Fact]
    public async Task Api_returns_rows_and_rejects_old_xls()
    {
        using var client = _factory.CreateClient();

        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(BuildWorkbook()), "file", "gecisler.xlsx");
        var response = await client.PostAsync("/api/crossings/read", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<List<string>>>();
        Assert.NotNull(rows);
        Assert.Equal("Karayolu", rows[1][1]);

        using var old = new MultipartFormDataContent();
        old.Add(new ByteArrayContent([1, 2, 3]), "file", "gecisler.xls");
        var rejected = await client.PostAsync("/api/crossings/read", old);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    private static MemoryStream BuildWorkbook()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Gecisler" sheetId="1" r:id="rId7"/></sheets>
                </workbook>
                """);
            Add(archive, "xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId7" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/liste.xml"/>
                </Relationships>
                """);
            Add(archive, "xl/sharedStrings.xml", """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <si><t>KM</t></si><si><t>CİNSİ</t></si><si><r><t>DERİN</t></r><r><t>LİK</t></r></si><si><t>Karayolu</t></si>
                </sst>
                """);
            Add(archive, "xl/worksheets/liste.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row>
                    <row r="2"><c r="A2"><v>1250.5</v></c><c r="B2" t="s"><v>3</v></c><c r="C2"><v>2.5</v></c></row>
                    <row r="4"><c r="A4" t="inlineStr"><is><t>1290</t></is></c><c r="C4"><v>3</v></c></row>
                  </sheetData>
                </worksheet>
                """);
        }

        stream.Position = 0;
        return stream;
    }

    private static void Add(ZipArchive archive, string path, string xml)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(xml);
    }
}
