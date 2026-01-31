using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using AEC3;

[RequireComponent(typeof(AudioSource))]
public class WebRTCAudioProcessor : MonoBehaviour
{
    [Header("Configuration")]
    [Tooltip("Target Sample Rate for AEC and STT (default 16000)")]
    public int TargetSampleRate = 16000;
    
    [Tooltip("Gain applied to microphone input")]
    [Range(0f, 10f)]
    public float MicGain = 1.0f;

    [Header("Debug")]
    public bool ProcessAEC = true;
    public float CurrentEchoDelayMs = 0f;

    public static WebRTCAudioProcessor Instance { get; private set; }

    // Debug Hook
    private AECTestRecorder _debugger;
    private int _debugFrameCount = 0;

    // AEC3 Core
    private AEC3Processor _aec;
    private const int AEC_FRAME_SIZE_MS = 10;
    private int _aecFrameSizeSamples; // e.g. 160 for 16kHz
    
    // Buffers
    // using float buffers for handling accumulated samples before batch processing
    private List<float> _referenceBuffer; // Data from TTS/Game Audio (at 16k)
    private List<float> _captureBuffer;   // Data from Mic (at 16k)
    
    // Raw buffers for the Native AEC call (Int16)
    private short[] _shortRefFrame;
    private short[] _shortCapFrame;
    private short[] _shortOutFrame;

    // Microphone Handling
    private string _micDevice;
    private CheckMicrophone _micRoutine;
    private AudioClip _micClip;
    private int _micLastPos;
    private int _micSampleRate;

    // Resampling state
    private float _resampleIndexRef = 0f;
    private float _resampleIndexCap = 0f;
    
    private int _outputSampleRate;

    // Output for STT
    // Use a thread-safe queue if STT consumes from a different thread
    public ConcurrentQueue<short[]> OutputQueue = new ConcurrentQueue<short[]>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        _referenceBuffer = new List<float>(4096);
        _captureBuffer = new List<float>(4096);

        _aecFrameSizeSamples = (TargetSampleRate * AEC_FRAME_SIZE_MS) / 1000;
        
        // Initialize AEC3
        // 1 channel (Mono), No Linear Export
        try 
        {
            _aec = new AEC3Processor(TargetSampleRate, 1, false);
            Debug.Log($"[AEC3] Initialized at {TargetSampleRate}Hz");
        }
        catch (Exception e)
        {
            Debug.LogError($"[AEC3] Failed to initialize: {e.Message}");
            enabled = false;
            return;
        }

        _shortRefFrame = new short[_aecFrameSizeSamples];
        _shortCapFrame = new short[_aecFrameSizeSamples];
        _shortOutFrame = new short[_aecFrameSizeSamples];

        _shortCapFrame = new short[_aecFrameSizeSamples];
        _shortOutFrame = new short[_aecFrameSizeSamples];
        
        _outputSampleRate = AudioSettings.outputSampleRate;

