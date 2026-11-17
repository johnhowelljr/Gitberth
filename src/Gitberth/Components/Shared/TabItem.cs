namespace Gitberth.Components.Shared;

/// <summary>
/// One tab of a TabBar: its key (the URL segment), words, icon and link, a count of unsaved changes, shorter words for a
/// phone, and its tip's key in the catalog (Help/Tips; give it by name, Tip: "app.tab.network", so the tests find it).
/// </summary>
public sealed record TabItem(string Key, string Text, string? Icon, string Href, int? Badge = null, bool Dot = false, string? Short = null, string? Tip = null);
