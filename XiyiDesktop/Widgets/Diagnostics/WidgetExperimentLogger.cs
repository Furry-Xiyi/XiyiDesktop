using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Windows.ApplicationModel;

namespace XiyiDesktop.Widgets.Diagnostics;

internal sealed class WidgetExperimentLogger
{
    private readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XiyiDesktop", "WidgetResearch", "experiments.log");

    public string LogPath => _logPath;

    public string GetEnvironmentDetails()
    {
        var osVersion = Environment.OSVersion.Version;
        var processDetails = TryGetProcessDetails();
        var packageIdentity = TryGetPackageIdentity();
        var (userName, integrityLevel) = TryGetCurrentWindowsIdentity();

        return string.Join("; ",
            $"OS={osVersion} (build {osVersion.Build})",
            $"{processDetails}",
            $"User={userName}",
            $"Integrity={integrityLevel}",
            $"ProcessArchitecture={RuntimeInformation.ProcessArchitecture}",
            $"OSArchitecture={RuntimeInformation.OSArchitecture}",
            $"PackageIdentity={packageIdentity}");
    }

    public void Write(string operation, string result, string? details = null, Exception? exception = null)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
                .Append(" | ").Append(GetEnvironmentDetails())
                .Append(" | Operation=").Append(Sanitize(operation))
                .Append(" | Result=").Append(Sanitize(result));
            if (!string.IsNullOrWhiteSpace(details))
                line.Append(" | Details=").Append(Sanitize(details));
            if (exception is not null)
            {
                line.Append(" | Exception=").Append(Sanitize(exception.GetType().FullName ?? exception.GetType().Name))
                    .Append(" | HRESULT=0x").Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture))
                    .Append(" | Message=").Append(Sanitize(exception.Message));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, line.AppendLine().ToString(), Encoding.UTF8);
            Debug.WriteLine(line.ToString());
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"Widget experiment log write failed: {logException.Message}");
        }
    }

    private static string TryGetPackageIdentity()
    {
        try
        {
            var package = Package.Current;
            return $"{package.Id.FamilyName} ({package.Id.FullName})";
        }
        catch (Exception exception)
        {
            return $"Unavailable ({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8})";
        }
    }

    private static string TryGetProcessDetails()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return $"Process={process.ProcessName} (PID {process.Id}, Path={Environment.ProcessPath ?? "Unavailable"})";
        }
        catch (Exception exception)
        {
            return $"Process=Unavailable ({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8})";
        }
    }

    private static (string UserName, string IntegrityLevel) TryGetCurrentWindowsIdentity()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var level = "Unavailable";
            var groups = identity.Groups;
            if (groups is not null)
            {
                foreach (var group in groups)
                {
                    var value = group.Value;
                    if (!value.StartsWith("S-1-16-", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ridText = value[(value.LastIndexOf('-') + 1)..];
                    if (int.TryParse(ridText, NumberStyles.None, CultureInfo.InvariantCulture, out var rid))
                        level = rid switch
                        {
                            >= 0x4000 => "System",
                            >= 0x3000 => "High",
                            >= 0x2000 => "Medium",
                            >= 0x1000 => "Low",
                            _ => "Untrusted"
                        };
                    break;
                }
            }

            return (identity.Name ?? Environment.UserName, level);
        }
        catch (Exception exception)
        {
            return (Environment.UserName, $"Unavailable ({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8})");
        }
    }

    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
