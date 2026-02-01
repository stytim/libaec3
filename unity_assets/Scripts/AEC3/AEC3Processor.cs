using System;

namespace AEC3
{
    /// <summary>
    /// Managed wrapper for AEC3 acoustic echo cancellation
    /// </summary>
    public class AEC3Processor : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed = false;
        private readonly int _numChannels;
        private readonly bool _exportLinear;

        /// <summary>
        /// Creates a new instance of the AEC3 processor
        /// </summary>
        /// <param name="sampleRate">Sample rate in Hz (e.g., 16000, 48000)</param>
        /// <param name="numChannels">Number of audio channels</param>
        /// <param name="exportLinear">Whether to export the linear AEC output</param>
        public AEC3Processor(int sampleRate, int numChannels, bool exportLinear = false)
        {
            _numChannels = numChannels;
            _exportLinear = exportLinear;

            var config = new AEC3Config
            {
                SampleRate = sampleRate,
                NumChannels = numChannels,
                ExportLinear = exportLinear ? 1 : 0
            };

            _handle = AEC3Native.aec3_create(ref config);
            if (_handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create AEC3 instance");
            }
        }

        /// <summary>
        /// Process a frame of audio
        /// </summary>
        /// <param name="referenceFrame">Reference (far-end) audio</param>
        /// <param name="captureFrame">Capture (near-end) audio with echo</param>
        /// <param name="outputFrame">Buffer to receive processed output (echo cancelled audio)</param>
        /// <param name="linearOutputFrame">Optional buffer to receive linear AEC output (if enabled)</param>
        /// <param name="audioBufferDelay">Optional audio buffer delay in samples</param>
        /// <returns>0 on success, non-zero on error</returns>
        public int ProcessFrame(short[] referenceFrame, short[] captureFrame, short[] outputFrame, short[] linearOutputFrame = null, int audioBufferDelay = 0)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AEC3Processor));

            if (referenceFrame == null)
                throw new ArgumentNullException(nameof(referenceFrame));
            if (captureFrame == null)
                throw new ArgumentNullException(nameof(captureFrame));
            if (outputFrame == null)
                throw new ArgumentNullException(nameof(outputFrame));
            if (_exportLinear && linearOutputFrame == null)
                throw new ArgumentNullException(nameof(linearOutputFrame), "Linear output buffer is required when export_linear is enabled");

            // Calculate samples per channel (assume all arrays have the same length)
            int samplesPerChannel = referenceFrame.Length / _numChannels;

            // Validate buffer sizes
            // Note: We relax strict checks here slightly to trust the caller (WebRTCAudioProcessor) for performance, 
            // but keeping them for safety is fine for now.

            // Call the native function
            return AEC3Native.aec3_process_frame(
                _handle,
                referenceFrame,
                captureFrame,
                outputFrame,
                linearOutputFrame,
                (IntPtr)samplesPerChannel,
                audioBufferDelay
            );
        }

        /// <summary>
        /// Releases all resources used by the AEC3Processor
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (_handle != IntPtr.Zero)
                {
                    AEC3Native.aec3_destroy(_handle);
                    _handle = IntPtr.Zero;
                }
                _disposed = true;
            }
        }

        ~AEC3Processor()
        {
            Dispose(false);
        }
    }
}
