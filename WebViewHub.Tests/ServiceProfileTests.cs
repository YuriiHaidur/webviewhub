using WebViewHub.Models;

namespace WebViewHub.Tests;

/// <summary>
/// Guards the multi-profile migration. The failure mode being tested for is
/// severe and silent: if a migrated service stops resolving to the WebView2
/// profile bucket it used before, every existing login for that service is
/// stranded in a folder nothing points at anymore.
/// </summary>
public class ServiceProfileTests
{
    /// <summary>A service as deserialized from a pre-profiles config file.</summary>
    private static ServiceConfig LegacyConfig() => new()
    {
        Name = "Slack",
        Url = "https://app.slack.com",
        Id = "bd6ea48fd5334281a37824c9f0584e01",
    };

    [Fact]
    public void Legacy_service_keeps_its_existing_session_bucket()
    {
        var svc = LegacyConfig();

        svc.EnsureProfiles();

        // This is the whole point of the migration: the key handed to
        // WebView2 must still be the raw service Id.
        Assert.Equal(svc.Id, svc.EffectiveProfileKey);
    }

    [Fact]
    public void Migration_creates_exactly_one_active_default_profile()
    {
        var svc = LegacyConfig();

        svc.EnsureProfiles();

        var only = Assert.Single(svc.Profiles);
        Assert.Equal("Default", only.Name);
        Assert.Equal(only.Id, svc.ActiveProfileId);
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        var svc = LegacyConfig();

        svc.EnsureProfiles();
        var keyAfterFirst = svc.EffectiveProfileKey;
        var idAfterFirst = svc.ActiveProfileId;

        // Every app launch re-runs this over an already-migrated config.
        svc.EnsureProfiles();
        svc.EnsureProfiles();

        Assert.Single(svc.Profiles);
        Assert.Equal(keyAfterFirst, svc.EffectiveProfileKey);
        Assert.Equal(idAfterFirst, svc.ActiveProfileId);
    }

    [Fact]
    public void Added_profile_gets_its_own_bucket_and_never_steals_the_legacy_one()
    {
        var svc = LegacyConfig();
        svc.EnsureProfiles();
        var legacyKey = svc.EffectiveProfileKey;

        var second = new ServiceProfile { Name = "Work", ProfileKey = svc.NewProfileKey() };
        svc.Profiles.Add(second);

        Assert.NotEqual(legacyKey, second.ProfileKey);
        Assert.StartsWith(svc.Id, second.ProfileKey);
        // WebView2 caps ProfileName at 64 characters.
        Assert.True(second.ProfileKey.Length <= 64, $"key too long: {second.ProfileKey.Length}");
    }

    [Fact]
    public void Switching_active_profile_changes_the_bucket()
    {
        var svc = LegacyConfig();
        svc.EnsureProfiles();
        var legacyKey = svc.EffectiveProfileKey;

        var second = new ServiceProfile { Name = "Work", ProfileKey = svc.NewProfileKey() };
        svc.Profiles.Add(second);
        svc.ActiveProfileId = second.Id;

        Assert.Equal(second.ProfileKey, svc.EffectiveProfileKey);
        Assert.NotEqual(legacyKey, svc.EffectiveProfileKey);
    }

    [Fact]
    public void Dangling_active_id_falls_back_instead_of_throwing()
    {
        var svc = LegacyConfig();
        svc.EnsureProfiles();
        var goodKey = svc.EffectiveProfileKey;

        // Simulates a profile deleted while it was the active one.
        svc.ActiveProfileId = "no-such-profile";

        Assert.Equal(goodKey, svc.EffectiveProfileKey);
        Assert.Equal(svc.Profiles[0].Id, svc.ActiveProfileId);
    }

    [Fact]
    public void Profile_with_blank_key_is_repaired_without_colliding()
    {
        var svc = LegacyConfig();
        svc.EnsureProfiles();
        svc.Profiles.Add(new ServiceProfile { Name = "Hand-edited", ProfileKey = "" });

        svc.EnsureProfiles();

        Assert.All(svc.Profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.ProfileKey)));
        Assert.Equal(2, svc.Profiles.Select(p => p.ProfileKey).Distinct().Count());
    }

    [Fact]
    public void Two_services_never_share_a_bucket()
    {
        var a = new ServiceConfig { Name = "A", Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
        var b = new ServiceConfig { Name = "B", Id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
        a.EnsureProfiles();
        b.EnsureProfiles();

        Assert.NotEqual(a.EffectiveProfileKey, b.EffectiveProfileKey);
        Assert.NotEqual(a.NewProfileKey(), b.NewProfileKey());
    }

    [Fact]
    public void Fresh_service_resolves_a_bucket_without_explicit_migration()
    {
        // Services created at runtime never pass through ConfigManager.LoadAsync,
        // so the property getters have to be self-healing.
        var svc = new ServiceConfig { Name = "New", Url = "https://example.com" };

        Assert.False(string.IsNullOrWhiteSpace(svc.EffectiveProfileKey));
        Assert.Equal(svc.Id, svc.EffectiveProfileKey);
    }
}
