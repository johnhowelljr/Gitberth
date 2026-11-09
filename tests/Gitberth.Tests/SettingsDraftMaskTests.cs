using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Services;

namespace Gitberth.Tests;

/// <summary>
/// SEC-3: the Settings tab shows a command that carries a secret (cloudflared's --token) masked. Editing around the mask
/// keeps the real secret; a mask left where Gitberth can't give the secret back is refused, never written in as the token.
/// The token here is a made-up value.
/// </summary>
public sealed class SettingsDraftMaskTests
{
    private const string Token = "made-up-token-0123";

    private static ContainerSettings Cloudflared() => new()
    {
        Image = "cloudflare/cloudflared:latest",
        Command = ["tunnel", "--no-autoupdate", "run", "--token", Token],
    };

    [Fact]
    public void SEC3_the_command_is_shown_masked_until_its_typed_over()
    {
        var draft = ContainerSettingsDraft.From(Cloudflared());
        Assert.True(draft.CommandHasSecrets);
        Assert.Equal($"tunnel --no-autoupdate run --token {ContainerService.Masked}", draft.CommandShown);
        Assert.DoesNotContain(Token, draft.CommandShown);
        Assert.Null(draft.CommandProblem);
    }

    [Fact]
    public void SEC3_editing_the_masked_command_keeps_the_real_token()
    {
        var draft = ContainerSettingsDraft.From(Cloudflared());
        // The owner adds an option in the middle of the masked line: the words move, the token is given back.
        draft.Command = $"tunnel --no-autoupdate --loglevel debug run --token {ContainerService.Masked}";

        Assert.Null(draft.CommandProblem);
        Assert.Equal(["tunnel", "--no-autoupdate", "--loglevel", "debug", "run", "--token", Token], draft.ToSettings().Command);
        Assert.Contains(SettingField.Command, draft.ChangedFields());
        // What the field shows is still what was typed: the mask, never the token.
        Assert.DoesNotContain(Token, draft.CommandShown);
    }

    [Fact]
    public void SEC3_typing_the_masked_line_back_is_no_change()
    {
        var draft = ContainerSettingsDraft.From(Cloudflared());
        draft.Command = draft.CommandShown + " ";
        Assert.DoesNotContain(SettingField.Command, draft.ChangedFields());
        Assert.Equal(Cloudflared().Command, draft.ToSettings().Command);
    }

    [Fact]
    public void SEC3_refuses_a_leftover_mask()
    {
        var draft = ContainerSettingsDraft.From(Cloudflared());
        // The mask moved after another option: Gitberth won't give the token to --password, nor write the mask in.
        draft.Command = $"tunnel run --password {ContainerService.Masked}";

        Assert.NotNull(draft.CommandProblem);
        Assert.Contains(draft.CommandProblem!, draft.Problems());
        Assert.Equal(Cloudflared().Command, draft.ToSettings().Command);
        Assert.DoesNotContain(SettingField.Command, draft.ChangedFields());
    }

    [Fact]
    public void SEC3_a_new_token_typed_over_the_mask_replaces_it()
    {
        var draft = ContainerSettingsDraft.From(Cloudflared());
        draft.Command = "tunnel --no-autoupdate run --token another-made-up-token";
        Assert.Null(draft.CommandProblem);
        Assert.Equal("another-made-up-token", draft.ToSettings().Command![^1]);
    }

    [Fact]
    public void SEC3_an_option_with_its_value_after_an_equals_sign_and_a_url_password_come_back_too()
    {
        IReadOnlyList<string> loaded = ["run", "--token=abc123", "--db", "postgres://app:pw123@db/app"];
        var masked = ContainerService.MaskArgs(loaded);
        Assert.Equal(["serve", "--token=abc123", "--db", "postgres://app:pw123@db/app"],
            ContainerSettingsDraft.Unmask(["serve", masked[1], "--db", masked[3]], loaded));
    }
}
