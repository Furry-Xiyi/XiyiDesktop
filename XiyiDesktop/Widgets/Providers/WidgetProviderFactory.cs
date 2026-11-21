using Microsoft.Windows.Widgets.Providers;
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using WinRT;

namespace XiyiDesktop.Widgets.Providers;

internal static class ComGuids
{
    public const string IClassFactory = "00000001-0000-0000-C000-000000000046";
}

[GeneratedComInterface]
[Guid(ComGuids.IClassFactory)]
internal partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr outer, in Guid interfaceId, out IntPtr instance);

    [PreserveSig]
    int LockServer(int lockServer);
}

[GeneratedComClass]
internal sealed partial class SmtcWidgetProviderFactory : IClassFactory
{
    private const int ClassENoAggregation = unchecked((int)0x80040110);

    public int CreateInstance(IntPtr outer, in Guid interfaceId, out IntPtr instance)
    {
        instance = IntPtr.Zero;
        if (outer != IntPtr.Zero)
            return ClassENoAggregation;

        IntPtr inspectable = IntPtr.Zero;
        try
        {
            inspectable = MarshalInspectable<IWidgetProvider>.FromManaged(new SmtcWidgetProvider());
            return Marshal.QueryInterface(inspectable, in interfaceId, out instance);
        }
        catch (Exception exception)
        {
            return Marshal.GetHRForException(exception);
        }
        finally
        {
            if (inspectable != IntPtr.Zero)
                Marshal.Release(inspectable);
        }
    }

    public int LockServer(int lockServer) => 0;
}
