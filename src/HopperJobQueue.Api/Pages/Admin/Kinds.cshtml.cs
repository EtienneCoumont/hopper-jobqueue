using HopperJobQueue.Api.Domain;
using HopperJobQueue.Api.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HopperJobQueue.Api.Pages.Admin;

public sealed class KindsModel(JobStore jobStore) : PageModel
{
    public IReadOnlyList<JobKind> Kinds { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Kinds = await jobStore.ListKindsAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostCreateAsync(
        [FromForm] string? name,
        [FromForm] string? description,
        [FromForm] int defaultTtlSeconds = 86400,
        [FromForm] int defaultMaxAttempts = 3,
        [FromForm] int defaultLeaseSeconds = 1200,
        [FromForm] int retentionDays = 90)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            await jobStore.CreateKindAsync(
                new JobKind
                {
                    Name = name.Trim(),
                    Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    Enabled = true,
                    DefaultTtlSeconds = Math.Clamp(defaultTtlSeconds, 1, 604800),
                    DefaultMaxAttempts = Math.Clamp(defaultMaxAttempts, 1, 10),
                    DefaultLeaseSeconds = Math.Clamp(defaultLeaseSeconds, 1, 86400),
                    RetentionDays = Math.Clamp(retentionDays, 1, 3650),
                },
                HttpContext.RequestAborted);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(string name)
    {
        var kind = (await jobStore.ListKindsAsync(HttpContext.RequestAborted))
            .FirstOrDefault(k => k.Name == name);
        if (kind is not null)
        {
            await jobStore.SetKindEnabledAsync(name, !kind.Enabled, HttpContext.RequestAborted);
        }

        return RedirectToPage();
    }
}
