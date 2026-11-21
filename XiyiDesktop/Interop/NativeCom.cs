using System;
using System.Runtime.InteropServices;

namespace XiyiDesktop.Interop;

/// <summary>Calls native IUnknown interfaces without runtime COM wrappers, including under NativeAOT.</summary>
internal static unsafe class NativeCom
{
    public static ApartmentScope InitializeApartment(uint concurrencyModel = 2, bool allowDifferentApartment = false)
    {
        var result = CoInitializeEx(IntPtr.Zero, concurrencyModel);
        if (allowDifferentApartment && result == unchecked((int)0x80010106))
            return new ApartmentScope(false);
        Marshal.ThrowExceptionForHR(result);
        return new ApartmentScope(true);
    }

    public static IntPtr CreateInstance(Guid classId, Guid interfaceId, uint classContext = 5)
    {
        var result = CoCreateInstance(ref classId, IntPtr.Zero, classContext, ref interfaceId, out var instance);
        if (result < 0)
        {
            Release(instance);
            Marshal.ThrowExceptionForHR(result);
        }
        if (instance == IntPtr.Zero)
            throw new COMException("COM activation returned a null interface.");
        return instance;
    }

    public static IntPtr QueryInterface(IntPtr instance, Guid interfaceId)
    {
        IntPtr queried = IntPtr.Zero;
        var query = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)GetMethod(instance, 0);
        var result = query(instance, &interfaceId, &queried);
        if (result < 0)
        {
            Release(queried);
            Marshal.ThrowExceptionForHR(result);
        }
        if (queried == IntPtr.Zero)
            throw new COMException("QueryInterface returned a null interface.");
        return queried;
    }

    public static IntPtr TryQueryInterface(IntPtr instance, Guid interfaceId)
    {
        if (instance == IntPtr.Zero)
            return IntPtr.Zero;
        IntPtr queried = IntPtr.Zero;
        var query = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)GetMethod(instance, 0);
        if (query(instance, &interfaceId, &queried) < 0)
        {
            Release(queried);
            return IntPtr.Zero;
        }
        return queried;
    }

    public static void Release(IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)GetMethod(instance, 2);
            _ = release(instance);
        }
    }

    public static void* GetMethod(IntPtr instance, int slot)
    {
        if (instance == IntPtr.Zero)
            throw new ArgumentNullException(nameof(instance));
        return (*(void***)instance)[slot];
    }

    public sealed class ApartmentScope : IDisposable
    {
        private bool _mustUninitialize;

        internal ApartmentScope(bool mustUninitialize) => _mustUninitialize = mustUninitialize;

        public void Dispose()
        {
            if (_mustUninitialize)
            {
                _mustUninitialize = false;
                CoUninitialize();
            }
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid classId, IntPtr outer, uint classContext, ref Guid interfaceId, out IntPtr instance);
}
