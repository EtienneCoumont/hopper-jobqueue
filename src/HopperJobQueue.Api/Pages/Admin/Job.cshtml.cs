using System.Text.Json;
using HopperJobQueue.Api.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HopperJobQueue.Api.Pages.Admin;

public sealed class JobModel(JobStore jobStore) : PageModel
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public Domain.Job Job { get; private set; } = null!;

    public IReadOnlyList<Domain.JobEvent> Events { get; private set; } = [];

    public string PayloadPretty { get; private set; } = "";

    public string? ResultPretty { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var job = await jobStore.GetAsync(id, HttpContext.RequestAborted);
        if (job is null)
        {
            return NotFound();
        }

        Job = job;
        Events = await jobStore.GetEventsAsync(id, HttpContext.RequestAborted);
        PayloadPretty = Pretty(job.Payload);
        ResultPretty = job.Result is null ? null : Pretty(job.Result);
        return Page();
    }

    public async Task<IActionResult> OnPostRequeueAsync(long id)
    {
        await jobStore.RequeueAsync(id, Actor(), HttpContext.RequestAborted);
        return Redirect($"/admin/jobs/{id}");
    }

    public async Task<IActionResult> OnPostCancelAsync(long id)
    {
        await jobStore.CancelAsync(id, Actor(), HttpContext.RequestAborted);
        return Redirect($"/admin/jobs/{id}");
    }

    private string Actor() => $"admin:{User.Identity?.Name ?? "?"}";

    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, PrettyJson);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
