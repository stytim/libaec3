using System;
using System.Runtime.InteropServices;

namespace AEC3
{
    /// <summary>
    /// Configuration structure for AEC3
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AEC3Config
    {
        public int SampleRate;      // Sample rate in Hz
        public int NumChannels;     // Number of channels
        public int ExportLinear;    // Whether to export linear AEC output (0 = false, 1 = true)
    }

    /// <summary>
    /// P/Invoke declarations for the AEC3 native library
    /// </summary>
    internal static class AEC3Native
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string LibName = "__Internal";
#else
        // In Unity, the plugin name usually matches the filename without extension/prefix.
        // Mac: libaec3.dylib -> "aec3"
        // Windows: aec3.dll -> "aec3"
        // Android: libaec3.so -> "aec3"
        private const string LibName = "aec3";
#endif

        // Handle for the AEC3 instance (opaque pointer)
        [StructLayout(LayoutKind.Sequential)]
        internal struct AEC3Handle { IntPtr handle; }

        /// <summary>
        /// Create a new AEC3 instance
        /// </summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr aec3_create(ref AEC3Config config);

        /// <summary>
        /// Process a frame of audio
        /// </summary>
        /// <returns>0 on success, non-zero on error</returns>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int aec3_process_frame(
            IntPtr handle,
            [In] short[] referenceFrame,    // Reference (far-end) audio
            [In] short[] captureFrame,      // Capture (near-end) audio
            [Out] short[] outputFrame,      // Processed output
            [Out] short[] linearOutputFrame, // Linear AEC output (can be null if not enabled)
            IntPtr frameSize,                // Number of samples per channel
            int bufferDelay = 0             // Optional audio buffer delay in samples
        );

        /// <summary>
        /// Destroy an AEC3 instance
        /// </summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void aec3_destroy(IntPtr handle);
    }
}
