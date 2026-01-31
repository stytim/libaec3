using UnityEngine;
using System.Collections.Concurrent;
using System.IO;

[RequireComponent(typeof(WebRTCAudioProcessor))]
public class AECTestRecorder : MonoBehaviour
{
    public bool IsRecording = false;
    
    private WavWriter _refWriter;
    private WavWriter _capWriter;
    private WavWriter _outWriter;
    
    // Hooks to receive data from processor
    // We will need to modify WebRTCAudioProcessor to call these if this component exists
    
    private string _path;

    private void Start()
    {
        _path = Application.persistentDataPath;
        Debug.Log($"[AECTestRecorder] Save path: {_path}");
    }

    public void StartRecording()
    {
        if (IsRecording) return;
        
        _refWriter = new WavWriter(Path.Combine(_path, "aec_debug_ref.wav"), 16000, 1);
        _capWriter = new WavWriter(Path.Combine(_path, "aec_debug_cap.wav"), 16000, 1);
        _outWriter = new WavWriter(Path.Combine(_path, "aec_debug_out.wav"), 16000, 1);
        
        IsRecording = true;
        Debug.Log("[AECTestRecorder] Recording started...");
    }

    public void StopRecording()
    {
        if (!IsRecording) return;
        
        IsRecording = false;
        
        _refWriter.Close();
        _capWriter.Close();
        _outWriter.Close();
        
        Debug.Log("[AECTestRecorder] Recording saved.");
    }

    // Called by WebRTCAudioProcessor
    public void OnAECFrameProcessed(short[] refFrame, short[] capFrame, short[] outFrame)
    {
        if (!IsRecording) return;
        
        lock(this) 
        {
            if (_refWriter != null) _refWriter.WriteSamples(refFrame);
            if (_capWriter != null) _capWriter.WriteSamples(capFrame);
            if (_outWriter != null) _outWriter.WriteSamples(outFrame);
        }
    }
    
    private void OnDestroy() { StopRecording(); }
    
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))
        {
            if (IsRecording) StopRecording();
            else StartRecording();
        }
    }
}
