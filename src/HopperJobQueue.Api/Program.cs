using System.Security.Claims;
using System.Threading.RateLimiting;
using Dapper;
using HopperJobQueue.Api.Admin;
using HopperJobQueue.Api.Auth;
using HopperJobQueue.Api.Domain;
using HopperJobQueue.Api.Infrastructure;
using HopperJobQueue.Api.Jobs;
using HopperJobQueue.Api.Maintenance;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(ParseLogLevel(Environment.GetEnvironmentVariable("HOPPER_LOG_LEVEL")))
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Configuration.AddEnvironmentVariables("HOPPER_");
    builder.Host.UseSerilog();

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.AddServerHeader = false;
        // Low global limit (64 KiB); /complete is raised to 512 KiB by middleware.
        kestrel.Limits.MaxRequestBodySize = 64 * 1024;
    });
    builder.Host.ConfigureHostOptions(host => host.ShutdownTimeout = TimeSpan.FromSeconds(20));

    var config = AppConfig.Load(builder.Configuration);
    DapperConfig.Configure();

    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(config.ConnectionString));
    builder.Services.AddSingleton<ApiKeyStore>();
    builder.Services.AddSingleton<JobStore>();
    builder.Services.AddSingleton<KeyUsageTracker>();
    builder.Services.AddSingleton<SweeperService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SweeperService>());

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Indispensable behind Traefik in Docker: the proxy's IP changes on every
        // recreation, the default allowlist would silently reject the headers.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    // Two tiers: sliding window per API key for authenticated requests (worker
    // polling is legitimate), per-IP window for everything else.
    builder.Services.AddRateLimiter(limiter =>
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var key = context.GetApiKey();
            if (key is not null)
            {
                return RateLimitPartition.GetSlidingWindowLimiter($"key:{key.Id}", _ =>
                    new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 300,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueLimit = 0,
                    });
            }

            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetSlidingWindowLimiter($"ip:{ip}", _ =>
                new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                });
        });
    });

    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "hopper_admin";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/admin";
            options.LoginPath = "/admin/login";
            options.AccessDeniedPath = "/admin/login";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents
            {
                // A revoked key invalidates the session on the next round-trip.
                OnValidatePrincipal = async context =>
                {
                    var idClaim = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                    if (!long.TryParse(idClaim, out var keyId))
                    {
                        context.RejectPrincipal();
                        return;
                    }

                    var store = context.HttpContext.RequestServices.GetRequiredService<ApiKeyStore>();
                    var key = await store.GetAsync(keyId, context.HttpContext.RequestAborted);
                    if (key is null || key.RevokedAt is not null || key.Scope != ApiScope.Admin)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                },
            };
        });
    builder.Services.AddAuthorization();
    builder.Services.AddAntiforgery(options =>
    {
        options.Cookie.HttpOnly = true;
        // SameAsRequest, not Always: behind Traefik the request is seen as HTTPS
        // (X-Forwarded-Proto), so the cookie is Secure in production — but Always
        // would make CheckSSLConfig throw in dev, where the port is served over
        // plain HTTP.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/admin";
    });
    builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AuthorizeFolder("/Admin");
        options.Conventions.AllowAnonymousToPage("/Admin/Login");
    });

    var app = builder.Build();

    // Migrations before accepting traffic; failure = non-zero exit.
    DatabaseMigrator.Run(config.ConnectionString, app.Logger);
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var keyStore = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();
        await keyStore.EnsureBootstrapKeyAsync(config, app.Logger);
    }

    app.UseForwardedHeaders();

    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        // Neutral problem+json: never a stack trace or internal detail in a response.
        await Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
            .ExecuteAsync(context);
    }));

    app.UseSerilogRequestLogging();

    // /complete accepts a result up to 256 KiB: body limit raised to 512 KiB, every
    // other route stays at the global 64 KiB Kestrel limit.
    app.Use(async (context, next) =>
    {
        if (context.Request.Method == HttpMethods.Post
            && context.Request.Path.StartsWithSegments("/api/v1/jobs")
            && context.Request.Path.Value!.EndsWith("/complete", StringComparison.Ordinal))
        {
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
            {
                feature.MaxRequestBodySize = 512 * 1024;
            }
        }

        await next(context);
    });

    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/admin"))
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; "
                + "base-uri 'none'; frame-ancestors 'none'";
        }

        await next(context);
    });

    app.UseApiKeyAuthentication();
    app.UseRateLimiter();
    app.UseScopeEnforcement();
    app.UseStaticFiles();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapGet("/healthz", () => Results.Text("ok"));
    app.MapGet("/readyz", async (NpgsqlDataSource dataSource) =>
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var conn = await dataSource.OpenConnectionAsync(timeout.Token);
            await conn.ExecuteScalarAsync<int>("select 1");
            return Results.Text("ok");
        }
        catch
        {
            // No error detail: the route is public.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    });

    app.MapJobEndpoints();
    app.MapAdminEndpoints();
    app.MapRazorPages();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
    Environment.ExitCode = 1;
    throw;
}
finally
{
    Log.CloseAndFlush();
}

static LogEventLevel ParseLogLevel(string? raw) =>
    Enum.TryParse<LogEventLevel>(raw, ignoreCase: true, out var level) ? level : LogEventLevel.Information;

// Exposes the entry point to the integration tests (WebApplicationFactory).
public partial class Program;
