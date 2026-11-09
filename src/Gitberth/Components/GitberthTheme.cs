using MudBlazor;

namespace Gitberth.Components;

/// <summary>
/// Gitberth's dark theme, from the design system's tokens (wwwroot/app.css): near-black surfaces with a blue cast, one
/// blue accent, and status colours that are the only saturated ones. MudBlazor's own components (dialogs, menus,
/// selects) pick these up; app.css tunes the rest.
/// </summary>
public static class GitberthTheme
{
    private static readonly string[] Sans = ["Inter", "Segoe UI", "system-ui", "-apple-system", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#4c8dff",
            PrimaryContrastText = "#ffffff",
            Secondary = "#8b5cf6",
            Tertiary = "#34d6c0",
            Info = "#6cb4ff",
            Success = "#3ddc97",
            Warning = "#f5c04e",
            Error = "#ff6b75",
            Dark = "#090b10",
            Black = "#05070a",
            Background = "#090b10",
            BackgroundGray = "#151922",
            Surface = "#0f1218",
            AppbarBackground = "rgba(9,11,16,.78)",
            AppbarText = "#eef1f6",
            DrawerBackground = "#0c0f15",
            DrawerText = "#aab3c2",
            DrawerIcon = "#868fa0",
            TextPrimary = "#eef1f6",
            TextSecondary = "#aab3c2",
            TextDisabled = "#5d6676",
            ActionDefault = "#aab3c2",
            ActionDisabled = "#5d6676",
            ActionDisabledBackground = "#1b2029",
            LinesDefault = "rgba(255,255,255,.085)",
            LinesInputs = "rgba(255,255,255,.14)",
            TableLines = "rgba(255,255,255,.055)",
            TableHover = "rgba(255,255,255,.028)",
            TableStriped = "rgba(255,255,255,.015)",
            Divider = "rgba(255,255,255,.055)",
            DividerLight = "rgba(255,255,255,.04)",
            Skeleton = "rgba(255,255,255,.05)",
            OverlayDark = "rgba(3,5,8,0.72)",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Sans, FontSize = ".875rem", LineHeight = "1.5", LetterSpacing = "0" },
            H4 = new H4Typography { FontFamily = Sans, FontSize = "1.75rem", FontWeight = "650", LineHeight = "1.2", LetterSpacing = "-.025em" },
            H5 = new H5Typography { FontFamily = Sans, FontSize = "1.25rem", FontWeight = "650", LineHeight = "1.3", LetterSpacing = "-.018em" },
            H6 = new H6Typography { FontFamily = Sans, FontSize = ".9375rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "-.01em" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Sans, FontSize = ".875rem", FontWeight = "600", LineHeight = "1.5", LetterSpacing = "0" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Sans, FontSize = ".8125rem", FontWeight = "600", LineHeight = "1.5", LetterSpacing = "0" },
            Body1 = new Body1Typography { FontFamily = Sans, FontSize = ".875rem", LineHeight = "1.5", LetterSpacing = "0" },
            Body2 = new Body2Typography { FontFamily = Sans, FontSize = ".8125rem", LineHeight = "1.5", LetterSpacing = "0" },
            Button = new ButtonTypography { FontFamily = Sans, FontSize = ".8125rem", FontWeight = "600", LineHeight = "1.75", LetterSpacing = "0", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = Sans, FontSize = ".75rem", LineHeight = "1.35", LetterSpacing = "0" },
            Overline = new OverlineTypography { FontFamily = Sans, FontSize = ".6875rem", FontWeight = "600", LetterSpacing = ".07em" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "7px",
            AppbarHeight = "56px",
            DrawerWidthLeft = "236px",
        },
    };
}
