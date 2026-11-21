using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
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
    {
        IPropertyStore? propertyStore = null;
        var appUserModelId = IntPtr.Zero;
        try
        {
            appUserModelId = Marshal.StringToCoTaskMemUni($"{Package.Current.Id.FamilyName}!{applicationId}");
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var interfaceId = PropertyStoreInterfaceId;
            var result = SHGetPropertyStoreForWindow(windowHandle, ref interfaceId, out propertyStore);
            Marshal.ThrowExceptionForHR(result);

            var value = new PropVariant
            {
                VariantType = VariantTypeString,
                StringValue = appUserModelId
            };

            var appUserModelIdKey = AppUserModelIdKey;
            Marshal.ThrowExceptionForHR(propertyStore!.SetValue(ref appUserModelIdKey, ref value));
            Marshal.ThrowExceptionForHR(propertyStore.Commit());
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not assign the package application identity to a window: {exception}");
        }
        finally
        {
            if (appUserModelId != IntPtr.Zero)
                Marshal.FreeCoTaskMem(appUserModelId);
            if (propertyStore is not null)
                Marshal.ReleaseComObject(propertyStore);
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

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr windowHandle,
        ref Guid interfaceId,
        out IPropertyStore propertyStore);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint propertyCount);
        [PreserveSig] int GetAt(uint propertyIndex, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
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
