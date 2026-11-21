using System;

namespace XiyiDesktop.DesktopHost
{
    public interface IDesktopLayerHost
    {
        string Name { get; }

        bool TryAttach(IntPtr windowHandle, out DesktopHostAttachment? attachment);
    }

    public enum DesktopHostKind
    {
        ShellViewOverlay
    }

    public sealed class DesktopHostAttachment
    {
        public DesktopHostAttachment(IntPtr parentHandle, string hostName, DesktopHostKind kind,
            IntPtr zOrderAnchor, bool usesUndocumentedShellBehavior)
        {
            ParentHandle = parentHandle;
            HostName = hostName;
            Kind = kind;
            ZOrderAnchor = zOrderAnchor;
            UsesUndocumentedShellBehavior = usesUndocumentedShellBehavior;
        }

        public IntPtr ParentHandle { get; }
        public string HostName { get; }
        public DesktopHostKind Kind { get; }
        public IntPtr ZOrderAnchor { get; }
        public bool UsesUndocumentedShellBehavior { get; }
    }
}

