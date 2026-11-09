namespace Gitberth.Components.Shared;

/// <summary>One choice of a Seg: its value, words, icon, a count after it, and a class (gb-ac-lan tints it in an Access colour).</summary>
public sealed record SegOption<T>(T Value, string Text, string? Icon = null, int? Count = null, string? Class = null, bool Disabled = false, string? Title = null);
