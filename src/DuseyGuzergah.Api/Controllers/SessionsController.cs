using DuseyGuzergah.Api.Contracts;
using DuseyGuzergah.Api.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DuseyGuzergah.Api.Controllers;

[ApiController]
[Route("api/sessions")]
public sealed class SessionsController : ControllerBase
{
    private readonly IDesignSessionStore _store;
    private readonly DesignSessionOptions _options;

    public SessionsController(IDesignSessionStore store, IOptions<DesignSessionOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    [HttpPost]
    [RequestSizeLimit(52_428_800)]
    [RequestFormLimits(MultipartBodyLengthLimit = 52_428_800)]
    public async Task<ActionResult<SessionResponse>> Create(
        IFormFile? kse,
        IFormFile? ktb,
        CancellationToken cancellationToken)
    {
        if (kse is null || kse.Length == 0)
        {
            return BadRequest(new ApiMessage("KSE dosyası gerekli."));
        }

        if (!HasExtension(kse.FileName, ".kse"))
        {
            return BadRequest(new ApiMessage("Arazi dosyası .kse uzantılı olmalı."));
        }

        if (ktb is not null && ktb.Length > 0 && !HasExtension(ktb.FileName, ".ktb"))
        {
            return BadRequest(new ApiMessage("Güzergâh dosyası .ktb uzantılı olmalı."));
        }

        var maxBytes = Math.Max(1, _options.MaxUploadBytes);
        if (kse.Length > maxBytes || (ktb?.Length ?? 0) > maxBytes)
        {
            return BadRequest(new ApiMessage("Dosya boyut sınırı aşıldı."));
        }

        var id = Guid.NewGuid();
        var directory = _store.CreateSessionDirectory(id);
        var ksePath = Path.Combine(directory, "arazi.kse");
        string? ktbPath = null;

        try
        {
            await SaveAsync(kse, ksePath, cancellationToken);
            if (ktb is not null && ktb.Length > 0)
            {
                ktbPath = Path.Combine(directory, "sablon.ktb");
                await SaveAsync(ktb, ktbPath, cancellationToken);
            }

            var sections = KseReader.ReadSections(ksePath);
            var terrain = sections
                .Select(section => section.ToCenterProfilePoint())
                .OfType<ProfilePoint>()
                .OrderBy(point => point.Station)
                .ToList();

            if (terrain.Count == 0)
            {
                _store.Abandon(id);
                return BadRequest(new ApiMessage("KSE dosyasından arazi profili okunamadı."));
            }

            var issues = new List<FileValidationIssue>();
            FileLoadValidator.AppendTerrainIssues(terrain, issues);

            var vertices = new List<VerticalAlignmentVertex>();
            var curve = new List<ProfilePoint>();
            var guides = new List<VerticalCurveGuide>();
            if (ktbPath is not null)
            {
                FileLoadValidator.AppendKtbRawIssues(ktbPath, issues);
                vertices = KtbReader.ReadVerticalAlignmentVertices(ktbPath);
                FileLoadValidator.AppendVertexIssues(vertices, issues);
                curve = KtbReader.BuildVerticalCurve(vertices);
                guides = KtbReader.BuildVerticalCurveGuides(vertices);
            }

            var session = new DesignSession
            {
                Id = id,
                DirectoryPath = directory,
                KtbTemplatePath = ktbPath,
                Terrain = terrain,
                KtbVertices = vertices,
                KtbCurve = curve,
                KtbGuides = guides
            };
            _store.Add(session);

            var boundsPoints = terrain
                .Concat(curve)
                .Concat(vertices.Select(vertex => new ProfilePoint(vertex.Station, vertex.Elevation)));

            return Ok(new SessionResponse(
                id,
                sections.Count,
                terrain.Count,
                terrain,
                vertices,
                curve,
                guides,
                issues.Select(issue => new ValidationIssueDto(issue.Severity.ToString(), issue.Source, issue.Message)).ToList(),
                ProfileBoundsFrom(boundsPoints),
                ktbPath is not null));
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException)
        {
            _store.Abandon(id);
            return BadRequest(new ApiMessage(ex.Message));
        }
        catch
        {
            _store.Abandon(id);
            throw;
        }
    }

    [HttpPost("{id:guid}/calculate")]
    public ActionResult<DesignProfileResult> Calculate(Guid id, DesignParameters parameters)
    {
        var session = _store.TryGet(id);
        if (session is null)
        {
            return NotFound(new ApiMessage("Oturum bulunamadı veya süresi doldu."));
        }

        var error = ValidateParameters(parameters);
        if (error is not null)
        {
            return BadRequest(new ApiMessage(error));
        }

        var result = VerticalDesignCalculator.Calculate(session.Terrain, parameters);
        lock (session.Gate)
        {
            session.CalculatedVertices = result.Vertices.ToList();
        }

        return Ok(result);
    }

    [HttpPost("{id:guid}/export")]
    public ActionResult Export(Guid id)
    {
        var session = _store.TryGet(id);
        if (session is null)
        {
            return NotFound(new ApiMessage("Oturum bulunamadı veya süresi doldu."));
        }

        if (string.IsNullOrWhiteSpace(session.KtbTemplatePath) || !System.IO.File.Exists(session.KtbTemplatePath))
        {
            return BadRequest(new ApiMessage("KTB yazmak için şablon KTB yüklenmeli."));
        }

        List<VerticalAlignmentVertex> vertices;
        lock (session.Gate)
        {
            vertices = session.CalculatedVertices?.ToList() ?? [];
        }

        if (vertices.Count == 0)
        {
            return BadRequest(new ApiMessage("Önce düşey hesap yapılmalı."));
        }

        var outputPath = Path.Combine(session.DirectoryPath, "GÜZERGAH_dusey.KTB");
        KtbWriter.WriteWithVerticalAlignment(session.KtbTemplatePath, outputPath, vertices);
        var bytes = System.IO.File.ReadAllBytes(outputPath);
        return File(bytes, "application/octet-stream", "GÜZERGAH_dusey.KTB");
    }

    [HttpPost("{id:guid}/export-editor")]
    public ActionResult ExportEditor(Guid id, List<VerticalAlignmentVertex> vertices)
    {
        var session = _store.TryGet(id);
        if (session is null)
        {
            return NotFound(new ApiMessage("Oturum bulunamadı veya süresi doldu."));
        }

        if (string.IsNullOrWhiteSpace(session.KtbTemplatePath) || !System.IO.File.Exists(session.KtbTemplatePath))
        {
            return BadRequest(new ApiMessage("KTB yazmak için şablon KTB yüklenmeli."));
        }

        if (vertices.Count == 0)
        {
            return BadRequest(new ApiMessage("KTB yazmak için tabloda some satırı olmalı."));
        }

        var outputPath = Path.Combine(session.DirectoryPath, "GÜZERGAH_dusey_duzen.KTB");
        KtbWriter.WriteWithVerticalAlignment(session.KtbTemplatePath, outputPath, vertices);
        var bytes = System.IO.File.ReadAllBytes(outputPath);
        return File(bytes, "application/octet-stream", "GÜZERGAH_dusey_duzen.KTB");
    }

    [HttpPost("{id:guid}/warnings")]
    public ActionResult<List<string>> Warnings(Guid id, WarningRequest request)
    {
        var session = _store.TryGet(id);
        if (session is null)
        {
            return NotFound(new ApiMessage("Oturum bulunamadı veya süresi doldu."));
        }

        var error = ValidateParameters(request.Parameters);
        if (error is not null)
        {
            return BadRequest(new ApiMessage(error));
        }

        var ordered = request.Vertices.OrderBy(vertex => vertex.Station).ToList();
        var curve = KtbReader.BuildVerticalCurve(ordered);
        var messages = DesignWarningsEvaluator.Evaluate(
            request.Thresholds,
            request.Parameters,
            session.Terrain,
            ordered,
            curve);
        return Ok(messages);
    }

    private static async Task SaveAsync(IFormFile file, string path, CancellationToken cancellationToken)
    {
        await using var stream = System.IO.File.Create(path);
        await file.CopyToAsync(stream, cancellationToken);
    }

    private static bool HasExtension(string fileName, string extension)
    {
        return string.Equals(Path.GetExtension(fileName), extension, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ValidateParameters(DesignParameters parameters)
    {
        if (parameters.TargetCover < 0 ||
            parameters.CoverTolerance < 0 ||
            parameters.MinimumCover < 0 ||
            parameters.MaximumCover < 0 ||
            parameters.StrippingDepth < 0 ||
            parameters.PipeDiameter < 0 ||
            parameters.PipeLength < 0 ||
            parameters.MinimumSegmentLength < 0 ||
            parameters.MinimumRadius < 0 ||
            parameters.MergeExtraBurial < 0 ||
            parameters.MaxDeflectionDeg < 0 ||
            parameters.CrossingBufferM < 0 ||
            parameters.CrossingRampMinM < 0)
        {
            return "Hesap parametreleri negatif olamaz.";
        }

        if (parameters.PipeDiameter > 2)
        {
            return "Boru çapı metre cinsinden olmalı (en fazla 2 m).";
        }

        if (parameters.MaxDeflectionDeg >= 89)
        {
            return "Sapma açısı 89°'den küçük olmalı.";
        }

        if (parameters.MaximumCover + 1e-9 < parameters.TargetCover)
        {
            return "Maksimum örtü, hedef örtüden küçük olamaz.";
        }

        if (parameters.MinimumCover > parameters.TargetCover + 1e-9)
        {
            return "Minimum örtü, hedef örtüden büyük olamaz.";
        }

        if (parameters.Crossings is not null)
        {
            foreach (var crossing in parameters.Crossings)
            {
                if (crossing.Depth < 0 || crossing.StartStation < 0 || crossing.EndStation < 0)
                {
                    return "Özel geçiş kilometresi ve derinliği negatif olamaz.";
                }
            }
        }

        return null;
    }

    private static ProfileBounds ProfileBoundsFrom(IEnumerable<ProfilePoint> points)
    {
        var list = points.ToList();
        if (list.Count == 0)
        {
            return new ProfileBounds(0, 1, 0, 1);
        }

        return new ProfileBounds(
            list.Min(point => point.Station),
            list.Max(point => point.Station),
            list.Min(point => point.Elevation),
            list.Max(point => point.Elevation));
    }
}
