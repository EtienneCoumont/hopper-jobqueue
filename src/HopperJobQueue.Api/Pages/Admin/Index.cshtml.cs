using HopperJobQueue.Api.Jobs;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HopperJobQueue.Api.Pages.Admin;

public sealed class IndexModel(JobStore jobStore) : PageModel
{
    public QueueStats Stats { get; private set; } = null!;

    public string? OldestPending { get; private set; }

    public async Task OnGetAsync()
    {
        Stats = await jobStore.GetStatsAsync(HttpContext.RequestAborted);
        if (Stats.OldestPendingAgeSeconds is { } age)
        {
            var span = TimeSpan.FromSeconds(age);
            OldestPending = span.TotalHours >= 1
                ? $"{(int)span.TotalHours} h {span.Minutes:D2} min"
                : span.TotalMinutes >= 1
                    ? $"{(int)span.TotalMinutes} min {span.Seconds:D2} s"
                    : $"{span.Seconds} s";
        }
    }
}
