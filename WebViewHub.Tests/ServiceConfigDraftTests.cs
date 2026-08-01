using WebViewHub.Models;

namespace WebViewHub.Tests;

public class ServiceConfigDraftTests
{
    private static ServiceConfig SampleConfig() => new()
    {
        Name = "Slack",
        Url = "https://app.slack.com",
        UserAgent = UserAgentMode.Desktop,
        ShowInTaskbar = true,
        WindowWidth = 1200,
        WindowHeight = 800,
        ZoomFactor = 1.0,
        IsTranslator = false,
        UseDoubleCtrlC = false,
        Hotkey = "Ctrl+Alt+S",
        ProtocolScheme = "slack",
        RegisterProtocol = true,
    };

    [Fact]
    public void Draft_starts_clean()
    {
        var draft = new ServiceConfigDraft(SampleConfig());
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void Draft_becomes_dirty_after_property_change()
    {
        var draft = new ServiceConfigDraft(SampleConfig());
        draft.Name = "Slack Workspace";
        Assert.True(draft.IsDirty);
    }

    [Fact]
    public void Draft_does_not_become_dirty_when_set_to_same_value()
    {
        var draft = new ServiceConfigDraft(SampleConfig());
        draft.Name = "Slack";
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void Discard_reverts_to_original_values()
    {
        var draft = new ServiceConfigDraft(SampleConfig());
        draft.Name = "X";
        draft.WindowWidth = 999;
        Assert.True(draft.IsDirty);

        draft.Discard();
        Assert.False(draft.IsDirty);
        Assert.Equal("Slack", draft.Name);
        Assert.Equal(1200, draft.WindowWidth);
    }

    [Fact]
    public void Snapshot_returns_deep_copy_of_current()
    {
        var draft = new ServiceConfigDraft(SampleConfig());
        draft.Name = "Renamed";

        var snap = draft.Snapshot();
        Assert.Equal("Renamed", snap.Name);

        // Mutating the snapshot must not bleed back into the draft.
        snap.Name = "Mutated";
        Assert.Equal("Renamed", draft.Name);
    }

    [Fact]
    public void Changed_event_fires_on_each_property_change()
    {
        var draft = new ServiceConfigDraft(SampleConfig());
        var fires = 0;
        draft.Changed += () => fires++;

        draft.Name = "A";
        draft.Url = "https://example.com";
        draft.WindowWidth = 1300;

        Assert.Equal(3, fires);
    }

    [Fact]
    public void Changed_event_does_not_fire_when_value_unchanged()
    {
        var draft = new ServiceConfigDraft(SampleConfig());
        var fires = 0;
        draft.Changed += () => fires++;

        draft.Name = "Slack";
        Assert.Equal(0, fires);
    }

    [Fact]
    public void PropertyChanged_fires_with_correct_name()
    {
        var draft = new ServiceConfigDraft(SampleConfig());
        var props = new List<string?>();
        draft.PropertyChanged += (_, e) => props.Add(e.PropertyName);

        draft.WindowHeight = 900;
        // Setter fires the field's own name plus a follow-up for the
        // computed WindowSizeDisplay; assert the field name shows up.
        Assert.Contains(nameof(ServiceConfigDraft.WindowHeight), props);
    }

    [Fact]
    public void WindowHeight_change_notifies_WindowSizeDisplay()
    {
        var draft = new ServiceConfigDraft(SampleConfig());
        var props = new List<string?>();
        draft.PropertyChanged += (_, e) => props.Add(e.PropertyName);

        draft.WindowHeight = 900;
        Assert.Contains("WindowSizeDisplay", props);
    }

    [Fact]
    public void HideToTrayMaster_writes_both_min_and_close()
    {
        var cfg = SampleConfig();
        cfg.MinimizeToTray = false;
        cfg.CloseToTray = false;
        var draft = new ServiceConfigDraft(cfg);
        Assert.False(draft.HideToTrayMaster);

        draft.HideToTrayMaster = true;
        Assert.True(draft.MinimizeToTray);
        Assert.True(draft.CloseToTray);

        draft.HideToTrayMaster = false;
        Assert.False(draft.MinimizeToTray);
        Assert.False(draft.CloseToTray);
    }

    [Fact]
    public void HideToTrayMaster_is_true_when_either_sub_is_on()
    {
        var cfg = SampleConfig();
        cfg.MinimizeToTray = true;
        cfg.CloseToTray = false;
        var draft = new ServiceConfigDraft(cfg);
        Assert.True(draft.HideToTrayMaster);
    }

    [Fact]
    public void Id_is_preserved_from_source()
    {
        var src = SampleConfig();
        var originalId = src.Id;
        var draft = new ServiceConfigDraft(src);
        Assert.Equal(originalId, draft.Snapshot().Id);
    }

    [Fact]
    public void RememberWindowState_round_trips_through_draft()
    {
        var draft = new ServiceConfigDraft(SampleConfig());
        Assert.True(draft.RememberWindowState);   // default
        draft.RememberWindowState = false;
        Assert.False(draft.Snapshot().RememberWindowState);
        draft.Discard();
        Assert.True(draft.RememberWindowState);
    }
}
