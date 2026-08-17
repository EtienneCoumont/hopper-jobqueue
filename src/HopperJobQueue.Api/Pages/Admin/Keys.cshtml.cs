using HopperJobQueue.Api.Auth;
using HopperJobQueue.Api.Domain;
using HopperJobQueue.Api.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HopperJobQueue.Api.Pages.Admin;

public sealed class KeysModel(ApiKeyStore apiKeyStore, JobStore jobStore) : PageModel
{
    public IReadOnlyList<ApiKeyRecord> Keys { get; private set; } = [];

    public IReadOnlyList<JobKind> Kinds { get; private set; } = [];

    [TempData]
    public string? CreatedKey { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync(
        [FromForm] string? name, [FromForm] string? scope, [FromForm] string[]? allowedKinds)
    {
        if (string.IsNullOrWhiteSpace(name) || scope is not (ApiScope.Producer or ApiScope.Worker or ApiScope.Admin))
        {
            return Redirect("/admin/keys");
        }

        var kinds = await jobStore.ListKindsAsync(HttpContext.RequestAborted);
        var validKinds = (allowedKinds ?? [])
            .Where(k => kinds.Any(existing => existing.Name == k))
            .Distinct()
            .ToArray();

        var (_, plaintext) = await apiKeyStore.CreateAsync(
            name.Trim(), scope, validKinds, HttpContext.RequestAborted);

        // La clé en clair transite une seule fois, via TempData, puis disparaît.
        CreatedKey = plaintext;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(long id)
    {
        await apiKeyStore.RevokeAsync(id, HttpContext.RequestAborted);
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Keys = await apiKeyStore.ListAsync(HttpContext.RequestAborted);
        Kinds = await jobStore.ListKindsAsync(HttpContext.RequestAborted);
    }
}
