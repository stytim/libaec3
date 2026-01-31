using System;
using System.Threading; // Required for Interlocked
using System.Threading.Tasks;
using JetBrains.Annotations;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Scripting;
using Utilities.Audio; // Required to use PCMEncoder from the package

namespace App.Audio
{
    /// <summary>
    /// A local copy of StreamAudioSource, customized for A2F synchronization.
    /// Acts as the authoritative time source for A/V sync.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class SyncAudioSource : MonoBehaviour
    {
        // Allow implicit conversion to AudioSource for convenience
        [Preserve]
        public static implicit operator AudioSource(SyncAudioSource syncSource)
            => syncSource.audioSource;

        [SerializeField]
        private AudioSource audioSource;

        // Use a standard managed array as a ring buffer to avoid NativeMemory issues on Android
        private float[] _audioBuffer;
        private int _writeIndex = 0;
        private int _readIndex = 0;
        private int _count = 0;
        private readonly int _bufferSize = 48000 * 300; // 10 seconds of buffer approx
        private readonly object _lock = new object();

        // --- SYNC VARIABLES ---
        private long _totalSamplesPlayed = 0;

        /// <summary>
        /// Returns the exact playback time in seconds based on actual samples consumed by the audio thread.
        /// Use this instead of Time.time for animation sync.
        /// </summary>
        public float PlaybackTime
        {
            get
            {
                // Thread-safe read.
                long currentSamples = Interlocked.Read(ref _totalSamplesPlayed);
                return (float)((double)currentSamples / AudioSettings.outputSampleRate);
            }
        }

        // Thread-safe accesor for current buffering latency in milliseconds
        public int CurrentLatencyMs
        {
            get
            {
                if (_cachedSampleRate == 0) return 0;
                // _count is volatileish (modified on audio thread), but reading int is atomic enough for this estimate
                return (_count * 1000) / _cachedSampleRate;
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_lock) return _count == 0;
            }
        }

        private void OnValidate()
        {
            if (audioSource == null)
            {
                audioSource = GetComponent<AudioSource>();
            }
        }

        private int _cachedSampleRate;
        
        private void Awake()
        {
            _audioBuffer = new float[_bufferSize];
            _cachedSampleRate = AudioSettings.outputSampleRate;
            OnValidate();
        }

        /// <summary>
        /// Resets the internal clock and clears buffers. 
        /// Call this at the start of a new TTS sentence.
        /// </summary>
        public void ResetTime()
        {
            lock (_lock)
            {
                Interlocked.Exchange(ref _totalSamplesPlayed, 0);
                _readIndex = 0;
                _writeIndex = 0;
                _count = 0;
                Array.Clear(_audioBuffer, 0, _bufferSize);
            }
        }

        // This callback runs on the audio thread
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (channels < 1 || data == null) return;

