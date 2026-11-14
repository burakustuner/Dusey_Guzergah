using System.Xml;
using DuseyGuzergah.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace DuseyGuzergah.Api.Controllers;

[ApiController]
[Route("api/crossings")]
public sealed class CrossingsController : ControllerBase
{
    private const long MaxBytes = 5_242_880;

    [HttpPost("read")]
    [RequestSizeLimit(MaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBytes)]
    public ActionResult<List<List<string>>> Read(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ApiMessage("Excel dosyası gerekli."));
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension == ".xls")
        {
            return BadRequest(new ApiMessage("Eski .xls biçimi okunamıyor. Excel’de «Farklı Kaydet → Excel Çalışma Kitabı (.xlsx)» ile kaydedin."));
        }

        if (extension != ".xlsx")
        {
            return BadRequest(new ApiMessage("Excel listesi .xlsx uzantılı olmalı."));
        }

        try
        {
            using var stream = file.OpenReadStream();
            return Ok(XlsxTableReader.ReadFirstSheet(stream));
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            return BadRequest(new ApiMessage("Excel dosyası okunamadı: " + ex.Message));
        }
    }
}
