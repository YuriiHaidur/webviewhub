using Microsoft.Win32;

namespace WebViewHub.Services;

/// <summary>
/// Registers / unregisters our exe as the system-wide handler for a
/// custom URI scheme like "spotify://" or "slack://". Writes only to
/// HKCU (no admin rights), so the registration is per-user and easy
/// to roll back.
///
/// Layout written for scheme "spotify":
///
///   HKCU\Software\Classes\spotify
///     (default)             = "URL:spotify Protocol"
///     "URL Protocol"        = ""
///     DefaultIcon\(default) = "<exe>,1"
///     shell\open\command\(default) = "\"<exe>\" --service=<id> --uri=\"%1\""
///
/// On launch, the OS executes the command above with %1 replaced by
/// the original URI. App.OnPipeCommand parses --uri and routes the
/// translation through ProtocolUrlTranslator.
/// </summary>
public static class ProtocolHandlerManager
{
    private const string ClassesPrefix = @"Software\Classes\";

    /// <summary>
    /// Marker we write under each handler key so we can later tell
    /// whether the registration is ours and safe to remove (vs. one
    /// that pre-existed and we shouldn't touch on uninstall).
    /// </summary>
    private const string OwnerMarkerName = "WebViewHubServiceId";

    public static void Register(string scheme, string serviceId, string exePath)
    {
        if (string.IsNullOrWhiteSpace(scheme)) return;
        scheme = scheme.Trim().ToLowerInvariant();
        if (!IsValidScheme(scheme)) throw new ArgumentException($"Invalid scheme: {scheme}");

        var keyPath = ClassesPrefix + scheme;
        using var root = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        if (root == null) throw new InvalidOperationException($"Cannot open HKCU\\{keyPath}");

        // Standard Windows custom-protocol handshake: (default) describes
        // the protocol, "URL Protocol" (empty REG_SZ) marks it as a URL
        // protocol so the shell will hand the full URI to the command.
        root.SetValue("", $"URL:{scheme} Protocol", RegistryValueKind.String);
        root.SetValue("URL Protocol", "", RegistryValueKind.String);
        root.SetValue(OwnerMarkerName, serviceId, RegistryValueKind.String);

        using (var icon = root.CreateSubKey("DefaultIcon", writable: true))
        {
            icon?.SetValue("", $"\"{exePath}\",1", RegistryValueKind.String);
        }

        using (var cmd = root.CreateSubKey(@"shell\open\command", writable: true))
        {
            // %1 is replaced by Windows with the original URI before launch.
            // Quoting the URI is critical: many schemes embed colons (e.g.
            // spotify:track:abc) which would otherwise be argv-split badly.
            var line = $"\"{exePath}\" --service={serviceId} --uri=\"%1\"";
            cmd?.SetValue("", line, RegistryValueKind.String);
        }

        Logger.Info($"Protocol '{scheme}://' registered → service {serviceId}");
    }

    /// <summary>
    /// Removes the registration, but only if we own it (marker matches
    /// or is missing — to allow upgrades from older versions that didn't
    /// stamp the marker).
    /// </summary>
    public static void Unregister(string scheme, string? expectedServiceId = null)
    {
        if (string.IsNullOrWhiteSpace(scheme)) return;
        scheme = scheme.Trim().ToLowerInvariant();
        if (!IsValidScheme(scheme)) return;

        var keyPath = ClassesPrefix + scheme;
        try
        {
            using (var root = Registry.CurrentUser.OpenSubKey(keyPath, writable: false))
            {
                if (root == null) return; // Nothing to do.

                var owner = root.GetValue(OwnerMarkerName) as string;
                if (!string.IsNullOrEmpty(expectedServiceId) &&
                    !string.IsNullOrEmpty(owner) &&
                    !string.Equals(owner, expectedServiceId, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info($"Protocol '{scheme}://' is owned by another service ({owner}); leaving alone.");
                    return;
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            Logger.Info($"Protocol '{scheme}://' unregistered.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Unregister '{scheme}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the service id this scheme is registered to via the
    /// owner marker, or null if the key doesn't exist or isn't ours.
    /// </summary>
    public static string? GetOwnerServiceId(string scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme)) return null;
        scheme = scheme.Trim().ToLowerInvariant();
        if (!IsValidScheme(scheme)) return null;

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ClassesPrefix + scheme, writable: false);
            return root?.GetValue(OwnerMarkerName) as string;
        }
        catch { return null; }
    }

    private static bool IsValidScheme(string scheme)
    {
        if (string.IsNullOrEmpty(scheme) || scheme.Length > 64) return false;
        if (!char.IsLetter(scheme[0])) return false;
        foreach (var c in scheme)
        {
            if (!(char.IsLetterOrDigit(c) || c == '+' || c == '-' || c == '.'))
                return false;
        }
        return true;
    }
}
