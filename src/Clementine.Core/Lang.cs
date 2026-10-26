using System.Globalization;

namespace Clementine.Core;

/// <summary>
/// Italiano o inglese. Ogni testo visibile è scritto con le due lingue una accanto all'altra, T("Annulla", "Cancel"):
/// con due lingue è meno codice dei .resx, e una frase non può restare tradotta a metà.
/// </summary>
public static class Lang
{
    public static bool English { get; set; }

    public static string T(string italian, string english) => English ? english : italian;

    /// <summary>"it", "en", oppure "auto": italiano se Windows è in italiano, altrimenti inglese.</summary>
    public static void Apply(string? setting) => English = setting switch
    {
        "it" => false,
        "en" => true,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "it",
    };
}
