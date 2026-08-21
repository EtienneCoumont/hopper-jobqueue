using HopperJobQueue.Api.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HopperJobQueue.Api.Pages.Admin;

public sealed class JobsModel(JobStore jobStore) : PageModel
{
    private const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Project { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Kind { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    // "page" is a reserved Razor Pages route value (it holds the page path), and route
    // values shadow the query string during binding — hence "p".
    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public JobPage Result { get; private set; } = null!;

    public string CurrentQuery => QueryForPage(PageNumber);

    public string QueryForPage(int page)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Status))
        {
            parts.Add($"status={Uri.EscapeDataString(Status)}");
        }

        if (!string.IsNullOrWhiteSpace(Project))
        {
            parts.Add($"project={Uri.EscapeDataString(Project)}");
        }

        if (!string.IsNullOrWhiteSpace(Kind))
        {
            parts.Add($"kind={Uri.EscapeDataString(Kind)}");
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            parts.Add($"q={Uri.EscapeDataString(Q)}");
        }

        if (page > 1)
        {
            parts.Add($"p={page}");
        }

        return string.Join("&", parts);
    }

    public async Task OnGetAsync()
    {
        if (PageNumber < 1)
        {
            PageNumber = 1;
        }

        Result = await jobStore.ListAsync(
            Status, Project, Kind, Q, PageNumber, PageSize, HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostRequeueAsync(long id, [FromForm] string? returnQuery)
    {
        await jobStore.RequeueAsync(id, Actor(), HttpContext.RequestAborted);
        return RedirectBack(returnQuery);
    }

    public async Task<IActionResult> OnPostCancelAsync(long id, [FromForm] string? returnQuery)
    {
        await jobStore.CancelAsync(id, Actor(), HttpContext.RequestAborted);
        return RedirectBack(returnQuery);
    }

    private string Actor() => $"admin:{User.Identity?.Name ?? "?"}";

    private IActionResult RedirectBack(string? returnQuery) =>
        Redirect(string.IsNullOrEmpty(returnQuery) ? "/admin/jobs" : $"/admin/jobs?{returnQuery}");
}
