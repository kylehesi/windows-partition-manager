using System.Globalization;
using System.Resources;

namespace WindowsPartitionManager.Core.Localization;

/// <summary>
/// The messages Core and the platform layer hand to the user: validation issues, safety warnings,
/// progress, the shrink report. <c>CoreStrings.resx</c> is the neutral (English) set and
/// <c>CoreStrings.tr.resx</c> the Turkish satellite.
/// </summary>
/// <remarks>
/// The culture is set explicitly rather than taken from the thread: it stays English unless the
/// desktop app switches it with the user's language choice, so the CLI and the tests keep their
/// English output whatever language Windows itself runs in.
/// </remarks>
public static class CoreText
{
    private static readonly ResourceManager Manager =
        new("WindowsPartitionManager.Core.Localization.CoreStrings", typeof(CoreText).Assembly);

    public static CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>The string for <paramref name="key"/>, or the key itself when it is missing.</summary>
    public static string Get(string key) => Manager.GetString(key, Culture) ?? key;

    /// <summary>The string for <paramref name="key"/> with the arguments substituted (numbers formatted invariantly).</summary>
    public static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, Get(key), args);

    /// <summary>
    /// The display name of a partition type. The platform layer describes types in English
    /// ("Basic data", "Recovery", ...); names without a translation are shown as they are.
    /// </summary>
    public static string PartitionType(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var key = "PartitionType_" + new string(description.Where(char.IsLetterOrDigit).ToArray());
        return Manager.GetString(key, Culture) ?? description;
    }
}
