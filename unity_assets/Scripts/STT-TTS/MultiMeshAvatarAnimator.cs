using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Utilities.Audio;
using App.Audio; // Add this namespace

public class MultiMeshAvatarAnimator : MonoBehaviour
{
    [Header("References")]
    public A2FClient client;
    // public StreamAudioSource streamSource;
    public SyncAudioSource streamSource;
    public SkinnedMeshRenderer[] targetMeshes;

    [Header("Configuration")]
    public float weightMultiplier = 100.0f;

    [Header("Sync Settings")]
    public bool useAudioSync = true;

    [Header("Debug")]
    public bool showDebugGUI = true;

    // --- SYNC DATA ---
    private struct AnimationFrame
    {
        public float time;
        public Dictionary<string, float> weights;
    }
    private List<AnimationFrame> _frameBuffer = new List<AnimationFrame>();

    // The timestamp of the very first frame received from A2F for the current sentence
    private float _firstFrameA2FTime = -1f;

    // The Audio Clock time when that first frame arrived
    private float _audioTimeAtFirstFrame = -1f;

    // --- CACHING ---
    private Dictionary<string, List<BlendShapeTarget>> _globalBlendShapeMap;
    private struct BlendShapeTarget { public SkinnedMeshRenderer mesh; public int index; }

    // --- LIVE DEBUGGING VARS ---
    private float _currentAudioTime;
    private float _currentAnimTime;
    private float _currentDelta; // (AudioTime - AnimTime)
    private float _avgDelta;
    private int _frameCount;

    void Start()
    {
        BuildBlendShapeMap();

        if (client != null) client.OnAnimationReceived += OnFrameReceived;
        if (streamSource == null) streamSource = GetComponent<SyncAudioSource>();
        if (streamSource == null && client != null) streamSource = client.GetComponent<SyncAudioSource>();
    }

    public void ResetSession()
    {
        _frameBuffer.Clear();
        _firstFrameA2FTime = -1f;
        _audioTimeAtFirstFrame = -1f;
        _avgDelta = 0f;
        _frameCount = 0;
        Debug.Log("[Animator] Session Reset.");
    }

    private void OnFrameReceived(float rawTimeStamp, Dictionary<string, float> weights)
    {
        // Debug.Log($"[Animator] RAW Frame: {rawTimeStamp:F3}"); // Too verbose?
        if (useAudioSync)
        {
            float currentAudioClock = (streamSource != null) ? streamSource.PlaybackTime : 0f;

            // 1. Initialize Zero Point on first frame
            if (_firstFrameA2FTime < 0)
            {
                _firstFrameA2FTime = rawTimeStamp;
                _audioTimeAtFirstFrame = currentAudioClock;

                // Log the initial latency hit
                Debug.Log($"[Sync Start] First Frame Arrived! \n" +
                          $"A2F Timestamp: {rawTimeStamp:F3}s \n" +
                          $"Audio Clock is already at: {currentAudioClock:F3}s \n" +
                          $"LATENCY OFFSET NEEDED: {currentAudioClock:F3}s");
            }
            else
            {
                // Sanity Check: If we receive a frame that is OLDER than our start time, something is wrong (stale data from previous run?)
                if (rawTimeStamp < _firstFrameA2FTime)
                {
                    Debug.LogError($"[Sync Error] Received timestamp {rawTimeStamp:F3} which is OLDER than start time {_firstFrameA2FTime:F3}. Stale packet?");
                }
            }

            // 2. Normalize A2F time to start at 0.0
            float adjustedTime = rawTimeStamp - _firstFrameA2FTime;

            // Debug.Log($"[Animator] Frame Added. AdjTime: {adjustedTime:F3} (Raw: {rawTimeStamp:F3})");

            _frameBuffer.Add(new AnimationFrame { time = adjustedTime, weights = weights });

            // 3. Cleanup old frames to save memory
            if (_frameBuffer.Count > 0 && _frameBuffer[0].time < (currentAudioClock - _audioTimeAtFirstFrame - 2.0f))
            {
                _frameBuffer.RemoveAt(0);
            }
        }
        else
        {
            ApplyInterpolatedFrame(weights, weights, 0f);
        }
    }

    void Update()
    {
        if (useAudioSync) ProcessSyncLogic();
    }

