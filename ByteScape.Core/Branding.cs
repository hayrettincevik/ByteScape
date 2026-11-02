namespace ByteScape;

/// <summary>Product identity used by every window, file name and message. Change it here only.</summary>
public static class Branding
{
    public const string Name = "ByteScape";
    public const string Version = "1.0.0";
    public const string Slogan = /*loc*/ "See where your disk space went.";

    /// <summary>Project home page (the About window's link): the source code, the releases and the issues. Empty hides it.</summary>
    public const string HomeUrl = "https://github.com/hayrettincevik/ByteScape";

    /// <summary>
    /// The update description the portable exe reads when the user asks (or switched on the weekly check): the project's
    /// latest GitHub release ("…/releases/latest" of the GitHub API), or an https address of ByteScape's own JSON
    /// (store/make-update-manifest.ps1). Empty hides update checks.
    /// </summary>
    public const string UpdateUrl = "https://api.github.com/repos/hayrettincevik/ByteScape/releases/latest";

    /// <summary>
    /// Who holds the copyright: the LICENSE file at the root of the repository (embedded in the program), the About window,
    /// the exe's file properties (the csproj Copyright line repeats it). Empty would make the texts say "the author of ByteScape".
    /// </summary>
    public const string Publisher = "ByteScape contributors";

    public const string CopyrightYear = "2026";

    /// <summary>
    /// ByteScape is free and open source (MIT). An optional donation page ("Support ByteScape" in the About window
    /// of the portable exe; the Store package does not show it); empty hides it.
    /// </summary>
    public const string DonateUrl = "";

    /// <summary>
    /// Where users report problems ("Report a problem" → "Open an issue"): the project's new-issue page, opened in the browser
    /// with the description filled in; empty hides that button and says "send it to the developer".
    /// </summary>
    public const string IssueUrl = "https://github.com/hayrettincevik/ByteScape/issues/new";

    /// <summary>One sentence about the program for the About window.</summary>
    public const string Description = /*loc*/ "Every block on the map is a file or a folder, drawn as big as the space it takes on the disk.";
}
