using System.Net;

namespace DokiDex.Web;

// Defense for the unauthenticated loopback host. Binding 127.0.0.1 does NOT stop a malicious page the
// user visits from scripting requests at the local server, so we add two cheap guards:
//   (1) Host-header allowlist  -> defeats DNS-rebinding (the attacker's hostname won't be in the set).
//   (2) Origin check on state-changing verbs -> defeats cross-site POSTs (CSRF).
// Single-user app, so there is no auth beyond this.
public sealed class LocalSecurityMiddleware
{
    private static readonly HashSet<string> AllowedHosts =
        new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "::1", "[::1]" };

    private readonly RequestDelegate _next;
    public LocalSecurityMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!AllowedHosts.Contains(ctx.Request.Host.Host))
        {
            await Deny(ctx, "forbidden host");
            return;
        }

        var method = ctx.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin)
                && (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !AllowedHosts.Contains(o.Host)))
            {
                await Deny(ctx, "forbidden origin");
                return;
            }
        }

        await _next(ctx);
    }

    private static async Task Deny(HttpContext ctx, string why)
    {
        ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
        await ctx.Response.WriteAsync(why);
    }
}
