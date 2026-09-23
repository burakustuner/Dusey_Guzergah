using DuseyGuzergah.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace DuseyGuzergah.Api.Controllers;

[ApiController]
[Route("api/alignment")]
public sealed class AlignmentController : ControllerBase
{
    [HttpPost("curve")]
    public ActionResult<CurvePreviewResponse> Curve(List<VerticalAlignmentVertex> vertices)
    {
        var ordered = vertices.OrderBy(vertex => vertex.Station).ToList();
        return Ok(new CurvePreviewResponse(
            KtbReader.BuildVerticalCurve(ordered),
            KtbReader.BuildVerticalCurveGuides(ordered)));
    }
}