            // Minimize lock scope
            lock (_lock)
            {
                // If buffer is empty, fast exit with silence
                if (_count <= 0)
                {
                    Array.Clear(data, 0, data.Length);
                    
                    // Feed silence to AEC as reference logic requires continuous stream usually
                    // (Handled by WebRTCAudioProcessor's own OnAudioFilterRead now)
                    return;
                }

                // Thread-safe logging counter
                if ((_writeIndex + _readIndex) % 100 == 0) // crude pseudo-random check or just use atomic if critical, but for debug write/read index sum is fine enough to throttle
                {
                     // actually let's just use a simple counter, strictly local to this method if possible, but method is called repeatedly. 
                     // We can't maintain state easily without a field.
                     // Let's use _readIndex as a proxy for time.
                }

                if (_readIndex % 48000 < 480) // Roughly once per second (assuming 48k buffer wrap)
                {
                     // Even simpler: check simple sparseness
                }
                
                // Let's use a static volatile or Interlocked if we really care, but for debug logging:
                // We'll just skip the Time.frameCount check and blindly check every N calls if we had a counter.
                // Since I can't easily add a field in replace_content without context of class start, 
                // I will just remove the framing check and use Interlocked.Increment on a new field or just remove it if it's too risky.
                // Actually, I can just use a random check or the buffer index.
                
                if (_totalSamplesPlayed % 1000 == 0) // _totalSamplesPlayed is atomic read
                {
                    float sum = 0;
                    for(int i=0; i<data.Length; i+=10) sum += data[i]*data[i]; 
                    if (sum > 0.001f) Debug.Log($"[SyncAudioSource] Has Audio Data. RMS approx: {Mathf.Sqrt(sum/(data.Length/10))}");
                }

                try
                {
                    // We need 'samplesNeeded' frames. Each frame has 'channels' samples.
                    // But our buffer is MONO (1 sample per frame).
                    // So we read 'samplesNeeded' from buffer, and duplicate to channels.
                    
                    int outputLen = data.Length;
                    int framesNeeded = outputLen / channels;
                    
                    // Read 'framesNeeded' from ring buffer
                    int framesRead = 0;
                    
                    while (framesRead < framesNeeded && _count > 0)
                    {
                        // How many contiguous from readIndex to end?
                        int contiguous = Mathf.Min(framesNeeded - framesRead, _bufferSize - _readIndex);
                        // Also limited by valid data count
                        contiguous = Mathf.Min(contiguous, _count);

                        // We have to iterate to interleave to channels unfortunately
                        // But we can optimize the read loop
                        if (channels == 1)
                        {
                            // Mono -> Mono: Direct Block Copy
                            Array.Copy(_audioBuffer, _readIndex, data, framesRead, contiguous);
                        }
                        else
                        {
                            // Mono -> Stereo/Surround: Loop copy (sadly necessary unless we alloc temp)
                            int targetIdx = framesRead * channels;
                            int sourceIdx = _readIndex;
                            for (int k = 0; k < contiguous; k++)
                            {
                                float val = _audioBuffer[sourceIdx + k];
                                for (int c = 0; c < channels; c++)
                                {
                                    data[targetIdx + c] = val;
                                }
                                targetIdx += channels;
                            }
                        }

                        _readIndex = (_readIndex + contiguous) % _bufferSize;
                        _count -= contiguous;
                        framesRead += contiguous;
                    }

                    // Fill remainder with silence if buffer ran dry
                    if (framesRead < framesNeeded)
                    {
                        Array.Clear(data, framesRead * channels, (framesNeeded - framesRead) * channels);
                    }
                    
                    // Interlocked update is safe inside lock too
                    if (framesRead > 0)
                    {
                        // _totalSamplesPlayed tracks FRAMES (time), not raw samples.
                        Interlocked.Add(ref _totalSamplesPlayed, framesRead);
                    }
                }
                catch (Exception e)
                {
                    // Emergency silence
                    Array.Clear(data, 0, data.Length);
                }
            }

            // --- SEND TO WEB RTC FOR AEC REFERENCE ---
            // (New architecture uses OnAudioFilterRead in WebRTCAudioProcessor directly, so no manual push needed here)
        }

        private void OnDestroy()
        {
            // Managed array will be GC'd. No dispose needed.
        }

        // --- PUBLIC API ---

        public void SampleCallback(float[] samples, int? count = null, int? inputSampleRate = null, int? outputSampleRate = null)
            => EnqueueManaged(samples, count ?? samples.Length); // Fire and forget (sync enqueue)

        // Keep this for compatibility but internally convert immediately
        public void SampleCallback(NativeArray<float> samples, int? count = null, int? inputSampleRate = null, int? outputSampleRate = null)
        {
            if (!samples.IsCreated) return;
            float[] temp = samples.ToArray();
            EnqueueManaged(temp, temp.Length);
        }

        private void EnqueueManaged(float[] samples, int count)
        {
            lock (_lock)
            {
                int samplesToWrite = Mathf.Min(count, _bufferSize - _count);
                if (samplesToWrite < count)
                {
                    Debug.LogWarning($"[SyncAudioSource] Overflow! Buffer full. Dropping {count - samplesToWrite} samples.");
                }
                
                int written = 0;
                while (written < samplesToWrite)
                {
                    int contiguous = Mathf.Min(samplesToWrite - written, _bufferSize - _writeIndex);
                    
                    Array.Copy(samples, written, _audioBuffer, _writeIndex, contiguous);
                    
                    _writeIndex = (_writeIndex + contiguous) % _bufferSize;
                    _count += contiguous;
                    written += contiguous;
                }
            }
        }

        [UsedImplicitly]
        public void ClearBuffer()
        {
            lock (_lock)
            {
                _readIndex = 0;
                _writeIndex = 0;
                _count = 0;
            }
        }
    }
}