        _debugger = GetComponent<AECTestRecorder>();
        StartMicrophone();
    }

    private void OnDestroy()
    {
        if (_aec != null)
        {
            _aec.Dispose();
            _aec = null;
        }
        Microphone.End(_micDevice);
    }

    private void StartMicrophone()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("[AEC3] No microphone devices found!");
            return;
        }

        _micDevice = Microphone.devices[0]; // Use default
        // Request 16kHz if possible, otherwise system default
        int minFreq, maxFreq;
        Microphone.GetDeviceCaps(_micDevice, out minFreq, out maxFreq);
        
        // Prefer TargetRate if supported, else max
        // Note: On many platforms, asking for 16k might give 16k, or 48k. 
        // We will adapt to whatever we get.
        int requestRate = TargetSampleRate;
        if (maxFreq > 0 && requestRate > maxFreq) requestRate = maxFreq;
        if (minFreq > 0 && requestRate < minFreq) requestRate = minFreq;

        _micClip = Microphone.Start(_micDevice, true, 10, requestRate);
        _micSampleRate = _micClip.frequency;
        _micLastPos = 0;
        
        Debug.Log($"[AEC3] Mic started: {_micDevice} @ {_micSampleRate}Hz");
    }

    private void Update()
    {
        if (_micClip == null) return;

        // 1. Read Microphone Data
        int pos = Microphone.GetPosition(_micDevice);
        if (pos < 0 || _micLastPos == pos) return;

        int diff = pos - _micLastPos;
        if (diff < 0) diff += _micClip.samples; // Wrapped around

        if (diff > 0)
        {
            float[] micData = new float[diff * _micClip.channels];
            _micClip.GetData(micData, _micLastPos);
            
            // Assume Mic is Mono or we take first channel
            // If Mic is Stereo, we might need to stride. 
            // Most headset mics are Mono. If Stereo, just taking Ch0 is usually fine for voice.
            // Let's ensure we only take Ch0 if multi-channel.
            if (_micClip.channels > 1)
            {
                float[] monoData = new float[diff];
                for(int i=0; i<diff; i++) monoData[i] = micData[i * _micClip.channels];
                micData = monoData;
            }

            // Downsample and Add to Buffer
            ProcessCaptureInput(micData, _micSampleRate);
            
            _micLastPos = pos;
        }

        // 2. Process AEC if we have enough data
        ProcessAECFrames();
    }

    /// <summary>
    /// Called by Unity Audio Thread on the AudioSource playing the Reference Audio (TTS)
    /// </summary>
    private void OnAudioFilterRead(float[] data, int channels)
    {
        // 'data' contains interleaved samples.
        // We need to extract a Mono mix for the Reference signal.
        
        int samples = data.Length / channels;
        float[] monoRef = new float[samples];

        for (int i = 0; i < samples; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                sum += data[i * channels + ch];
            }
            monoRef[i] = sum / channels; // Average mix
        }

        // Send to buffering logic (Thread-Safe needed?)
        // OnAudioFilterRead runs on a separate thread. Update runs on Main.
        // List<T> is NOT thread safe. We need a lock.
        lock (_referenceBuffer)
        {
            // We assume AudioSettings.outputSampleRate is the rate here (e.g. 44100 or 48000)
            int sourceRate = _outputSampleRate;
            ProcessReferenceInput_Locked(monoRef, sourceRate);
        }
    }

    // --- Resampling & buffering Logic ---

    // Adds Resampled Audio to _captureBuffer
    private void ProcessCaptureInput(float[] input, int inputRate)
    {
        // Simple Linear Resampler: InputRate -> TargetSampleRate
        float ratio = (float)inputRate / TargetSampleRate;
        
        // We want to generate 'Target' samples from 'Input' samples
        // But here we are push-based.
        // Actually, better to iterate over output required? No, we have input chunk.
        
        // Iterate over Input and map to Output? 
        // No, standard way: 
        // while (index < input.Length) { output.Add(Interpolate(input, index)); index += ratio; }
        
        // Reset index relative to new chunk? No, keep phase.
        // _resampleIndexCap is the position in the *current logical stream*.
        // But we receive chunks.
        // Let's track "position in the current chunk".
        
        // If ratio is 3.0 (48k->16k), we take sample 0, 3, 6...
        // _resampleIndexCap tracks the "next sample index to read from input"
        
        int outSamplesEstimate = (int)(input.Length / ratio) + 1;
        
        for (; _resampleIndexCap < input.Length; _resampleIndexCap += ratio)
        {
            // Perform Linear Interpolation
            int idx = (int)_resampleIndexCap;
            float frac = _resampleIndexCap - idx;
            
            float val1 = input[idx];
            float val2 = (idx + 1 < input.Length) ? input[idx + 1] : val1; // Clamp end
            
            float sample = val1 * (1f - frac) + val2 * frac;
            _captureBuffer.Add(sample * MicGain); // Apply Gain here
        }
        
        // Wrap index for next chunk
        _resampleIndexCap -= input.Length;
    }

    private void ProcessReferenceInput_Locked(float[] input, int inputRate)
    {
        float ratio = (float)inputRate / TargetSampleRate;

        for (; _resampleIndexRef < input.Length; _resampleIndexRef += ratio)
        {
            int idx = (int)_resampleIndexRef;
            float frac = _resampleIndexRef - idx;
            
            float val1 = input[idx];
            float val2 = (idx + 1 < input.Length) ? input[idx + 1] : val1;
            
            float sample = val1 * (1f - frac) + val2 * frac;
            _referenceBuffer.Add(sample);
        }
        _resampleIndexRef -= input.Length;
    }

    private void ProcessAECFrames()
    {
        // Check if we have enough samples in BOTH buffers
        // We need lock for reference buffer
        int refCount;
        lock(_referenceBuffer) refCount = _referenceBuffer.Count;
        
        int capCount = _captureBuffer.Count;

        // processing logic: we need at least one frame in both
        while (capCount >= _aecFrameSizeSamples && refCount >= _aecFrameSizeSamples)
        {
            // Extract Reference Frame
            float[] refChunk = new float[_aecFrameSizeSamples];
            lock (_referenceBuffer)
            {
                _referenceBuffer.CopyTo(0, refChunk, 0, _aecFrameSizeSamples);
                _referenceBuffer.RemoveRange(0, _aecFrameSizeSamples);
                refCount = _referenceBuffer.Count; // Update count
            }

            // Extract Capture Frame
            float[] capChunk = new float[_aecFrameSizeSamples];
            _captureBuffer.CopyTo(0, capChunk, 0, _aecFrameSizeSamples);
            _captureBuffer.RemoveRange(0, _aecFrameSizeSamples);
            capCount = _captureBuffer.Count;

            // Convert to Short
            FloatToShort(refChunk, _shortRefFrame);
            FloatToShort(capChunk, _shortCapFrame);

            // Process AEC
            if (ProcessAEC && _aec != null)
            {
                bool result = _aec.ProcessFrame(
                    _shortRefFrame, 
                    _shortCapFrame, 
                    _shortOutFrame, 
                    null, 
                    (int)(CurrentEchoDelayMs * TargetSampleRate / 1000)
                );
                
                if (result)
                {
                    // Success, queue output
                    // Clone the array because _shortOutFrame is reused
                    short[] outCopy = new short[_aecFrameSizeSamples];
                    Array.Copy(_shortOutFrame, outCopy, _aecFrameSizeSamples);
                    OutputQueue.Enqueue(outCopy);
                }
            }
                // Passthrough (Bypass AEC)
                short[] outCopy = new short[_aecFrameSizeSamples];
                Array.Copy(_shortCapFrame, outCopy, _aecFrameSizeSamples);
                OutputQueue.Enqueue(outCopy);
            }
            
            // Debugging
            if (_debugger != null && _debugger.IsRecording)
            {
                 // Pass copies or ensure writer handles them sync
                 // Since they are written immediately to file in lock, passing reference is OK if writer copies, 
                 // but writer writes directly. Let's trust the sync execution in Update.
                 _debugger.OnAECFrameProcessed(_shortRefFrame, _shortCapFrame, _shortOutFrame);
            }
            
            // RMS Logging (every ~1 sec)
            _debugFrameCount++;
            if (_debugFrameCount % 100 == 0)
            {
                 float rmsRef = CalculateRMS(_shortRefFrame);
                 float rmsCap = CalculateRMS(_shortCapFrame);
                 // Debug.Log($"[AEC Stats] Ref Vol: {rmsRef:F3}, Cap Vol: {rmsCap:F3}");
            }
        }
    }

    private float CalculateRMS(short[] samples)
    {
        double sum = 0;
        for(int i=0; i<samples.Length; i++) sum += samples[i]*samples[i];
        return (float)Math.Sqrt(sum/samples.Length) / 32768f;
    }

    private void FloatToShort(float[] input, short[] output)
    {
        for (int i = 0; i < input.Length; i++)
        {
            float v = input[i] * 32768f;
            if (v > 32767f) v = 32767f;
            if (v < -32768f) v = -32768f;
            output[i] = (short)v;
        }
    }

    // Helper class for coroutine if needed, but Update is fine
    private class CheckMicrophone { } 
}
