using System;
using System.IO;

namespace MitosScope.App.Services;

/// <summary>
/// The addon app key. Release builds have it built in (EmbeddedAppKey.Secret.cs, git-ignored, only
/// on the machine that builds the releases). API_KEY.txt next to the executable, if present, takes
/// precedence, so a developer can use their own key without rebuilding. ADDON_API.md: the key ships
/// with the addon, it's not a secret from the player, but it shouldn't be published separately.
/// </summary>
public static class AppKeyProvider
{
    public const string FileName = "API_KEY.txt";

    public static string? TryLoad()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(path))
        {
            var fromFile = File.ReadAllText(path).Trim();
            if (fromFile.Length > 0)
                return fromFile;
        }

#if EMBEDDED_APP_KEY
        return EmbeddedAppKey.Decode();
#else
        return null;
#endif
    }
}
