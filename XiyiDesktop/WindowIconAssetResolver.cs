using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace XiyiDesktop;

internal static class WindowIconAssetResolver
{
    private const ushort VariantTypeString = 31;
    private static readonly Guid PropertyStoreInterfaceId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);

    internal static void AssignPackageApplicationIdentity(Window window, string applicationId)
        => AssignPackageApplicationIdentity(WinRT.Interop.WindowNative.GetWindowHandle(window), applicationId);

    internal static void AssignPackageApplicationIdentity(IntPtr windowHandle, string applicationId)
    {
        var propertyStore = IntPtr.Zero;
        var appUserModelId = IntPtr.Zero;
        try
        {
            appUserModelId = Marshal.StringToCoTaskMemUni($"{Package.Current.Id.FamilyName}!{applicationId}");
            var interfaceId = PropertyStoreInterfaceId;
            var result = SHGetPropertyStoreForWindow(windowHandle, ref interfaceId, out propertyStore);
            Marshal.ThrowExceptionForHR(result);

            var value = new PropVariant
            {
                VariantType = VariantTypeString,
                StringValue = appUserModelId
            };

            var appUserModelIdKey = AppUserModelIdKey;
            Marshal.ThrowExceptionForHR(SetPropertyValue(propertyStore, appUserModelIdKey, value));
            Marshal.ThrowExceptionForHR(CommitPropertyStore(propertyStore));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not assign the package application identity to a window: {exception}");
        }
        finally
        {
            if (appUserModelId != IntPtr.Zero)
                Marshal.FreeCoTaskMem(appUserModelId);
            ReleasePropertyStore(propertyStore);
        }
    }

    internal static IconElement CreateTitleBarIcon(string assetDirectory)
    {
        var assetUri = new Uri($"ms-appx:///Assets/{assetDirectory}/Square44x44Logo.png");
        try
        {
            return new ImageIcon { Source = new BitmapImage(assetUri) };
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not load the {assetDirectory} title bar icon: {exception.Message}");
            return new ImageIcon();
        }
    }

    internal static void SetWindowIcon(Window window, string assetDirectory)
    {
        try
        {
            var iconPath = Path.Combine(
                Package.Current.InstalledLocation.Path,
                "Assets",
                assetDirectory,
                "AppIcon.ico");
            window.AppWindow.SetIcon(iconPath);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not set the {assetDirectory} window icon: {exception.Message}");
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr windowHandle,
        ref Guid interfaceId,
        out IntPtr propertyStore);

    // Use the native IPropertyStore vtable directly so Native AOT does not need
    // the built-in COM interface marshaller for this optional window property.
    private static unsafe int SetPropertyValue(IntPtr propertyStore, PropertyKey key, PropVariant value)
    {
        var vtable = *(IntPtr**)propertyStore;
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, PropVariant*, int>)vtable[6];
        return setValue(propertyStore, &key, &value);
    }

    private static unsafe int CommitPropertyStore(IntPtr propertyStore)
    {
        var vtable = *(IntPtr**)propertyStore;
        var commit = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[7];
        return commit(propertyStore);
    }

    private static unsafe void ReleasePropertyStore(IntPtr propertyStore)
    {
        if (propertyStore == IntPtr.Zero)
            return;

        var vtable = *(IntPtr**)propertyStore;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
        release(propertyStore);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr StringValue;
    }
}
