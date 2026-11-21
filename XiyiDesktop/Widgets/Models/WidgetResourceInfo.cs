using System;

namespace XiyiDesktop.Widgets.Models;

internal sealed record WidgetResourceInfo(
    string Kind,
    string Theme,
    string PackageRelativePath,
    string? ResolvedPath,
    string? AltText);
