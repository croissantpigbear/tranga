namespace API.Security;

/// <summary>
/// When the login is on, every API request needs a session cookie, "Authorization: Bearer &lt;API key&gt;" or an
/// "X-Api-Key" header. Open: /v2/Auth/* (login itself), Swagger's documentation and CORS preflight requests.
/// </summary>
public static class AuthMiddleware
{
    private static readonly string[] OpenPrefixes = ["/v2/auth", "/swagger"];

    public static IApplicationBuilder UseOptionalLogin(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        string path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        if (!AuthStore.Enabled
            || HttpMethods.IsOptions(context.Request.Method)
            || OpenPrefixes.Any(p => path == p || path.StartsWith(p + "/"))
            || AuthStore.IsAuthenticated(context))
        {
            await next();
            return;
        }
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("Log in to Tranga first (or send an API key).");
    });
}
