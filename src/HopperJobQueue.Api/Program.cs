using System.Security.Claims;
using System.Threading.RateLimiting;
using Dapper;
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
        // Limite globale basse (64 Ko) ; /complete est relevé à 512 Ko par middleware.
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
        // Indispensable derrière Traefik en Docker : l'IP du proxy change à chaque
        // recréation, la liste blanche par défaut rejetterait les en-têtes en silence.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    // Deux étages : fenêtre glissante par clé API pour les requêtes authentifiées
    // (le polling des workers est légitime), fenêtre par IP pour tout le reste.
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
                // Une clé révoquée invalide la session au prochain aller-retour.
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
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/admin";
    });
    builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AuthorizeFolder("/Admin");
        options.Conventions.AllowAnonymousToPage("/Admin/Login");
    });

    var app = builder.Build();

    // Migrations avant d'accepter du trafic ; échec = sortie en code non nul.
    DatabaseMigrator.Run(config.ConnectionString, app.Logger);
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var keyStore = scope.ServiceProvider.GetRequiredService<ApiKeyStore>();
        await keyStore.EnsureBootstrapKeyAsync(config, app.Logger);
    }

    app.UseForwardedHeaders();

    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        // problem+json neutre : jamais de trace d'appels ni de détail interne en réponse.
        await Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
            .ExecuteAsync(context);
    }));

    app.UseSerilogRequestLogging();

    // /complete accepte un result jusqu'à 256 Ko : limite de corps relevée à 512 Ko,
    // toutes les autres routes restent à la limite Kestrel globale de 64 Ko.
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
            // Sans détail d'erreur : la route est publique.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    });

    app.MapJobEndpoints();
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

// Expose le point d'entrée aux tests d'intégration (WebApplicationFactory).
public partial class Program;
