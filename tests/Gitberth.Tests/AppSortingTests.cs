using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Components.Shared;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>The Apps list sorts by any column of its header: app, status, address, source, CPU and memory.</summary>
public sealed class AppSortingTests
{
    private static AppSummary App(string name, AppState state = AppState.Running, AppSource source = AppSource.Existing, int? hostPort = null,
        double? cpu = null, ulong? memory = null) =>
        new(name, null, name, name, source, ContainerRole.Other, ImageOrigin.Local, "img-" + name, null, state, state.ToString(), "id-" + name,
            hostPort is int p ? [new AddressChip($"{p}:80", "", AddressChipKind.Open, $"http://10.1.1.80:{p}/", p, 80, "tcp")] : [],
            memory, cpu, null, false, false, null);

    private static IEnumerable<string> Names(IEnumerable<AppSummary> apps) => apps.Select(a => a.Title);

    [Fact]
    public void Names_sort_with_their_numbers_read_as_numbers_either_way()
    {
        var apps = new[] { App("sample-go-10"), App("Postgres"), App("sample-go-2"), App("orb") };

        Assert.Equal(["orb", "Postgres", "sample-go-2", "sample-go-10"], Names(AppSorting.Sort(apps, SortBy.Name, descending: false)));
        Assert.Equal(["sample-go-10", "sample-go-2", "Postgres", "orb"], Names(AppSorting.Sort(apps, SortBy.Name, descending: true)));
    }

    [Fact]
    public void Status_puts_trouble_first_then_what_runs_then_whats_stopped()
    {
        var apps = new[] { App("a-stopped", AppState.Stopped), App("b-running"), App("c-crashed", AppState.Crashed), App("d-paused", AppState.Paused) };

        Assert.Equal(["c-crashed", "b-running", "d-paused", "a-stopped"], Names(AppSorting.Sort(apps, SortBy.Status, descending: false)));
    }

    [Fact]
    public void Addresses_sort_by_port_as_a_number_and_apps_with_none_go_last_whichever_way()
    {
        var apps = new[] { App("none"), App("ten-thousand", hostPort: 10000), App("ninety-one", hostPort: 9101), App("three", hostPort: 3000) };

        Assert.Equal(["three", "ninety-one", "ten-thousand", "none"], Names(AppSorting.Sort(apps, SortBy.Address, descending: false)));
        Assert.Equal(["ten-thousand", "ninety-one", "three", "none"], Names(AppSorting.Sort(apps, SortBy.Address, descending: true)));
    }

    [Fact]
    public void Cpu_and_memory_start_with_the_most_and_stopped_apps_go_last()
    {
        var apps = new[]
        {
            App("idle", cpu: 0.1, memory: 20 << 20), App("busy", cpu: 85, memory: 300 << 20), App("stopped", AppState.Stopped, cpu: 0, memory: 0),
            App("middle", cpu: 12.5, memory: 1UL << 30),
        };

        Assert.True(AppSorting.StartsDescending(SortBy.Cpu));
        Assert.True(AppSorting.StartsDescending(SortBy.Memory));
        Assert.False(AppSorting.StartsDescending(SortBy.Name));
        Assert.Equal(["busy", "middle", "idle", "stopped"], Names(AppSorting.Sort(apps, SortBy.Cpu, descending: true)));
        Assert.Equal(["middle", "busy", "idle", "stopped"], Names(AppSorting.Sort(apps, SortBy.Memory, descending: true)));
        Assert.Equal(["idle", "busy", "middle", "stopped"], Names(AppSorting.Sort(apps, SortBy.Memory, descending: false)));
    }

    [Fact]
    public void Source_sorts_by_its_word_and_ties_go_by_name()
    {
        var apps = new[] { App("z-git", source: AppSource.Git), App("b-found"), App("a-found"), App("m-registry", source: AppSource.Registry) };

        var sorted = Names(AppSorting.Sort(apps, SortBy.Source, descending: false)).ToList();
        Assert.Equal(["a-found", "b-found"], sorted.Where(n => n.EndsWith("found", StringComparison.Ordinal)));
        Assert.Equal(sorted.OrderBy(n => SourceBadge.WordOf(apps.Single(a => a.Title == n).Source), StringComparer.OrdinalIgnoreCase), sorted);
    }

    [Fact]
    public void Groups_keep_the_sorted_order_inside_them()
    {
        var apps = AppSorting.Sort(
            [App("sample-node-a", cpu: 1), App("sample-node-b", cpu: 9), App("sample-go-c", cpu: 5), App("orb", cpu: 2), App("kanban", cpu: 7)],
            SortBy.Cpu, descending: true);

        var groups = AppGrouping.Group(apps, GroupBy.Prefix);

        Assert.Equal(["kanban", "orb"], Names(groups.Single(g => g.Id == "none").Apps));
        Assert.Equal(["sample-node-b", "sample-go-c", "sample-node-a"], Names(groups.Single(g => g.Id == "prefix:sample").Apps));
    }
}
