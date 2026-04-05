using System;
using System.Runtime.InteropServices;

namespace Narabemi.Mpv
{
    /// <summary>
    /// Thin P/Invoke layer for libmpv.
    /// See https://mpv.io/manual/stable/#client-api for the C API reference.
    /// </summary>
    internal static partial class MpvApi
    {
        private const string LibName = "libmpv-2";

        // --- Lifecycle ---

        [LibraryImport(LibName, EntryPoint = "mpv_create")]
        internal static partial IntPtr Create();

        [LibraryImport(LibName, EntryPoint = "mpv_initialize")]
        internal static partial int Initialize(IntPtr ctx);

        [LibraryImport(LibName, EntryPoint = "mpv_destroy")]
        internal static partial void Destroy(IntPtr ctx);

        [LibraryImport(LibName, EntryPoint = "mpv_terminate_destroy")]
        internal static partial void TerminateDestroy(IntPtr ctx);

        // --- Commands ---

        [LibraryImport(LibName, EntryPoint = "mpv_command")]
        internal static partial int Command(IntPtr ctx, IntPtr args);

        [LibraryImport(LibName, EntryPoint = "mpv_command_string", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int CommandString(IntPtr ctx, string args);

        // --- Properties ---

        [LibraryImport(LibName, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SetOptionString(IntPtr ctx, string name, string data);

        [LibraryImport(LibName, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SetPropertyString(IntPtr ctx, string name, string data);

        [LibraryImport(LibName, EntryPoint = "mpv_get_property_string", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr GetPropertyString(IntPtr ctx, string name);

        [LibraryImport(LibName, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int GetProperty(IntPtr ctx, string name, MpvFormat format, out double data);

        [LibraryImport(LibName, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int GetPropertyFlag(IntPtr ctx, string name, MpvFormat format, out int data);

        [LibraryImport(LibName, EntryPoint = "mpv_set_property", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SetProperty(IntPtr ctx, string name, MpvFormat format, ref double data);

        [LibraryImport(LibName, EntryPoint = "mpv_set_property", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SetPropertyFlag(IntPtr ctx, string name, MpvFormat format, ref int data);

        // --- Events ---

        [LibraryImport(LibName, EntryPoint = "mpv_wait_event")]
        internal static partial IntPtr WaitEvent(IntPtr ctx, double timeout);

        [LibraryImport(LibName, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int ObserveProperty(IntPtr ctx, ulong replyUserdata, string name, MpvFormat format);

        // --- Error ---

        [LibraryImport(LibName, EntryPoint = "mpv_error_string")]
        internal static partial IntPtr ErrorString(int error);

        [LibraryImport(LibName, EntryPoint = "mpv_free")]
        internal static partial void Free(IntPtr data);

        // --- Helpers ---

        internal static string? GetErrorMessage(int error)
        {
            var ptr = ErrorString(error);
            return Marshal.PtrToStringUTF8(ptr);
        }

        internal static string? GetPropertyStringManaged(IntPtr ctx, string name)
        {
            var ptr = GetPropertyString(ctx, name);
            if (ptr == IntPtr.Zero) return null;
            var result = Marshal.PtrToStringUTF8(ptr);
            Free(ptr);
            return result;
        }

        /// <summary>
        /// Builds a null-terminated array of null-terminated UTF-8 strings for mpv_command.
        /// </summary>
        internal static int CommandArgs(IntPtr ctx, params string[] args)
        {
            var ptrs = new IntPtr[args.Length + 1];
            try
            {
                for (int i = 0; i < args.Length; i++)
                    ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
                ptrs[args.Length] = IntPtr.Zero;

                var arrayPtr = Marshal.AllocCoTaskMem(IntPtr.Size * ptrs.Length);
                try
                {
                    Marshal.Copy(ptrs, 0, arrayPtr, ptrs.Length);
                    return Command(ctx, arrayPtr);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(arrayPtr);
                }
            }
            finally
            {
                foreach (var p in ptrs)
                    if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            }
        }
    }

    internal enum MpvFormat
    {
        None = 0,
        String = 1,
        OsdString = 2,
        Flag = 3,
        Int64 = 4,
        Double = 5,
        Node = 6,
        NodeArray = 7,
        NodeMap = 8,
        ByteArray = 9,
    }

    internal enum MpvEventId
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 6,
        GetPropertyReply = 8,
        SetPropertyReply = 9,
        CommandReply = 10,
        StartFile = 16,
        EndFile = 17,
        FileLoaded = 18,
        ClientMessage = 20,
        VideoReconfig = 21,
        AudioReconfig = 22,
        Seek = 23,
        PlaybackRestart = 24,
        PropertyChange = 25,
        QueueOverflow = 26,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEvent
    {
        public MpvEventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventProperty
    {
        public IntPtr Name;
        public MpvFormat Format;
        public IntPtr Data;
    }
}
