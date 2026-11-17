namespace Gitberth.Components.Shared;

/// <summary>
/// One choice of a Seg: its value, words, icon, a count after it, a class (gb-ac-lan tints it in an Access colour), a short
/// note on hover (Title), and its tip's key in the catalog (Help/Tips; give it by name, Tip: "app.settings.restart.always",
/// so the tests find it). With a Tip, Title becomes a line of its card.
/// </summary>
public sealed record SegOption<T>(T Value, string Text, string? Icon = null, int? Count = null, string? Class = null, bool Disabled = false, string? Title = null, string? Tip = null);
