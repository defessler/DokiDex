using SwarmUI.Core;
using SwarmUI.Utils;

namespace DokiGen.Extensions.ThemeExtension;

/// <summary>
/// DokiGen Void — registers the on-brand SwarmUI theme (void / cyan / gold) so it
/// appears in User Settings -> Theme. Upgrade-safe: lives in src/Extensions/ as a
/// separate folder, so a `git pull` of SwarmUI core never touches it.
/// Recipe per docs/Making Extensions.md "Custom Themes".
/// </summary>
public class DokiGenThemeExtension : Extension
{
    public override void OnPreInit()
    {
        // Expose the CSS as a static asset reachable at
        // /ExtensionFile/<ExtensionName>/Assets/dokigen.css.
        // MUST be OtherAssets (NOT StyleSheetFiles) for a theme sheet.
        OtherAssets.Add("Assets/dokigen.css");
    }

    public override void OnInit()
    {
        // Layer the DokiGen variables ON TOP of the modern.css base (fonts/layout/
        // color-mix derivations). Order matters: later file overrides earlier.
        // SwarmUI theme paths are ROOT-RELATIVE (no leading slash): every built-in theme
        // (WebServer.cs) uses "css/themes/..." and extension files use "ExtensionFile/<Name>/..."
        // (Extension.cs) — kept relative so the UI also works behind a subpath reverse proxy. isDark: true.
        Program.Web.RegisterTheme(new(
            "dokigen",
            "DokiGen Void",
            ["css/themes/modern.css", $"ExtensionFile/{ExtensionName}/Assets/dokigen.css"],
            true));
        Logs.Info("DokiGen Void theme registered.");
    }
}
