using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using System;
using System.Text;

[RequireComponent(typeof(AudioSource))]
public class AECTestRunner : MonoBehaviour
{
    [Header("Test Configuration")]
    public string ReferenceFileName = "ref.wav"; // In StreamingAssets
    public int StartDelayMs = 0;
    public int EndDelayMs = 1000;
    public int StepMs = 50;
    public int TestDurationSec = 5;

    [Header("Status")]
    public bool IsRunning = false;
    public string CurrentStatus = "Idle";
    
    private AudioSource _audioSource;
    private WebRTCAudioProcessor _aecProcessor;
    private List<short> _capturedAudio = new List<short>();

    private void Start()
    {
        _audioSource = GetComponent<AudioSource>();
        _aecProcessor = WebRTCAudioProcessor.Instance;
        
        if (_aecProcessor == null)
        {
            Debug.LogError("[AECTest] WebRTCAudioProcessor instance not found!");
            enabled = false;
        }
    }

    [ContextMenu("Run Test Suite")]
    public void RunTestSuite()
    {
        if (IsRunning) return;
        StartCoroutine(TestRoutine());
    }

    private IEnumerator TestRoutine()
    {
        IsRunning = true;
        CurrentStatus = "Loading Reference Audio...";
        
        // 1. Load Audio
        string path = Path.Combine(Application.streamingAssetsPath, ReferenceFileName);
        // Handle Android/Web differs, but for Windows local file is fine with file:// or just path if Request
        // UnityWebRequest needs file:// protocol on Windows for absolute paths sometimes, but StreamingAssetsPath usually works directly or needs protocol.
        // Best cross-platform way for StreamingAssets:
        
        string url = path;
        if (!path.Contains("://")) url = "file://" + path;

        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.WAV))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[AECTest] Failed to load {url}: {www.error}");
                IsRunning = false;
                yield break;
            }

            _audioSource.clip = DownloadHandlerAudioClip.GetContent(www);
            Debug.Log($"[AECTest] Loaded {ReferenceFileName}: {_audioSource.clip.length}s");
        }

        // 2. Prepare Loop
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string resultsDir = Path.Combine(Application.dataPath, "..", "AECTestResults_" + timestamp);
        Directory.CreateDirectory(resultsDir);
        
        StringBuilder report = new StringBuilder();
        report.AppendLine("Delay(ms),RMS,Filename");

        for (int delay = StartDelayMs; delay <= EndDelayMs; delay += StepMs)
        {
            CurrentStatus = $"Testing Delay: {delay}ms";
            Debug.Log($"[AECTest] >>> Starting Test for Delay {delay}ms");

            var aec = WebRTCAudioProcessor.Instance; // Get fresh reference
            if (aec == null) { Debug.LogError("[AECTest] AEC lost!"); break; }

            // Configure AEC
            aec.CurrentEchoDelayMs = delay;
            aec.DebugFilterRead = false; // Disable filter logging
            
            // Clear previous queues/buffers
            aec.ClearBuffers();
            _capturedAudio.Clear();

            // Play
            _audioSource.loop = true; 
            _audioSource.Play();
            Debug.Log($"[AECTest] AudioSource Playing: {_audioSource.isPlaying}, Clip: {_audioSource.clip?.name}, Time: {_audioSource.time}");
            
            // Record loop
            float endTime = Time.time + TestDurationSec;
            int chunksCollected = 0;
            
            while (Time.time < endTime)
            {
                // Dequeue processed audio
                // Use Instance directly to be absolutely sure
                var q = WebRTCAudioProcessor.Instance.OutputQueue; 
                if (q != null)
                {
                    while (q.TryDequeue(out short[] chunk))
                    {
                        if (chunk != null && chunk.Length > 0)
                        {
                            _capturedAudio.AddRange(chunk);
                            chunksCollected++;
                        }
                    }
                }
                yield return null;
            }

            // Stop
            _audioSource.Stop();
            
            // Wait a bit for tail
            yield return new WaitForSeconds(0.5f);
             
            // Flush remaining
            var qFlush = WebRTCAudioProcessor.Instance.OutputQueue;
            while (qFlush != null && qFlush.TryDequeue(out short[] chunk))
            {
                _capturedAudio.AddRange(chunk);
                chunksCollected++;
            }

            // Analyze
            float rms = CalculateRMS(_capturedAudio);
            string wavName = $"Delay_{delay}ms_RMS_{rms:F4}.wav";
            string wavPath = Path.Combine(resultsDir, wavName);
            
            SaveWav(wavPath, _capturedAudio.ToArray(), 16000); 
            
            Debug.Log($"[AECTest] Delay {delay}ms -> Captured {_capturedAudio.Count} samples ({chunksCollected} chunks). RMS: {rms:F4}");
            report.AppendLine($"{delay},{rms:F4},{wavName}");
            
            yield return new WaitForSeconds(1.0f);
        }

        // Save Report
        File.WriteAllText(Path.Combine(resultsDir, "Report.csv"), report.ToString());
        
        CurrentStatus = "Complete";
        IsRunning = false;
        Debug.Log($"[AECTest] Test Suite Complete. Results in {resultsDir}");
    }

    private float CalculateRMS(List<short> audio)
    {
        if (audio.Count == 0) return 0;
        double sum = 0;
        foreach (var s in audio) sum += s * s;
        return (float)Math.Sqrt(sum / audio.Count) / 32768f; // Normalized 0-1
    }

    private void SaveWav(string path, short[] data, int sampleRate)
    {
        using (FileStream fs = new FileStream(path, FileMode.Create))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + data.Length * 2);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1); // Mono
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(data.Length * 2);

            foreach (var s in data) bw.Write(s);
        }
    }
}
