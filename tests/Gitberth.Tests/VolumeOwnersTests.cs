using Gitberth.Data;
using Gitberth.Deploy;

namespace Gitberth.Tests;

/// <summary>
/// Which app has each named volume, when one app's name spells another's: orb's test-data and its preview orb-test's
/// data are both gb-orb-test-data. Saves, a deploy's declared VOLUMEs and the deploy itself never let a preview share one.
/// </summary>
public sealed class VolumeOwnersTests
{
    private static readonly AppDefinition Orb = new() { Id = 1, Name = "orb", Slug = "orb" };
    // orb's preview of the branch test.
    private static readonly AppDefinition Test = new() { Id = 2, Name = "orb · test", Slug = "orb-test", ParentAppId = 1 };
    // An app of its own, not a preview, whose name orb's spells too.
    private static readonly AppDefinition Kiosk = new() { Id = 3, Name = "kiosk", Slug = "orb-kiosk" };

    private static VolumeOwners Owners(params (int AppId, string Source)[] volumes) =>
        new([new(1, "orb", "orb", null), new(2, "orb · test", "orb-test", 1), new(3, "kiosk", "orb-kiosk", null)], volumes);

    private static VolumeMount[] Volumes(params string[] sources) => [.. sources.Select(s => new VolumeMount { Source = s, Target = s.StartsWith('/') ? "/host" + s : "/" + s })];

    [Fact]
    public void A_volume_added_to_production_whose_name_its_preview_has_is_refused()
    {
        // orb has data; orb-test has its copy, gb-orb-test-data.
        var owners = Owners((1, "data"), (2, "data"));

        Assert.Equal(["The volume test-data would be gb-orb-test-data, which is also the preview orb · test's. Give it another name, or remove that preview first."],
            owners.Problems(Orb, Volumes("data", "test-data")));
        // What it has already, a free name and a host folder save as usual.
        Assert.Empty(owners.Problems(Orb, Volumes("data", "logs", "/srv/orb")));
    }

    [Fact]
    public void A_volume_whose_copy_in_a_preview_would_be_productions_is_refused()
    {
        // orb has test-cache, gb-orb-test-cache: orb-test's copy of a new cache would be called that too.
        var owners = Owners((1, "test-cache"), (2, "test-cache"));

        Assert.Equal(["The volume cache would be gb-orb-test-cache in the preview orb · test, which is also orb's. Give it another name, or remove that preview first."],
            owners.Problems(Orb, Volumes("test-cache", "cache")));
    }

    [Fact]
    public void Two_new_volumes_that_would_meet_in_a_preview_are_refused_and_so_is_another_apps_name()
    {
        // Neither is there yet: orb's test-data and orb-test's copy of data would both be gb-orb-test-data.
        Assert.Equal(
        [
            "The volume data would be gb-orb-test-data in the preview orb · test, which is also orb's. Give it another name, or remove that preview first.",
            "The volume test-data would be gb-orb-test-data, which is also the preview orb · test's. Give it another name, or remove that preview first.",
        ], Owners().Problems(Orb, Volumes("data", "test-data")));

        // An app that isn't a preview: a new name it has is refused too.
        Assert.Equal(["The volume kiosk-data would be gb-orb-kiosk-data, which is also kiosk's. Give it another name."],
            Owners((3, "data")).Problems(Orb, Volumes("kiosk-data")));
    }

    [Fact]
    public void A_deploy_finds_a_preview_on_another_apps_volume_but_leaves_two_other_apps_as_they_were()
    {
        // orb-test's copy of cache is orb's test-cache. orb's kiosk-data and kiosk's data are one volume too, from before.
        var owners = Owners((1, "test-cache"), (1, "cache"), (1, "kiosk-data"), (2, "test-cache"), (2, "cache"), (3, "data"));

        Assert.Equal(new VolumeOwners.Clash("cache", "gb-orb-test-cache", null, "orb", false), owners.SharedWithPreview(Test, Volumes("test-cache", "cache")));
        Assert.Equal(new VolumeOwners.Clash("test-cache", "gb-orb-test-cache", null, "orb · test", true), owners.SharedWithPreview(Orb, Volumes("test-cache", "cache")));
        // No preview in that one, so it stays as it was.
        Assert.Null(owners.SharedWithPreview(Orb, Volumes("kiosk-data")));
        Assert.Null(owners.SharedWithPreview(Kiosk, Volumes("data")));
        Assert.Equal("orb", owners.OtherThan(2, "gb-orb-test-cache"));
        Assert.Equal("orb", owners.OtherThan(3, "gb-orb-kiosk-data"));
        Assert.Null(owners.OtherThan(1, "gb-orb-cache"));
    }

    [Fact]
    public void A_volume_an_image_declares_gets_a_number_when_its_name_would_be_another_apps()
    {
        // The preview's branch declares VOLUME /data: orb-test's data would be gb-orb-test-data, orb's /srv/test.
        var owners = Owners((1, "test-data"), (2, "test-data"));
        Assert.Equal([("data-2", "/data")], Missing(owners, Test, Volumes("test-data"), "/data"));

        // Production declares VOLUME /cache: its own gb-orb-cache is free, but orb-test's copy would be orb's test-cache.
        owners = Owners((1, "test-cache"), (2, "test-cache"));
        Assert.Equal([("cache-2", "/cache")], Missing(owners, Orb, Volumes("test-cache"), "/cache"));

        // Two at once: data first, then test-data would meet orb-test's copy of it.
        Assert.Equal([("data", "/data"), ("test-data-2", "/test-data")], Missing(Owners(), Orb, [], "/test-data", "/data"));
    }

    private static List<(string Source, string Target)> Missing(VolumeOwners owners, AppDefinition app, VolumeMount[] mounts, params string[] declared) =>
        DeclaredVolumes.Missing(mounts, declared, (source, sources) => owners.ClashOf(app, source, sources) is not null).Select(v => (v.Source, v.Target)).ToList();
}
