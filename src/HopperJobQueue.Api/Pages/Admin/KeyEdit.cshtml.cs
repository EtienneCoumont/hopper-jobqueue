using HopperJobQueue.Api.Auth;
using HopperJobQueue.Api.Domain;
using HopperJobQueue.Api.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HopperJobQueue.Api.Pages.Admin;

public sealed class KeyEditModel(ApiKeyStore apiKeyStore, JobStore jobStore) : PageModel
{
    public ApiKeyRecord Key { get; private set; } = null!;

    public IReadOnlyList<JobKind> Kinds { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var key = await apiKeyStore.GetAsync(id, HttpContext.RequestAborted);
        if (key is null || key.RevokedAt is not null)
        {
            return Redirect("/admin/keys");
        }

        Key = key;
        Kinds = await jobStore.ListKindsAsync(HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        long id, [FromForm] string? name, [FromForm] string[]? allowedKinds)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var kinds = await jobStore.ListKindsAsync(HttpContext.RequestAborted);
            var validKinds = (allowedKinds ?? [])
                .Where(k => kinds.Any(existing => existing.Name == k))
                .Distinct()
                .ToArray();

            await apiKeyStore.UpdateAsync(id, name.Trim(), validKinds, HttpContext.RequestAborted);
        }

        return Redirect("/admin/keys");
    }
}