    private void ProcessSyncLogic()
    {
        if (_frameBuffer.Count == 0) return;
        if (streamSource == null) return;

        // --- 1. CALCULATE TIME ---
        // We want the animation time to align with the audio time.
        // However, the audio started playing BEFORE the first frame arrived.
        // So we must subtract that initial latency from the current audio clock.

        float rawAudioTime = streamSource.PlaybackTime;

        // This is the "Corrected" time that matches the 0.0 start of the animation
        float targetAnimTime = rawAudioTime - _audioTimeAtFirstFrame;

        // DEBUG LOGGING
        _currentAudioTime = rawAudioTime;
        _currentAnimTime = targetAnimTime;

        // --- 2. FETCH FRAMES ---
        AnimationFrame frameA = _frameBuffer.LastOrDefault(f => f.time <= targetAnimTime);
        AnimationFrame frameB = _frameBuffer.FirstOrDefault(f => f.time > targetAnimTime);

        // Debug.Log($"[Sync Logic] Audio: {rawAudioTime:F3} -> TargetAnim: {targetAnimTime:F3} | Buffer: [{_frameBuffer[0].time:F3} ... {_frameBuffer.Last().time:F3}] ({_frameBuffer.Count})");

        // --- 3. INTERPOLATE ---
        if (frameA.weights == null) frameA = _frameBuffer.FirstOrDefault();
        if (frameB.weights == null) frameB = frameA;

        float t = 0f;
        if (frameB.time > frameA.time)
        {
            t = (targetAnimTime - frameA.time) / (frameB.time - frameA.time);
        }

        // Calculate drift for debug (How far is the target from the closest available frame?)
        _currentDelta = targetAnimTime - frameA.time;

        ApplyInterpolatedFrame(frameA.weights, frameB.weights, t);
    }

    private void ApplyInterpolatedFrame(Dictionary<string, float> weightsA, Dictionary<string, float> weightsB, float t)
    {
        foreach (var kvp in weightsA)
        {
            if (_globalBlendShapeMap.TryGetValue(kvp.Key.ToLowerInvariant(), out List<BlendShapeTarget> targets))
            {
                float valA = kvp.Value;
                float valB = weightsB.ContainsKey(kvp.Key) ? weightsB[kvp.Key] : valA;
                float finalWeight = Mathf.Lerp(valA, valB, t) * weightMultiplier;

                for (int i = 0; i < targets.Count; i++)
                    targets[i].mesh.SetBlendShapeWeight(targets[i].index, finalWeight);
            }
        }
    }

    private void BuildBlendShapeMap()
    {
        _globalBlendShapeMap = new Dictionary<string, List<BlendShapeTarget>>();
        foreach (var meshRenderer in targetMeshes)
        {
            if (meshRenderer == null) continue;
            Mesh m = meshRenderer.sharedMesh;
            for (int i = 0; i < m.blendShapeCount; i++)
            {
                string cleanName = m.GetBlendShapeName(i).ToLowerInvariant();
                if (cleanName.Contains(".")) cleanName = cleanName.Split('.').Last();
                if (!_globalBlendShapeMap.ContainsKey(cleanName))
                    _globalBlendShapeMap[cleanName] = new List<BlendShapeTarget>();
                _globalBlendShapeMap[cleanName].Add(new BlendShapeTarget { mesh = meshRenderer, index = i });
            }
        }
    }

    private void OnGUI()
    {
        if (!showDebugGUI) return;

        GUI.skin.label.fontSize = 20;
        GUILayout.BeginArea(new Rect(10, 10, 600, 300), "A2F Sync Debugger", GUI.skin.window);

        GUILayout.Label($"Audio Clock (Raw): {_currentAudioTime:F3}s");
        GUILayout.Label($"Latency Offset: {_audioTimeAtFirstFrame:F3}s (Subtracted)");
        GUILayout.Label($"Target Anim Time: {_currentAnimTime:F3}s");

        GUI.color = (_currentDelta > 0.1f) ? Color.red : Color.green;
        GUILayout.Label($"Frame Buffer Delta: {_currentDelta:F4}s");
        GUILayout.Label(_currentDelta > 0.1f ? "LAGGING / BUFFER EMPTY" : "SYNCED");
        GUI.color = Color.white;

        if (_frameBuffer.Count > 0)
        {
            GUILayout.Label($"Buffered Range: {_frameBuffer[0].time:F2}s -> {_frameBuffer.Last().time:F2}s");
            GUILayout.Label($"Buffer Count: {_frameBuffer.Count}");
        }

        GUILayout.EndArea();
    }
}