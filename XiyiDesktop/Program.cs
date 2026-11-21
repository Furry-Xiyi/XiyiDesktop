using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using XiyiDesktop.Widgets.Providers;

namespace XiyiDesktop;

internal static class Program
{
    private static App? _app;

    [STAThread]
    public static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        ComWrappers.RegisterForMarshalling(new StrategyBasedComWrappers());
        if (args.Any(argument => argument.Equals("-Embedding", StringComparison.OrdinalIgnoreCase) ||
                                 argument.Equals("/Embedding", StringComparison.OrdinalIgnoreCase)))
        {
            RunWidgetProviderServer();
            return;
        }

        Application.Start(_ =>
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(dispatcherQueue));
            _app = new App();
        });
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IClassFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SmtcWidgetProviderFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SmtcWidgetProvider))]
    private static void RunWidgetProviderServer()
    {
        var factory = new SmtcWidgetProviderFactory();
        var clsid = Guid.Parse("4E5FEE0F-4F48-4C5D-9E74-3A3D9D5E2D4C");
        Marshal.ThrowExceptionForHR(CoRegisterClassObject(clsid, factory, 0x4, 0x1, out var registrationCookie));
        try
        {
            SmtcWidgetProvider.WaitForLastWidgetDeletion();
        }
        finally
        {
            Marshal.ThrowExceptionForHR(CoRevokeClassObject(registrationCookie));
            GC.KeepAlive(factory);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint registrationCookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint registrationCookie);
}
