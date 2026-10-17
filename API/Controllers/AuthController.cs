using API.Security;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// Optional login (off by default). When it's on, the rest of the API needs a session (from Login) or the API key.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class AuthController : ControllerBase
{
    /// <summary>Login state</summary>
    /// <param name="Enabled">The login is switched on</param>
    /// <param name="LoggedIn">This browser/app may use the API (always true while the login is off)</param>
    /// <param name="Username">User name (only when logged in)</param>
    /// <param name="ApiKeySet">An API key exists</param>
    /// <param name="ApiKeyCreatedUtc">When it was made</param>
    /// <param name="ForcedOff">TRANGA_DISABLE_LOGIN=true is set, so the login is off whatever the setting says</param>
    public record AuthStatusDto(bool Enabled, bool LoggedIn, string? Username, bool ApiKeySet, DateTime? ApiKeyCreatedUtc, bool ForcedOff);

    /// <summary>Log in</summary>
    /// <param name="Username">User name</param>
    /// <param name="Password">Password</param>
    /// <param name="Remember">Stay logged in for 30 days (otherwise 12 hours of inactivity)</param>
    public record LoginRequest(string Username, string Password, bool Remember = true);

    /// <summary>Switch the login on, or change the user name and password</summary>
    /// <param name="Username">User name</param>
    /// <param name="Password">New password (8+ characters)</param>
    /// <param name="CurrentPassword">Needed when the login is already on</param>
    public record SetLoginRequest(string Username, string Password, string? CurrentPassword);

    /// <summary>A new API key (shown only once)</summary>
    public record ApiKeyDto(string ApiKey);

    private AuthStatusDto Status()
    {
        bool loggedIn = AuthStore.IsAuthenticated(HttpContext);
        return new AuthStatusDto(AuthStore.Enabled, loggedIn, AuthStore.Enabled && loggedIn ? AuthStore.Username : null,
            AuthStore.HasApiKey, AuthStore.ApiKeyCreatedUtc, AuthStore.ForcedOff);
    }

    private string Address => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private void SetCookie(string token, DateTime expiresUtc, bool remember)
    {
        bool https = Request.IsHttps || Request.Headers["X-Forwarded-Proto"].ToString() == "https";
        Response.Cookies.Append(AuthStore.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = https,
            Path = "/",
            Expires = remember ? expiresUtc : null
        });
    }

    /// <summary>
    /// Whether the login is on and this browser is logged in
    /// </summary>
    /// <response code="200"></response>
    [HttpGet("Status")]
    [ProducesResponseType<AuthStatusDto>(Status200OK, "application/json")]
    public Ok<AuthStatusDto> GetStatus() => TypedResults.Ok(Status());

    /// <summary>
    /// Logs in (sets the session cookie)
    /// </summary>
    /// <response code="200"></response>
    /// <response code="401">Wrong user name or password</response>
    /// <response code="429">Too many wrong attempts; wait the given number of seconds</response>
    [HttpPost("Login")]
    [ProducesResponseType<AuthStatusDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status401Unauthorized, "text/plain")]
    [ProducesResponseType<string>(Status429TooManyRequests, "text/plain")]
    public Results<Ok<AuthStatusDto>, UnauthorizedHttpResult, ContentHttpResult> Login([FromBody] LoginRequest request)
    {
        if (!AuthStore.Enabled)
            return TypedResults.Ok(Status());
        if (AuthStore.RetryAfterSeconds(Address) is > 0 and var wait)
        {
            Response.Headers.RetryAfter = wait.ToString();
            return TypedResults.Text($"Too many wrong passwords. Try again in {wait} seconds.", "text/plain", null, Status429TooManyRequests);
        }
        if (!AuthStore.CheckPassword(request.Username ?? "", request.Password ?? ""))
        {
            AuthStore.RecordFailure(Address);
            return TypedResults.Unauthorized();
        }
        AuthStore.RecordSuccess(Address);
        (string token, DateTime expires) = AuthStore.StartSession(request.Remember);
        SetCookie(token, expires, request.Remember);
        return TypedResults.Ok(new AuthStatusDto(true, true, AuthStore.Username, AuthStore.HasApiKey, AuthStore.ApiKeyCreatedUtc, false));
    }

    /// <summary>
    /// Logs out this browser
    /// </summary>
    /// <response code="200"></response>
    [HttpPost("Logout")]
    [ProducesResponseType<AuthStatusDto>(Status200OK, "application/json")]
    public Ok<AuthStatusDto> Logout()
    {
        if (Request.Cookies.TryGetValue(AuthStore.CookieName, out string? token))
            AuthStore.EndSession(token);
        Response.Cookies.Delete(AuthStore.CookieName, new CookieOptions { Path = "/" });
        return TypedResults.Ok(new AuthStatusDto(AuthStore.Enabled, !AuthStore.Enabled, null, AuthStore.HasApiKey,
            AuthStore.ApiKeyCreatedUtc, AuthStore.ForcedOff));
    }

    /// <summary>
    /// Switches the login on, or changes the user name and password (ends all other sessions; this browser stays logged in)
    /// </summary>
    /// <response code="200"></response>
    /// <response code="400">User name or password not acceptable</response>
    /// <response code="401">Not logged in, or the current password is wrong</response>
    [HttpPost("Credentials")]
    [ProducesResponseType<AuthStatusDto>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status400BadRequest, "text/plain")]
    [ProducesResponseType(Status401Unauthorized)]
    public Results<Ok<AuthStatusDto>, BadRequest<string>, UnauthorizedHttpResult> SetCredentials([FromBody] SetLoginRequest request)
    {
        if (AuthStore.Enabled && (!AuthStore.IsAuthenticated(HttpContext)
                                  || !AuthStore.CheckPassword(AuthStore.Username, request.CurrentPassword ?? "")))
            return TypedResults.Unauthorized();
        if (AuthStore.PasswordProblem(request.Username, request.Password) is { } problem)
            return TypedResults.BadRequest(problem);
        AuthStore.SetCredentials(request.Username, request.Password);
        (string token, DateTime expires) = AuthStore.StartSession(true);
        SetCookie(token, expires, true);
        return TypedResults.Ok(new AuthStatusDto(true, true, AuthStore.Username, AuthStore.HasApiKey, AuthStore.ApiKeyCreatedUtc, AuthStore.ForcedOff));
    }

    /// <summary>
    /// Switches the login off (needs to be logged in)
    /// </summary>
    /// <response code="200"></response>
    /// <response code="401">Not logged in</response>
    [HttpPost("Disable")]
    [ProducesResponseType<AuthStatusDto>(Status200OK, "application/json")]
    [ProducesResponseType(Status401Unauthorized)]
    public Results<Ok<AuthStatusDto>, UnauthorizedHttpResult> Disable()
    {
        if (!AuthStore.IsAuthenticated(HttpContext))
            return TypedResults.Unauthorized();
        AuthStore.Disable();
        Response.Cookies.Delete(AuthStore.CookieName, new CookieOptions { Path = "/" });
        return TypedResults.Ok(Status());
    }

    /// <summary>
    /// Creates an API key for other apps (replaces the old one). Send it as "Authorization: Bearer &lt;key&gt;" or "X-Api-Key".
    /// </summary>
    /// <response code="200">The key; it can't be shown again</response>
    /// <response code="401">Not logged in</response>
    [HttpPost("ApiKey")]
    [ProducesResponseType<ApiKeyDto>(Status200OK, "application/json")]
    [ProducesResponseType(Status401Unauthorized)]
    public Results<Ok<ApiKeyDto>, UnauthorizedHttpResult> CreateApiKey() =>
        AuthStore.IsAuthenticated(HttpContext) ? TypedResults.Ok(new ApiKeyDto(AuthStore.CreateApiKey())) : TypedResults.Unauthorized();

    /// <summary>
    /// Deletes the API key
    /// </summary>
    /// <response code="200"></response>
    /// <response code="401">Not logged in</response>
    [HttpDelete("ApiKey")]
    [ProducesResponseType<AuthStatusDto>(Status200OK, "application/json")]
    [ProducesResponseType(Status401Unauthorized)]
    public Results<Ok<AuthStatusDto>, UnauthorizedHttpResult> DeleteApiKey()
    {
        if (!AuthStore.IsAuthenticated(HttpContext))
            return TypedResults.Unauthorized();
        AuthStore.DeleteApiKey();
        return TypedResults.Ok(Status());
    }
}
