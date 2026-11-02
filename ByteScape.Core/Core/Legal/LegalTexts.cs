using System.Reflection;

namespace ByteScape.Core;

/// <summary>
/// The licence (MIT, the LICENSE file at the root of the repository) and the third-party notices, embedded in the program
/// (About → "License terms", "Third-party notices") and written next to the downloads by store/build-portable.ps1 and into
/// the Store package by store/build-msix.ps1. {Name}, {Year} and {Publisher} in the notices are filled from <see cref="Branding"/>.
/// </summary>
public static class LegalTexts
{
    /// <summary>The MIT licence, as written (its legal text is not translated).</summary>
    public static string LicenseTerms() => Read("LICENSE.txt");

    /// <summary>The licences of the .NET runtime, WPF and d3-hierarchy, and the notices those projects publish.</summary>
    public static string ThirdPartyNotices() => Fill(Read("THIRD-PARTY-NOTICES.txt"), "en");

    /// <summary>The copyright holder as the texts name it: <see cref="Branding.Publisher"/>, or "the author of …" while that is empty.</summary>
    public static string Publisher(string language) =>
        Branding.Publisher.Length > 0 ? Branding.Publisher : language == "tr" ? Branding.Name + "'in yazarı" : "the author of " + Branding.Name;

    internal static string Fill(string text, string language) => text
        .Replace("{Name}", Branding.Name)
        .Replace("{Year}", Branding.CopyrightYear)
        .Replace("{Publisher}", Publisher(language));

    private static string Read(string file)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ByteScape.Core.Legal." + file)
            ?? throw new InvalidOperationException(file + " is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
