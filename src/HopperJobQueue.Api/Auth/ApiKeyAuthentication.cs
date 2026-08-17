using HopperJobQueue.Api.Domain;

namespace HopperJobQueue.Api.Auth;

/// <summary>Endpoint metadata: allowed scopes (admin passes everywhere).</summary>
public sealed record RequiredScopes(string[] Scopes);

public static class ApiKeyAuthentication
{
    private const string ItemKey = "HopperApiKey";

    public static ApiKeyRecord? GetApiKey(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) ? value as ApiKeyRecord : null;

    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, params string[] scopes)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredScopes(scopes));

    /// <summary>
    /// Resolves the API key from the <c>Authorization: Bearer hjq_…</c> header and stores
    /// it in <see cref="HttpContext.Items"/>. Placed before the rate limiter so that it
    /// partitions by key (authenticated) or by IP (unauthenticated).
    /// </summary>
    public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = header["Bearer ".Length..].Trim();
                if (token.Length > 0)
                {
                    var store = context.RequestServices.GetRequiredService<ApiKeyStore>();
                    var key = await store.AuthenticateAsync(token, context.RequestAborted);
                    if (key is not null)
                    {
                        context.Items[ItemKey] = key;
                        context.RequestServices.GetRequiredService<KeyUsageTracker>().Touch(key.Id);
                    }
                    else
                    {
                        // Information, not Warning: the background noise of scanners on a
                        // public IP would saturate alerts. Never the full key in the logs.
                        var logger = context.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("HopperJobQueue.Auth");
                        logger.LogInformation(
                            "Authentication failed from {RemoteIp} with key prefix {Prefix}",
                            context.Connection.RemoteIpAddress,
                            token.StartsWith("hjq_", StringComparison.Ordinal)
                                ? ApiKeys.Prefix(token)
                                : $"(not hjq_, length {token.Length})");
                    }
                }
            }

            await next(context);
        });

    /// <summary>
    /// Enforces the resolved endpoint's <see cref="RequiredScopes"/>: 401 without a valid
    /// key, 403 if the scope does not match. The <c>admin</c> scope passes everywhere
    /// ("everything").
    /// </summary>
    public static IApplicationBuilder UseScopeEnforcement(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var required = context.GetEndpoint()?.Metadata.GetMetadata<RequiredScopes>();
            if (required is null)
            {
                await next(context);
                return;
            }

            var key = context.GetApiKey();
            if (key is null)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await Results.Problem(
                        statusCode: StatusCodes.Status401Unauthorized,
                        title: "Unauthorized",
                        detail: "A valid API key is required: Authorization: Bearer hjq_…")
                    .ExecuteAsync(context);
                return;
            }

            if (key.Scope != ApiScope.Admin && !required.Scopes.Contains(key.Scope))
            {
                await Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Forbidden",
                        detail: $"This endpoint requires scope '{string.Join("' or '", required.Scopes)}'.")
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });
}
