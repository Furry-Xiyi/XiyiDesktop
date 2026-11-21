using System;
using System.IO;

namespace XiyiDesktop.Widgets.Discovery;

internal static class WidgetResourceResolver
{
    public static string? Resolve(string? packageRoot, string? publicFolder, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return null;

        foreach (var root in new[] { publicFolder, packageRoot })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            try
            {
                var fullRoot = Path.GetFullPath(root);
                var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A malformed provider resource path should not fail catalog discovery.
            }
        }

        return null;
    }
}
