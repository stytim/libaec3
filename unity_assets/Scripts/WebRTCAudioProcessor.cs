using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using AEC3;
using System.Text;

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
    public bool RecordDebugWav = false; // Toggle to record debug WAVs
    [Range(0, 1500)]
    public float CurrentEchoDelayMs = 0f;

    public static WebRTCAudioProcessor Instance { get; private set; }

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

        StartMicrophone();
    }

    // WAV Recording
    private FileStream _fsCap, _fsOut, _fsRef;
    private BinaryWriter _bwCap, _bwOut, _bwRef;
    private int _capSamplesWritten = 0;
    private int _outSamplesWritten = 0;
    private int _refSamplesWritten = 0;

    // Calibration
    private bool _isCalibrating = false;
    private List<float> _calibRefData = new List<float>();
    private List<float> _calibCapData = new List<float>();
    private int _calibNoiseDurationSamples = 0;
    private int _calibTotalDurationSamples = 0;
    private int _calibPlayHead = 0;
    private uint _lcgSeed = 12345;

    [ContextMenu("Start Auto-Calibration")]
    public void StartCalibration()
    {
        if (_isCalibrating) return;
        StartCoroutine(CalibrationRoutine());
    }

    private System.Collections.IEnumerator CalibrationRoutine()
    {
        Debug.Log("[AEC3] Starting Calibration...");
        _isCalibrating = true;
        _calibRefData.Clear();
        _calibCapData.Clear();
        _calibPlayHead = 0;
        
        // Ensure AudioSource is playing so OnAudioFilterRead runs
        var src = GetComponent<AudioSource>();
        bool wasPlaying = src.isPlaying;
        if (!wasPlaying)
        {
            src.loop = true; // Ensure it doesn't stop
            src.Play(); 
            Debug.Log("[AEC3] Forced AudioSource to Play for calibration...");
        }

        // Settings
        int noiseMs = 200;
        int totalMs = 1500; // 1.5s record window
        _calibNoiseDurationSamples = (TargetSampleRate * noiseMs) / 1000;
        _calibTotalDurationSamples = (TargetSampleRate * totalMs) / 1000;

        // Wait for recording to finish
        float waitTime = totalMs / 1000f + 0.5f; // + buffer
        yield return new WaitForSeconds(waitTime);

        _isCalibrating = false;
        
        // Restore AudioSource state
        if (!wasPlaying) src.Stop();

        // Calculate RMS for debugging
        double refRms = 0;
        foreach(var s in _calibRefData) refRms += s*s;
        refRms = Math.Sqrt(refRms / Math.Max(1, _calibRefData.Count));
        
        double capRms = 0;
        foreach(var s in _calibCapData) capRms += s*s;
        capRms = Math.Sqrt(capRms / Math.Max(1, _calibCapData.Count));

        Debug.Log($"[AEC3] Calibration Capture: Ref={_calibRefData.Count} (RMS={refRms:F3}), Cap={_calibCapData.Count} (RMS={capRms:F3})");

        // Analyze directly (might pause frame for ~100ms)
        int bestDelayMs = CalculateOptimalDelay(_calibRefData.ToArray(), _calibCapData.ToArray(), TargetSampleRate);
        
        if (bestDelayMs >= 0)
        {
            CurrentEchoDelayMs = bestDelayMs;
            Debug.Log($"[AEC3] Calibration Success! Applied Delay: {CurrentEchoDelayMs}ms");
        }
        else
        {
            Debug.LogError("[AEC3] Calibration Failed: Could not find reliable correlation peak.");
        }
    }

    private int CalculateOptimalDelay(float[] refData, float[] capData, int sampleRate)
    {
        if (refData.Length == 0 || capData.Length == 0) return -1;

        // Calculate Energy of Reference (to normalize)
        double refEnergy = 0;
        for(int i=0; i<refData.Length; i++) refEnergy += refData[i] * refData[i];
        
        // If Reference was silent, calibration is impossible
        if (refEnergy < 0.001) 
        {
            Debug.LogError("[AEC3] Calibration Failed: Reference Signal was silent.");
            return -1;
        }

        int maxLagSamples = (sampleRate * 1500) / 1000; // Search up to 1500ms
        float bestCorr = -1f;
        int bestLag = -1;

        // Cross-Correlation
        for (int lag = 0; lag < maxLagSamples; lag++)
        {
            int n = Math.Min(refData.Length, capData.Length - lag);
            if (n < 1000) break;

            double sum = 0;
            double capNrgy = 0;
            for (int i = 0; i < n; i++)
            {
                sum += refData[i] * capData[i + lag];
                capNrgy += capData[i + lag] * capData[i + lag];
            }
            
            // Normalized Correlation Coefficient: sum(xy) / sqrt(sum(x^2)*sum(y^2))
            // We use refEnergy as approximation for x^2 (ignoring end clipping for speed)
            // Ideally should re-calculate refEnergy for the specific window, but global refEnergy is fine for white noise.
            
            if (capNrgy > 0.0001)
            {
                float normCorr = (float)(sum / Math.Sqrt(refEnergy * capNrgy));
                
                if (Math.Abs(normCorr) > bestCorr)
                {
                    bestCorr = Math.Abs(normCorr);
                    bestLag = lag;
                }
            }
        }
        
        Debug.Log($"[AEC3] Best Correlation Peak: {bestCorr:F4} at Lag: {bestLag} samples");

        // Threshold: 0.1 is usually a weak correlation. 0.5+ is strong.
        if (bestCorr < 0.15f)
        {
             Debug.LogWarning("[AEC3] Calibration Signal too weak or noisy. Correlation < 0.15. Try increasing speaker volume or moving mic closer.");
             return -1;
        }

        if (bestLag >= 0)
        {
            int delayMs = (bestLag * 1000) / sampleRate;
            return delayMs;
        }
        return -1;
    }

    private void StartRecordWav()
    {
        try
        {
            string pathCap = Path.Combine(Application.dataPath, "..", "capture_debug.wav");
            string pathOut = Path.Combine(Application.dataPath, "..", "output_debug.wav");
            string pathRef = Path.Combine(Application.dataPath, "..", "reference_debug.wav");

            _fsCap = new FileStream(pathCap, FileMode.Create);
            _bwCap = new BinaryWriter(_fsCap);
            WriteWavHeader(_bwCap, TargetSampleRate, 1);

            _fsOut = new FileStream(pathOut, FileMode.Create);
            _bwOut = new BinaryWriter(_fsOut);
            WriteWavHeader(_bwOut, TargetSampleRate, 1);

            _fsRef = new FileStream(pathRef, FileMode.Create);
            _bwRef = new BinaryWriter(_fsRef);
            WriteWavHeader(_bwRef, TargetSampleRate, 1);

            Debug.Log($"[AEC3] Recording WAVs to: {pathCap}, {pathOut}, {pathRef}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[AEC3] Failed to start WAV recording: {e.Message}");
        }
    }

    private void WriteWavSamples(BinaryWriter bw, short[] samples, ref int count)
    {
        if (bw == null) return;
        for (int i = 0; i < samples.Length; i++)
        {
            bw.Write(samples[i]);
        }
        count += samples.Length;
    }

    private void StopRecordWav()
    {
        if (_bwCap != null) UpdateWavHeader(_bwCap, _fsCap, _capSamplesWritten);
        if (_bwCap != null) _bwCap.Close();
        if (_fsCap != null) _fsCap.Close();

        if (_bwOut != null) UpdateWavHeader(_bwOut, _fsOut, _outSamplesWritten);
        if (_bwOut != null) _bwOut.Close();
        if (_fsOut != null) _fsOut.Close();

        if (_bwRef != null) UpdateWavHeader(_bwRef, _fsRef, _refSamplesWritten);
        if (_bwRef != null) _bwRef.Close();
        if (_fsRef != null) _fsRef.Close();

        _bwCap = null; _fsCap = null;
        _bwOut = null; _fsOut = null;
        _bwRef = null; _fsRef = null;
    }

    private void WriteWavHeader(BinaryWriter bw, int sampleRate, int channels)
    {
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(0); // Placeholder for file size
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // Subchunk1Size
        bw.Write((short)1); // AudioFormat (PCM)
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2); // ByteRate
        bw.Write((short)(channels * 2)); // BlockAlign
        bw.Write((short)16); // BitsPerSample
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(0); // Placeholder for data size
    }

    private void UpdateWavHeader(BinaryWriter bw, FileStream fs, int sampleCount)
    {
        if (bw == null || fs == null) return;
        try
        {
            fs.Seek(4, SeekOrigin.Begin);
            bw.Write(36 + sampleCount * 2); // ChunkSize
            fs.Seek(40, SeekOrigin.Begin);
            bw.Write(sampleCount * 2); // Subchunk2Size
        }
        catch { }
    }

    private void OnDestroy()
    {
        StopRecordWav();

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

         _micDevice = Microphone.devices[0]; // Default to first device

        // --- NEW: Try to get selection from STTHandler ---
        STTHandler stt = FindFirstObjectByType<STTHandler>();
        if (stt != null && !string.IsNullOrEmpty(stt.selectedMicrophoneDeviceName))
        {
            string selected = stt.selectedMicrophoneDeviceName;
            bool found = false;
            foreach (var d in Microphone.devices)
            {
                if (d == selected)
                {
                    _micDevice = d;
                    found = true;
                    Debug.Log($"[AEC3] Using User-Selected Microphone: {_micDevice}");
                    break;
                }
            }

            if (!found)
            {
                Debug.LogWarning($"[AEC3] Selected microphone '{selected}' not found. Falling back to default: {_micDevice}");
            }
        }
        else
        {
            Debug.Log($"[AEC3] No specific microphone selected in STTHandler (or STTHandler not found). Using default: {_micDevice}");
        }
        // -------------------------------------------------

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
        
        Debug.Log($"[AEC3] Mic started: {_micDevice} @ {_micSampleRate}Hz (Requested: {requestRate}Hz)");
        Debug.Log($"[AEC3] Audio Settings: OutputRate={AudioSettings.outputSampleRate}Hz, TargetRate={TargetSampleRate}Hz");
        Debug.Log($"[AEC3] Buffer Ratio: Mic={_micSampleRate/(float)TargetSampleRate:F2}, Ref={AudioSettings.outputSampleRate/(float)TargetSampleRate:F2}");
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.C))
        {
            StartCalibration();
        }

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

    public bool DebugFilterRead = false;

    /// <summary>
    /// Called by Unity Audio Thread on the AudioSource playing the Reference Audio (TTS)
    /// </summary>
    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (DebugFilterRead) Debug.Log($"[AEC3] OnAudioFilterRead: {data.Length} samples, {channels} ch");

        // 'data' contains interleaved samples.
        // We need to extract a Mono mix for the Reference signal.
        
        int samples = data.Length / channels;
        float[] monoRef = new float[samples];
        
        if (_isCalibrating)
        {
            // Calibration Generation Mode
            for (int i = 0; i < samples; i++)
            {
                float signal = 0f;
                if (_calibPlayHead < _calibNoiseDurationSamples)
                {
                    // Generate White Noise (Simple LCG)
                    _lcgSeed = (_lcgSeed * 1664525 + 1013904223) & 0xFFFFFFFF;
                    float rand = (_lcgSeed / (float)uint.MaxValue) * 2.0f - 1.0f;
                    signal = rand * 0.5f; // 0.5 amplitude
                }
                
                // Advance Playhead
                _calibPlayHead++;
                if (_calibPlayHead > _calibTotalDurationSamples) signal = 0f;

                // Write to Audio Output (so it plays)
                for (int ch = 0; ch < channels; ch++) data[i * channels + ch] = signal;
                
                monoRef[i] = signal;
            }
        }
        else
        {
            // Normal Passthrough Mode
            for (int i = 0; i < samples; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    sum += data[i * channels + ch];
                }
                monoRef[i] = sum / channels; // Average mix
            }
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
        
        // Averaging Downsampler (Box Filter) to reduce aliasing
        // When downsampling (ratio > 1), we should average all samples that contribute to the output sample.
        
        while (_resampleIndexCap < input.Length)
        {
            // Determine the window in input space for this output sample
            float startPos = _resampleIndexCap;
            float endPos = startPos + ratio;
            
            // If end of window is beyond input, we must stop and wait for more data?
            // Or handle partial window?
            // For simplicity in streaming: if window crosses boundary, we stop and wait for next chunk.
            // But we need to maintain state.
            // Simplified approach: Integrate samples fully contained, and fractional parts.
            
            if (endPos > input.Length) break; // Not enough data for a full sample
            
            float sum = 0;
            float weightSum = 0;
            
            // Integrate from startPos to endPos
            int startIdx = (int)startPos;
            int endIdx = (int)endPos; // Exclusive integer index
            
            // 1. First partial sample
            if (startIdx < input.Length)
            {
                float frac = 1.0f - (startPos - startIdx);
                // If window is within single sample
                if (endIdx == startIdx) frac = endPos - startPos;
                
                sum += input[startIdx] * frac;
                weightSum += frac;
            }
            
            // 2. Full intermediate samples
            for (int i = startIdx + 1; i < endIdx; i++)
            {
                sum += input[i];
                weightSum += 1.0f;
            }
            
            // 3. Last partial sample (if window spans across)
            if (endIdx > startIdx && endIdx < input.Length)
            {
                float frac = endPos - endIdx;
                sum += input[endIdx] * frac;
                weightSum += frac;
            }
            
            float outputSample = (weightSum > 0.00001f) ? sum / weightSum : 0; // Average
            
            _captureBuffer.Add(outputSample * MicGain);
            
            if (_isCalibrating)
            {
                _calibCapData.Add(outputSample * MicGain);
            }
            
            _resampleIndexCap += ratio;
        }
        
        // Wrap index for next chunk
        _resampleIndexCap -= input.Length;
    }

    private void ProcessReferenceInput_Locked(float[] input, int inputRate)
    {
        float ratio = (float)inputRate / TargetSampleRate;
        if (ratio <= 0.001f) return; 

        for (; _resampleIndexRef < input.Length; _resampleIndexRef += ratio)
        {
            int idx = (int)_resampleIndexRef;
            float frac = _resampleIndexRef - idx;

            float val1 = input[idx];
            float val2 = (idx + 1 < input.Length) ? input[idx + 1] : val1;

            float sample = val1 * (1f - frac) + val2 * frac;
            _referenceBuffer.Add(sample);

            if (_isCalibrating)
            {
                _calibRefData.Add(sample);
            }
        }
        _resampleIndexRef -= input.Length;
    }

    /// <summary>
    /// Clears internal capture and reference buffers. 
    /// Useful for resetting state between tests to avoid desync.
    /// </summary>
    public void ClearBuffers()
    {
        lock (_referenceBuffer)
        {
            _referenceBuffer.Clear();
        }
        _captureBuffer.Clear();
        
        // Reset Resampler indices to avoid jumping
        _resampleIndexRef = 0;
        _resampleIndexCap = 0;
        
        int dropped = 0;
        while (OutputQueue.TryDequeue(out _)) { dropped++; }
        
        Debug.Log($"[AEC3] Buffers Cleared. Dropped {dropped} old output frames.");
    }

    private int _stallCounter = 0;
    
    private void ProcessAECFrames()
    {
        // Check if we have enough samples in BOTH buffers
        // We need lock for reference buffer
        int refCount;
        lock(_referenceBuffer) refCount = _referenceBuffer.Count;
        
        int capCount = _captureBuffer.Count;

        // processing logic: we need at least one frame in both
        if (capCount < _aecFrameSizeSamples || refCount < _aecFrameSizeSamples)
        {
            _stallCounter++;
            if (_stallCounter > 100) // ~1-2 seconds of stall at 60fps
            {
                if (_stallCounter % 100 == 0)
                   Debug.LogWarning($"[AEC3] Stall Warning: Cap: {capCount}, Ref: {refCount} (Need {_aecFrameSizeSamples}). Mic or AudioSource might be silent.");
            }
            return; 
        }
        else
        {
            _stallCounter = 0;
        }

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
            // Skip processing during calibration to avoid CPU load/side effects, 
            // but we MUST consume buffers to keep them flowing?
            // Actually, ProcessAECFrames removes from buffers. If we stop calling it, buffers grow indefinitely!
            // So we must continue to "consume" even if we don't AEC.
            
            if (_isCalibrating)
            {
                 // Consuming buffers is already done above (FloatToShort).
                 // Just don't run AEC.
                 // We don't need output during calibration (it's just "psst").
                 // But we should probably keep OutputQueue alive or just drop it.
                 // Let's just drop it.
                 continue; 
            }

            if (ProcessAEC && _aec != null)
            {
                int result = _aec.ProcessFrame(
                    _shortRefFrame, 
                    _shortCapFrame, 
                    _shortOutFrame, 
                    null, 
                    (int)(CurrentEchoDelayMs * TargetSampleRate / 1000)
                );
                
                if (result == 0)
                {
                    // Debug Logging: Check signal levels every 100 frames (~1 sec)
                    _debugFrameCount++;
                    /*
                    if (_debugFrameCount % 100 == 0)
                    {
                        float capRMS = CalculateRMS(_shortCapFrame);
                        float outRMS = CalculateRMS(_shortOutFrame);
                        float refRMS = CalculateRMS(_shortRefFrame);
                        Debug.Log($"[AEC3] RMS | Ref: {refRMS:F3} | Cap: {capRMS:F3} | Out: {outRMS:F3} | Queue: {OutputQueue.Count}");
                    }
                    */

                    // Write Debug WAVs if enabled
                    if (RecordDebugWav)
                    {
                        if (_bwCap == null) StartRecordWav(); // Just in case
                        WriteWavSamples(_bwCap, _shortCapFrame, ref _capSamplesWritten);
                        WriteWavSamples(_bwOut, _shortOutFrame, ref _outSamplesWritten);
                        WriteWavSamples(_bwRef, _shortRefFrame, ref _refSamplesWritten);
                    }

                    // Success, queue output
                    // Clone the array because _shortOutFrame is reused
                    short[] outCopy = new short[_aecFrameSizeSamples];
                    Array.Copy(_shortOutFrame, outCopy, _aecFrameSizeSamples);
                    OutputQueue.Enqueue(outCopy);
                }
                else
                {
                    // Log error but do NOT fallback (as requested)
                    Debug.LogError($"[AEC3] ProcessFrame failed with error code: {result}");
                }
            }
            else
            {
                // Passthrough (Bypass AEC)
                short[] outCopy = new short[_aecFrameSizeSamples];
                Array.Copy(_shortCapFrame, outCopy, _aecFrameSizeSamples);
                OutputQueue.Enqueue(outCopy);
            }
        }
    }

    private int _debugFrameCount = 0;

    private float CalculateRMS(short[] samples)
    {
        if (samples == null || samples.Length == 0) return 0f;
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sum += samples[i] * samples[i];
        }
        return (float)Math.Sqrt(sum / samples.Length);
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
