using System;
using Vortice.DXGI;

namespace Narabemi.Gpu
{
    /// <summary>
    /// Vortice's IDXGIKeyedMutex.AcquireSync returns void and does not surface WAIT_TIMEOUT.
    /// This helper calls the COM vtable directly to retrieve the raw HRESULT so callers
    /// can distinguish S_OK (acquired), WAIT_TIMEOUT (timed out), and real failures.
    /// </summary>
    internal static unsafe class DxgiKeyedMutexHelper
    {
        // HRESULT values returned by IDXGIKeyedMutex::AcquireSync.
        internal const int S_OK          = 0x00000000;
        internal const int WAIT_TIMEOUT  = 0x00000102; // success code — not a failure HRESULT
        internal const int WAIT_ABANDONED = 0x00000080;

        // vtable slot indices for IDXGIKeyedMutex COM interface:
        //   [0-2]  IUnknown            (QueryInterface, AddRef, Release)
        //   [3-6]  IDXGIObject         (SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent)
        //   [7]    IDXGIDeviceSubObject (GetDevice)
        //   [8-9]  IDXGIKeyedMutex     (AcquireSync, ReleaseSync)
        private const int VtblSlotAcquireSync = 8;

        /// <summary>
        /// Calls IDXGIKeyedMutex::AcquireSync via the raw COM vtable.
        /// Returns the HRESULT: 0 = acquired, WAIT_TIMEOUT = timed out, negative = error.
        /// </summary>
        internal static int AcquireSync(IDXGIKeyedMutex mutex, ulong key, int timeoutMs)
        {
            var nativePtr = mutex.NativePointer;
            var vtbl = *(IntPtr**)nativePtr;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, ulong, int, int>)vtbl[VtblSlotAcquireSync];
            return fn(nativePtr, key, timeoutMs);
        }
    }
}
