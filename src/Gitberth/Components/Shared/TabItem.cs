namespace Gitberth.Components.Shared;

/// <summary>One tab of a TabBar: its key (the URL segment), words, icon and link, a count of unsaved changes, and shorter words for a phone.</summary>
public sealed record TabItem(string Key, string Text, string? Icon, string Href, int? Badge = null, bool Dot = false, string? Short = null);
