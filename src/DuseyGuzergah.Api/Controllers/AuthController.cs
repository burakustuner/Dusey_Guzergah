using DuseyGuzergah.Api.Auth;
using DuseyGuzergah.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DuseyGuzergah.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    [HttpGet("config")]
    public IActionResult Config([FromServices] IOptions<GoogleAuthOptions> options)
        => Ok(new { clientId = options.Value.ClientId });

    [HttpGet("me")]
    public IActionResult Me()
    {
        if (HttpContext.Items[GoogleIdTokenMiddleware.UserItemKey] is not GoogleUser user)
        {
            return Unauthorized(new ApiMessage("Giriş gerekli."));
        }

        return Ok(user);
    }
}
