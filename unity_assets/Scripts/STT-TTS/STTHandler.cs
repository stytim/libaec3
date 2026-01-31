using NativeWebSocket;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// --- Data Structures for JSON communication ---
// Based on messages observed in the python script

[Serializable]
public class ControlCommand
{
    public string command;
    public string parameter;
    public object value; // Can be string, int, float, bool etc.
    public int request_id = -1; // Optional for get_parameter
    public string method;
    public List<object> args;
    public Dictionary<string, object> kwargs;
}

[Serializable]
public class ServerControlResponse
{
    public string status; // "success" or "error"
    public string message; // Error message
    public string parameter;
    public object value;
    public int request_id = -1;
}

[Serializable]
public class ServerDataMessage
{
    public string type; // "realtime", "fullSentence", "recording_start", etc.
    public string text;
    public string audio_bytes_base64; // For transcription_start
    public string language; // Language of the transcription
    // Add other fields as needed based on server messages
}

[Serializable]
public class AudioMetadata
{
    public int sampleRate;
    // Add other metadata fields if your server sends/needs them
    // e.g., public long server_sent_to_stt;
    // public string server_sent_to_stt_formatted;
}


public class STTHandler : MonoBehaviour
{
    [Header("LLM")]
    public LLMHandler llmhandler;
    public bool isMICCAI = false; // Flag for MICCAI IVS demo
    public string testString = "";
    public bool isChinese = false;

    [Header("Connection Settings")]
    public List<string> serverIPs = new List<string> { "127.0.0.1", "10.23.0.207" }; // Default IPs
    public int selectedIPIndex = 0; // Index for the list above
    public string controlPort = "8011"; // Control port
    public string dataPort = "8012"; // Data port
    private string controlUrl = "ws://127.0.0.1:8011"; //
    private string dataUrl = "ws://127.0.0.1:8012"; //

    [Header("Audio Settings")]
    public int recordingFrequency = 16000; // Target frequency
    public int recordingLengthSec = 1; // How often to process audio chunks

    // This will be set by our custom editor's dropdown
    [HideInInspector] // Hide this from the default inspector, as we'll draw it with a dropdown
    public string selectedMicrophoneDeviceName = "";

    // We can keep this to show the currently selected device in the inspector for feedback
    [Tooltip("The microphone device that will be used (selected via dropdown).")]
    [SerializeField] // Show in inspector but not editable directly if we prefer
    private string activeMicrophoneDeviceForInfo = "";

    [Header("Status")]
    [ReadOnly] public bool isControlConnected = false;
    [ReadOnly] public bool isDataConnected = false;
    [ReadOnly] public bool isRecording = false;
    [ReadOnly] public string lastError = "";
    [ReadOnly] public bool lastServerCheckResult = false; // Store result of sanity check
    [ReadOnly] public bool isCheckingServer = false; // Flag to prevent spamming checks
    public Image statusIndicator; // UI element to show status

    [Header("Mute State")]
    [ReadOnly] public bool isMuted = true;

    [Header("TTS State")]
    [ReadOnly] public bool isTTSPlaying = false; // To track if TTS is active on Unity's side
    public TMP_Text transcriptionText; // Text field to show transcriptions in Unity UI
    public TMP_Text subtitle;
    public float displayTime = 3f; // How long the subtitle stays fully visible
    public float fadeOutDuration = 0.5f;

    private Coroutine _activeFadeCoroutine;

    [Header("Events")]
    public UnityEngine.Events.UnityEvent OnConnected;
    public UnityEngine.Events.UnityEvent OnDisconnected;

    [System.Serializable]
    public class StringEvent : UnityEngine.Events.UnityEvent<string> { }
    [System.Serializable]
    public class TranscriptionEvent : UnityEngine.Events.UnityEvent<string, string> { }
    public StringEvent OnErrorReceived;
    public StringEvent OnRealtimeTranscriptionUpdate;
    public TranscriptionEvent OnFinalTranscription;
    public UnityEngine.Events.UnityEvent OnRecordingStarted; // Server confirmed
    public UnityEngine.Events.UnityEvent OnRecordingStopped; // Server confirmed

    // -- ADD THESE TWO LINES --
    [Header("VAD Events")]
    public UnityEngine.Events.UnityEvent OnSpeechStartDetected; // Triggers when the user starts speaking
    public UnityEngine.Events.UnityEvent OnSpeechStopDetected;  // Triggers when the user stops speaking

    public class ServerCheckResultEvent : UnityEngine.Events.UnityEvent<bool> { }
    public ServerCheckResultEvent OnServerCheckCompleted; // Event to signal check result

    public UnityEngine.Events.UnityEvent OnInterruptionDetected; // Event for interruption

    // Add more specific events based on ServerDataMessage types if needed (VAD, WakeWord etc.)

    private WebSocket controlWs;
    private WebSocket dataWs;

    private AudioClip recordingClip;
    private string microphoneDevice;
    // private float[] audioBuffer; // Removed
    // private int lastAudioSamplePosition = 0; // Removed
    // private bool wantsToRecord = false;
    private bool wantsToConnect = false;

    private string userRole = "";

    // For GetParameter responses
    private Dictionary<int, TaskCompletionSource<object>> pendingParameterRequests = new Dictionary<int, TaskCompletionSource<object>>();
    private int requestCounter = 0;

    // --- Unity Lifecycle Methods ---
    void Awake()
    {
        // It's good practice to ensure the info field is updated if the script reloads
        activeMicrophoneDeviceForInfo = selectedMicrophoneDeviceName;
    }
    void Start()
    {
        // Initialize WebSocket URLs
        UpdateConnectionUrls(); // Set URLs based on initial selection

        PerformServerCheck();

        InitializeMicrophoneAndStart();


        // --- listener ---
        if (OnInterruptionDetected == null) OnInterruptionDetected = new UnityEngine.Events.UnityEvent();
        if (OnFinalTranscription == null) OnFinalTranscription = new TranscriptionEvent(); // Safety check
        OnFinalTranscription.AddListener(HandleFinalTranscription);
    }

    async void Update()
    {
        // Dispatch messages received on the main thread
#if !UNITY_WEBGL || UNITY_EDITOR
        if (controlWs != null)
        {
            controlWs.DispatchMessageQueue();
        }
        if (dataWs != null)
        {
            dataWs.DispatchMessageQueue();
        }
#endif

        // Handle connection requests
        if (wantsToConnect)
        {
            wantsToConnect = false; // Attempt only once per request
            await Connect();
        }
    }

    async void OnDestroy()
    {
        await Disconnect();
        StopMicrophone(); // Ensure microphone is stopped
    }

    async void OnApplicationQuit()
    {
        await Disconnect();
        StopMicrophone();
    }

    public void SetUserRoleName(string roleName)
    {
        userRole = roleName + ": ";
    }

    public string GetCurrentMicrophoneDevice()
    {
        return microphoneDevice;
    }

    // This method is called by the editor script when the selection changes
    public void OnMicrophoneSelectedInEditor(string deviceName)
    {
        selectedMicrophoneDeviceName = deviceName;
        activeMicrophoneDeviceForInfo = deviceName;
    }

    // Helper to update URLs based on the selected IP
    private void UpdateConnectionUrls()
    {
        if (serverIPs == null || serverIPs.Count == 0)
        {
            if (string.IsNullOrEmpty(controlUrl)) // Prevent spamming log if list stays empty
                Debug.LogError("Server IP list is empty!");
            controlUrl = "";
            dataUrl = "";
            return;
        }

        // Clamp index to valid range
        selectedIPIndex = Mathf.Clamp(selectedIPIndex, 0, serverIPs.Count - 1);

        string selectedIP = serverIPs[selectedIPIndex];
        string newControlUrl = $"ws://{selectedIP}:{controlPort}";
        string newDataUrl = $"ws://{selectedIP}:{dataPort}";

        // Only update if changed to avoid unnecessary string creation
        if (controlUrl != newControlUrl)
        {
            controlUrl = newControlUrl;
            Debug.Log($"Control URL set to: {controlUrl}");
        }
        if (dataUrl != newDataUrl)
        {
            dataUrl = newDataUrl;
            Debug.Log($"Data URL set to: {dataUrl}");
        }
    }
    private void InitializeMicrophoneAndStart()
    {
        // Microphone is now handled by WebRTCAudioProcessor
        Debug.Log("[STTHandler] Audio input is managed by WebRTCAudioProcessor.");
        
        // Just start the consumer coroutine
        if (!isRecording)
        {
            isRecording = true;
            StartCoroutine(ConsumeAndSendAudio());
        }
    }

    // StartMicrophoneCoroutine removed


    [ContextMenu("Test Server Check")]
    private void PerformServerCheck()
    {
        TriggerServerCheck(3.0f);
    }


    public void TriggerServerCheck(float timeoutSeconds = 3.0f)
    {
        if (!isCheckingServer) // Prevent multiple simultaneous checks
        {
            StartCoroutine(PerformServerCheckCoroutine(timeoutSeconds));
        }
        else
        {
            Debug.LogWarning("Server check already in progress.");
        }
    }

    // Coroutine to run the async check and handle results on the main thread
    private IEnumerator PerformServerCheckCoroutine(float timeoutSeconds = 3.0f)
    {
        isCheckingServer = true;
        Debug.Log($"Performing server sanity check for {controlUrl}...");

        Task<bool> checkTask = CheckServerStatus(timeoutSeconds);
        yield return new WaitUntil(() => checkTask.IsCompleted); // Wait for the async task

        bool result = false;
        if (checkTask.IsFaulted)
        {
            Debug.LogError($"Server check failed with exception: {checkTask.Exception?.InnerException?.Message ?? checkTask.Exception?.Message}");
            result = false;
        }
        else if (checkTask.IsCanceled)
        {
            Debug.LogWarning("Server check was cancelled (likely timeout).");
            result = false;
        }
        else
        {
            result = checkTask.Result;
            Debug.Log($"STT Server check completed. STT Server is {(result ? "reachable" : "NOT reachable")}.");
        }
        // Update the status indicator based on the result
        if (statusIndicator != null)
        {
            statusIndicator.color = result ? Color.green : Color.red; // Green for success, red for failure
        }

        lastServerCheckResult = result;
        isCheckingServer = false;
        OnServerCheckCompleted?.Invoke(result); // Signal completion with result
    }


    // Server Sanity Check Function (async Task)
    /// <summary>
    /// Attempts a quick WebSocket connection to the control URL to check server status.
    /// </summary>
    /// <param name="timeoutSeconds">How long to wait for connection attempt.</param>
    /// <returns>True if the server acknowledged the connection attempt, false otherwise (timeout or error).</returns>
    public async Task<bool> CheckServerStatus(float timeoutSeconds = 3.0f)
    {
        if (string.IsNullOrEmpty(controlUrl))
        {
            Debug.LogError("Cannot check server status: Control URL is not set (check IP list).");
            return false;
        }

        WebSocket tempWs = null; // Use a temporary WebSocket instance
        try
        {
            tempWs = new WebSocket(controlUrl);

            // Use TaskCompletionSource for timeout control over the Connect() task
            var tcs = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false); // Ensure cancellation on timeout

            // Handle temporary WS events
            tempWs.OnOpen += () =>
            {
                // Successfully opened, close immediately and signal success
                Debug.Log("Sanity Check: Connection opened.");
                tempWs?.Close(); // Request close
                tcs.TrySetResult(true); // Mark task as successful
            };

            tempWs.OnError += (e) =>
            {
                Debug.LogError($"Sanity Check Error: {e}");
                tcs.TrySetResult(false); // Connection failed
            };

            tempWs.OnClose += (e) =>
            {
                // This might be called after OnOpen or OnError.
                // The TaskCompletionSource should already be set by then.
                // If it closes before opening or erroring (e.g., immediate refusal), consider it failure.
                Debug.Log($"Sanity Check: Connection closed ({e}).");
                tcs.TrySetResult(false); // If it closes without OnOpen firing, it's likely a failure/refusal
            };

            // Initiate connection
            Debug.Log($"Sanity Check: Attempting temporary connection to {controlUrl}...");
            await tempWs.Connect(); // Start the connection attempt (non-blocking)

            // Wait for the connection attempt to complete OR timeout
            bool success = await tcs.Task; // This will wait until OnOpen/OnError/OnClose sets the result or timeout cancels it
            return success;

        }
        catch (Exception ex)
        {
            // Catch exceptions during setup or from the Connect() call itself (less common with NativeWebSocket event model)
            Debug.LogError($"Sanity Check Exception: {ex.Message}");
            return false;
        }
        finally
        {
            // Ensure the temporary WebSocket is closed and resources released,
            // even if it's already closing/closed. NativeWebSocket might handle this internally,
            // but explicit close is good practice. Check state before closing.
            if (tempWs != null && tempWs.State == WebSocketState.Open)
            {
                // Debug.Log("Sanity Check: Ensuring temporary WebSocket is closed.");
                await tempWs.Close(); // Ensure it's closed if still open
            }
            // Note: We don't nullify tempWs here as it's a local variable.
        }
    }

    private void HandleTranslationResult(string translatedText)
    {
        llmhandler.Question(userRole + translatedText);
    }

    [ContextMenu("Test STT")]
    private void TestSTT()
    {
        HandleFinalTranscription(testString);
    }

    private void HandleFinalTranscription(string transcribedText, string language = "en")
    {
        // show what language it is
        Debug.Log($"Final transcription received: '{language}': '{transcribedText}'. Sending to LLM Handler.");

        // Check if the handler is assigned before calling it
        if (llmhandler != null)
        {
            if (!string.IsNullOrEmpty(transcribedText)) // Optional: Check if text is not empty
            {
                
                HandleTranslationResult(transcribedText); 
                if (transcriptionText != null)
                {
                    transcriptionText.text = transcribedText; // Update UI text field
                }

                if (subtitle != null)
                {
                    subtitle.text = transcribedText; // Update subtitle text field

                    subtitle.gameObject.SetActive(true); // Ensure the GameObject is active

                    // Reset alpha to fully opaque
                    Color subtitleColor = subtitle.color;
                    subtitleColor.a = 1f; // 1 is fully opaque
                    subtitle.color = subtitleColor;

                    // If there's an old fade coroutine running, stop it
                    if (_activeFadeCoroutine != null)
                    {
                        StopCoroutine(_activeFadeCoroutine);
                    }

                    // Start the new fade coroutine
                    _activeFadeCoroutine = StartCoroutine(FadeOutSubtitleAfterDelay());
                }

            }
            else
            {
                Debug.Log("Skipping LLM call because transcribed text is empty.");
            }
        }
        else
        {
            Debug.LogError("Cannot send transcription to LLM: LlmHandler reference is not set!", this);
        }
    }

    private IEnumerator FadeOutSubtitleAfterDelay()
    {
        // 1. Wait for the initial display time (3 seconds)
        yield return new WaitForSeconds(displayTime);

        // 2. Fade out the subtitle
        float currentTime = 0f;
        Color startColor = subtitle.color; // Should be fully opaque here

        while (currentTime < fadeOutDuration)
        {
            currentTime += Time.deltaTime;
            // Calculate the new alpha value by linearly interpolating from 1 (opaque) to 0 (transparent)
            float alpha = Mathf.Lerp(startColor.a, 0f, currentTime / fadeOutDuration);

            subtitle.color = new Color(startColor.r, startColor.g, startColor.b, alpha);
            yield return null; // Wait for the next frame before continuing the loop
        }

        // Ensure alpha is exactly 0 at the end
        subtitle.color = new Color(startColor.r, startColor.g, startColor.b, 0f);

        // Optional: Deactivate the subtitle GameObject after it has faded out
        subtitle.gameObject.SetActive(false);

        _activeFadeCoroutine = null; // Clear the reference to the completed coroutine
    }


    // --- Public Control Methods ---

    public void RequestConnect()
    {
        if (!isControlConnected && !isDataConnected)
        {
            wantsToConnect = true;
        }
        else
        {
            Debug.LogWarning("Already connected or connecting.");
        }
    }

    // Make sure to reset the _onConnectedInvoked flag in Disconnect as well
    public async Task Disconnect()
    {
        wantsToConnect = false;

        // Use Task.WhenAll to close concurrently
        List<Task> closeTasks = new List<Task>();
        if (controlWs != null)
        {
            Debug.Log("Closing Control WebSocket...");
            closeTasks.Add(controlWs.Close());
        }
        if (dataWs != null)
        {
            Debug.Log("Closing Data WebSocket...");
            closeTasks.Add(dataWs.Close());
        }

        try
        {
            await Task.WhenAll(closeTasks);
            Debug.Log("WebSocket closing tasks completed.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Exception during Task.WhenAll for closing sockets: {ex.Message}");
        }
        finally
        {
            // Nullify AFTER close attempt completes or fails
            controlWs = null;
            dataWs = null;
            isControlConnected = false;
            isDataConnected = false;
            _onConnectedInvoked = false; // Reset connection flag

            // Explicitly invoke OnDisconnected if it wasn't triggered by OnClose handlers
            // This ensures it's called even if OnClose events didn't fire correctly before nullifying.
            // However, rely primarily on OnClose within CheckOverallConnectionStatus.
            // Consider if an explicit call here is needed or causes duplicates.
            // Let's comment it out for now and rely on OnClose/CheckOverallConnectionStatus.
            // OnDisconnected?.Invoke();
            Debug.Log("Disconnect process finished.");
        }
    }

    public void Mute()
    {
        if (!isMuted)
        {
            Debug.Log("Microphone Muted.");
            isMuted = true;
        }
    }

    public void Unmute()
    {
        if (isMuted)
        {
            Debug.Log("Microphone Unmuted. Sending audio data.");
            isMuted = false;
        }
    }

    public void SetParameter(string parameterName, object value)
    {
        if (!isControlConnected || controlWs == null || controlWs.State != WebSocketState.Open)
        {
            Debug.LogError("Control WebSocket not connected. Cannot set parameter.");
            return;
        }

        ControlCommand cmd = new ControlCommand
        {
            command = "set_parameter",
            parameter = parameterName,
            value = value
        };
        string jsonCmd = JsonUtility.ToJson(cmd);
        Debug.Log($"Sending SetParameter: {jsonCmd}");
        controlWs.SendText(jsonCmd);
    }

    public async Task<object> GetParameter(string parameterName, float timeoutSeconds = 5.0f)
    {
        if (!isControlConnected || controlWs == null || controlWs.State != WebSocketState.Open)
        {
            Debug.LogError("Control WebSocket not connected. Cannot get parameter.");
            return null;
        }

        int requestId = Interlocked.Increment(ref requestCounter);
        var tcs = new TaskCompletionSource<object>();
        pendingParameterRequests[requestId] = tcs;

        ControlCommand cmd = new ControlCommand
        {
            command = "get_parameter",
            parameter = parameterName,
            request_id = requestId
        };
        string jsonCmd = JsonUtility.ToJson(cmd);
        Debug.Log($"Sending GetParameter (ID: {requestId}): {jsonCmd}");
        await controlWs.SendText(jsonCmd); // Use await for NativeWebSocket

        // Timeout logic
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

        if (completedTask == tcs.Task)
        {
            pendingParameterRequests.Remove(requestId);
            return await tcs.Task; // Return the received value
        }
        else
        {
            Debug.LogError($"Timeout waiting for GetParameter response for '{parameterName}' (ID: {requestId}).");
            pendingParameterRequests.Remove(requestId);
            return null; // Indicate timeout
        }
    }

    public void CallMethod(string methodName, List<object> args = null, Dictionary<string, object> kwargs = null)
    {
        if (!isControlConnected || controlWs == null || controlWs.State != WebSocketState.Open)
        {
            Debug.LogError("Control WebSocket not connected. Cannot call method.");
            return;
        }

        ControlCommand cmd = new ControlCommand
        {
            command = "call_method",
            method = methodName,
            args = args ?? new List<object>(),
            kwargs = kwargs ?? new Dictionary<string, object>()
        };

        // JsonUtility doesn't directly support Dictionary, need custom serialization or different library (like Newtonsoft.Json)
        // For simplicity, sending kwargs as null for now. Adapt if needed.
        // string jsonCmd = JsonUtility.ToJson(cmd);
        string jsonCmd = JsonConvert.SerializeObject(cmd, Formatting.None,
                                     new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });


        Debug.Log($"Sending CallMethod: {jsonCmd}");
        controlWs.SendText(jsonCmd);
    }

    /// <summary>
    /// Call this method when your TTS system starts playing audio.
    /// </summary>
    public void NotifyTTSStarted()
    {
        if (!isControlConnected || controlWs == null || controlWs.State != WebSocketState.Open)
        {
            Debug.LogWarning("Cannot notify TTS started: Control WebSocket not connected.");
            return;
        }

        isTTSPlaying = true;
        Debug.Log("STTHandler: Notifying server that TTS has started.");

        ControlCommand cmd = new ControlCommand
        {
            command = "set_tts_state", // Matches the command in stt_server.py
                                       // 'parameter' and 'value' are not standard for this custom command,
                                       // but we can adapt the ControlCommand or create a new one.
                                       // For now, we'll use a custom value structure within the 'value' field
                                       // or add a dedicated field to ControlCommand if you modify it.
                                       // Let's pass 'active' directly as part of a dictionary in 'value' or use a more specific command structure.
                                       // The python side expects `active` in the command_data, not within `value`.
                                       // So, let's create a slightly different structure for this specific command.
        };

        // Using Newtonsoft.Json for more flexible serialization for custom commands
        var ttsStateCommand = new { command = "set_tts_state", active = true };
        string jsonCmd = JsonConvert.SerializeObject(ttsStateCommand);

        Debug.Log($"Sending TTS State (Active: true): {jsonCmd}");
        controlWs.SendText(jsonCmd);
    }

    /// <summary>
    /// Call this method when your TTS system stops playing audio.
    /// </summary>
    public void NotifyTTSStopped()
    {
        if (!isControlConnected || controlWs == null || controlWs.State != WebSocketState.Open)
        {
            // It's possible TTS stops after disconnection, so this might not always be an error.
            // Debug.LogWarning("Cannot notify TTS stopped: Control WebSocket not connected.");
            // return;
        }

        isTTSPlaying = false;
        Debug.Log("STTHandler: Notifying server that TTS has stopped.");

        if (isControlConnected && controlWs != null && controlWs.State == WebSocketState.Open)
        {
            var ttsStateCommand = new { command = "set_tts_state", active = false };
            string jsonCmd = JsonConvert.SerializeObject(ttsStateCommand);

            Debug.Log($"Sending TTS State (Active: false): {jsonCmd}");
            controlWs.SendText(jsonCmd);
        }
    }


    // --- Internal Logic ---

    // --- Internal Logic ---

    private async Task Connect()
    {
        lastError = "";
        bool controlConnectAttempted = false;
        bool dataConnectAttempted = false;

        // --- Setup and Initiate Control WebSocket Connection ---
        controlWs = new WebSocket(controlUrl);

        controlWs.OnOpen += () =>
        {
            Debug.Log("Control WebSocket connection opened.");
            // Use Interlocked or locks if accessed from multiple threads,
            // but NativeWebSocket dispatches to main thread, so direct check should be okay here.
            CheckOverallConnectionStatus(true, isDataConnected);
        };

        controlWs.OnError += (e) =>
        {
            Debug.LogError($"Control WebSocket error: {e}");
            lastError = $"Control WS Error: {e}";
            OnErrorReceived?.Invoke(lastError);
            isControlConnected = false; // Assume disconnect on error
            CheckOverallConnectionStatus(false, isDataConnected); // Update overall status
        };

        controlWs.OnClose += (e) =>
        {
            // Check if ws instance still exists before logging/acting
            if (controlWs != null)
            {
                Debug.Log($"Control WebSocket connection closed: {e}");
                isControlConnected = false;
                // If the other socket is also closed, trigger disconnect event
                if (!isDataConnected)
                {
                    OnDisconnected?.Invoke();
                }
            }
            else
            {
                Debug.Log($"Control WebSocket already null on close event: {e}");
            }
        };

        controlWs.OnMessage += (bytes) =>
        {
            var message = System.Text.Encoding.UTF8.GetString(bytes);
            // Debug.Log($"Control WS Message: {message}");
            HandleControlMessage(message);
        };

        Debug.Log("Attempting to connect Control WebSocket...");
        // Start connection without awaiting here
        var controlConnectTask = controlWs.Connect();
        controlConnectAttempted = true;


        // --- Setup and Initiate Data WebSocket Connection ---
        dataWs = new WebSocket(dataUrl);

        dataWs.OnOpen += () =>
        {
            Debug.Log("Data WebSocket connection opened.");
            CheckOverallConnectionStatus(isControlConnected, true);
        };

        dataWs.OnError += (e) =>
        {
            Debug.LogError($"Data WebSocket error: {e}");
            lastError = $"Data WS Error: {e}";
            OnErrorReceived?.Invoke(lastError);
            isDataConnected = false; // Assume disconnect on error
            CheckOverallConnectionStatus(isControlConnected, false); // Update overall status
        };

        dataWs.OnClose += (e) =>
        {
            // Check if ws instance still exists before logging/acting
            if (dataWs != null)
            {
                Debug.Log($"Data WebSocket connection closed: {e}");
                isDataConnected = false;
                // If the other socket is also closed, trigger disconnect event
                if (!isControlConnected)
                {
                    OnDisconnected?.Invoke();
                }
            }
            else
            {
                Debug.Log($"Data WebSocket already null on close event: {e}");
            }
        };

        dataWs.OnMessage += (bytes) =>
        {
            // Data messages are expected to be JSON text
            var message = System.Text.Encoding.UTF8.GetString(bytes);
            // Debug.Log($"Data WS Message: {message}");
            HandleDataMessage(message);
        };

        Debug.Log("Attempting to connect Data WebSocket...");
        // Start connection without awaiting here
        var dataConnectTask = dataWs.Connect();
        dataConnectAttempted = true;

        // --- Optional: Wait for both connection attempts to finish ---
        // You might want to wait here if subsequent logic depends on connections being established
        // or if you want Connect() to represent the full attempt phase.
        // If you only care about events firing when connections open/close, you can remove this.
        try
        {
            // Wait for both tasks initiated above to complete (either succeed or fail)
            await Task.WhenAll(controlConnectTask, dataConnectTask);
            Debug.Log("Initial connection attempts completed (check OnOpen/OnError for status).");
        }
        catch (Exception ex)
        {
            // Task.WhenAll throws if *any* task throws. Log the exception.
            // Individual OnError handlers should have already logged specific WS errors.
            Debug.LogWarning($"Exception during Task.WhenAll for connection attempts: {ex.Message}");
        }

        // Safety check if somehow OnOpen wasn't triggered but connection succeeded silently (unlikely)
        // Update internal state based on final WebSocket state after awaiting attempts.
        // This helps ensure isConnected flags are accurate even if events fired unexpectedly.
        if (controlWs != null) isControlConnected = controlWs.State == WebSocketState.Open;
        if (dataWs != null) isDataConnected = dataWs.State == WebSocketState.Open;

        // Final check after attempts completed
        CheckOverallConnectionStatus(isControlConnected, isDataConnected);


    }

    // Updated CheckOverallConnectionStatus to prevent multiple OnConnected calls
    private bool _onConnectedInvoked = false; // Track if OnConnected has been called

    private void CheckOverallConnectionStatus(bool ctrlConnected, bool dataConnected)
    {
        bool previouslyConnected = isControlConnected && isDataConnected;

        // Update internal state *before* invoking events based on the new state
        isControlConnected = ctrlConnected;
        isDataConnected = dataConnected;

        bool currentlyConnected = isControlConnected && isDataConnected;

        // --- Handle Connection ---
        // If now connected and wasn't before, and haven't invoked OnConnected yet this session
        if (currentlyConnected && !previouslyConnected && !_onConnectedInvoked)
        {
            Debug.Log("Both WebSockets connected successfully.");
            OnConnected?.Invoke();
            _onConnectedInvoked = true; // Mark OnConnected as invoked
        }
        // --- Handle Disconnection ---
        // If now disconnected but was previously connected
        else if (!currentlyConnected && previouslyConnected)
        {
            Debug.Log("A WebSocket disconnected, overall status is now disconnected.");
            OnDisconnected?.Invoke();
            _onConnectedInvoked = false; // Reset OnConnected flag upon disconnection
        }
        // --- Handle edge case: If connection attempt fails before OnConnected was ever invoked ---
        // If we are not connected, and OnConnected was never invoked (meaning initial connection failed)
        // and we were previously not fully connected either (handles state updates where one connects then disconnects)
        else if (!currentlyConnected && !_onConnectedInvoked && !previouslyConnected)
        {
            // Check if *both* connections are closed/aborted after attempting
            bool ctrlFailed = controlWs == null || controlWs.State == WebSocketState.Closed || controlWs.State == WebSocketState.Closing;
            bool dataFailed = dataWs == null || dataWs.State == WebSocketState.Closed || dataWs.State == WebSocketState.Closing;

            // If both failed or are closed, and we never got to a fully connected state
            if (ctrlFailed && dataFailed)
            {
                // This could be considered a failed connection attempt.
                // Optionally trigger OnDisconnected here if you want that event for failed initial connects.
                // Debug.Log("Initial connection attempt failed for one or both sockets.");
                // OnDisconnected?.Invoke(); // Uncomment if desired behavior
                // _onConnectedInvoked = false; // Ensure flag is reset
            }
        }

    }


    private void HandleControlMessage(string message)
    {
        try
        {
            // Using Newtonsoft.Json here because Unity's JsonUtility might struggle with 'object' value type
            // ServerControlResponse response = JsonUtility.FromJson<ServerControlResponse>(message);
            ServerControlResponse response = JsonConvert.DeserializeObject<ServerControlResponse>(message);


            if (response.request_id >= 0 && pendingParameterRequests.ContainsKey(response.request_id))
            {
                if (response.status == "success")
                {
                    Debug.Log($"Received response for GetParameter '{response.parameter}' (ID: {response.request_id}): {response.value}");
                    pendingParameterRequests[response.request_id].TrySetResult(response.value);
                }
                else
                {
                    Debug.LogError($"Server error for GetParameter '{response.parameter}' (ID: {response.request_id}): {response.message}");
                    pendingParameterRequests[response.request_id].TrySetException(new Exception(response.message ?? "Unknown server error"));
                }
                // Don't remove from dict here, handled in GetParameter timeout logic
            }
            else if (response.status == "success")
            {
                // Handle success messages for SetParameter or CallMethod if needed
                Debug.Log($"Server success: Parameter '{response.parameter}' set or method called.");
            }
            else if (response.status == "error")
            {
                Debug.LogError($"Server error: {response.message}");
                lastError = $"Server Error: {response.message}";
                OnErrorReceived?.Invoke(lastError);
            }
            else
            {
                Debug.LogWarning($"Received unknown control message: {message}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to parse control message: {message}\nError: {e}");
        }
    }

    private void HandleDataMessage(string message)
    {
        try
        {
            // Using Newtonsoft.Json for flexibility
            //ServerDataMessage dataMessage = JsonUtility.FromJson<ServerDataMessage>(message);
            ServerDataMessage dataMessage = JsonConvert.DeserializeObject<ServerDataMessage>(message);


            switch (dataMessage.type)
            {
                case "realtime":
                    // Debug.Log($"Realtime: {dataMessage.text}");
                    OnRealtimeTranscriptionUpdate?.Invoke(dataMessage.text);
                    break;
                case "fullSentence":
                    Debug.Log($"Final: {dataMessage.text}");
                    OnFinalTranscription?.Invoke(dataMessage.text, dataMessage.language);
                    break;
                case "recording_start":
                    Debug.Log("Server indicated recording started.");
                    OnRecordingStarted?.Invoke();
                    break;
                case "recording_stop":
                    Debug.Log("Server indicated recording stopped.");
                    OnRecordingStopped?.Invoke();
                    break;
                case "vad_detect_start":
                    Debug.Log("User stopped talking. VAD Start detecting for next speech");
                    OnSpeechStopDetected?.Invoke();
                    AvatarBehaviorController.Instance?.TriggerBehavior(AvatarBehavior.Idle);
                    break;
                case "vad_detect_stop":
                    Debug.Log("User started talking. VAD Stop detecting");
                    AvatarBehaviorController.Instance?.TriggerBehavior(AvatarBehavior.ActiveListen);
                    OnSpeechStartDetected?.Invoke();
                    break;
                // case "start_turn_detection":
                //     Debug.Log("Server indicated start of turn detection.");
                //     break;
                // case "stop_turn_detection":
                //     Debug.Log("Server indicated end of turn detection.");
                //     break;

                case "interruption_detected": // New case for interruption
                    Debug.LogWarning("STTHandler: Interruption detected by server!");
                    OnInterruptionDetected?.Invoke();
                    // After an interruption, the server-side STT aborts.
                    // Your TTS and LLM should be stopped by whatever listens to OnInterruptionDetected.
                    // Once they are stopped, Unity should call NotifyTTSStopped()
                    // which will inform the server that TTS is no longer active.
                    // This is important so the server doesn't immediately flag another interruption
                    // if there's a quick follow-up sound.
                    isTTSPlaying = false; // Assume TTS will be stopped by the listener of OnInterruptionDetected
                                          // And then that system will call NotifyTTSStopped() to confirm with server.
                                          // Forcing it false here helps prevent re-triggering locally if there's a delay.
                                          // Add cases for other message types from Python script as needed
                                          // case "transcription_start":
                                          //     Debug.Log("Server indicated transcription started.");
                                          //     // Process dataMessage.audio_bytes_base64 if needed
                                          //     break;
                                          // case "vad_start": Debug.Log("VAD Start"); break;
                                          // case "vad_stop": Debug.Log("VAD Stop"); break;
                                          // case "wakeword_detected": Debug.Log("Wakeword Detected"); break;
                                          // // ... other types
                    break;

                default:
                    //Debug.LogWarning($"Received unknown data message type: {dataMessage.type} - {message}");
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to parse data message: {message}\nError: {e}");
        }
    }

    private void StartMicrophone()
    {
        if (isRecording) return;
        Debug.Log("[STTHandler] Resuming audio consumption.");
        isRecording = true;
        StartCoroutine(ConsumeAndSendAudio());
    }

    private void StopMicrophone()
    {
        if (!isRecording) return;
        Debug.Log("[STTHandler] Pausing audio consumption.");
        isRecording = false;
    }

    private IEnumerator ConsumeAndSendAudio()
    {
        while (isRecording)
        {
            if (WebRTCAudioProcessor.Instance != null)
            {
                // Dequeue all available chunks
                while (WebRTCAudioProcessor.Instance.OutputQueue.TryDequeue(out short[] chunk))
                {
                    if (!isMuted && isDataConnected && dataWs != null && dataWs.State == WebSocketState.Open && chunk.Length > 0)
                    {
                        SendAudioChunk(chunk, chunk.Length);
                    }
                }
            }
            yield return null;
        }
    }

    // Overload for short[] input (AEC output)
    private async void SendAudioChunk(short[] samples, int sampleCount)
    {
        if (!isDataConnected || dataWs == null || dataWs.State != WebSocketState.Open || sampleCount == 0)
        {
            return;
        }

        // 1. Convert short[] (pcm16) to bytes
        byte[] pcmData = new byte[sampleCount * 2];
        Buffer.BlockCopy(samples, 0, pcmData, 0, pcmData.Length);
        
        // Ensure Little Endian if system is Big Endian (Unity/C# usually runs on LE, but good practice)
        if (!BitConverter.IsLittleEndian)
        {
             // BlockCopy copies bytes directly. If 'samples' are in machine endian (Big), we need to swap.
             // But 'short[]' in C# memory is machine endian.
             // If we used BlockCopy, we got bytes in machine endian.
             // If machine is Big, we need to swap every pair.
             for(int i=0; i<pcmData.Length; i+=2) {
                 byte temp = pcmData[i];
                 pcmData[i] = pcmData[i+1];
                 pcmData[i+1] = temp;
             }
        }
        
        // 2. Prepare metadata JSON
        AudioMetadata metadata = new AudioMetadata { sampleRate = this.recordingFrequency };
        string metadataJson = JsonUtility.ToJson(metadata);
        byte[] metadataBytes = Encoding.UTF8.GetBytes(metadataJson);

         // 3. Get metadata length as 4-byte little-endian integer
        int metadataLength = metadataBytes.Length;
        byte[] lengthBytes = BitConverter.GetBytes(metadataLength);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(lengthBytes); // Ensure LittleEndian
        }

        // 4. Combine into final message: [metadata_length][metadata_json][audio_bytes]
        byte[] messageBytes = new byte[lengthBytes.Length + metadataBytes.Length + pcmData.Length];
        Buffer.BlockCopy(lengthBytes, 0, messageBytes, 0, lengthBytes.Length);
        Buffer.BlockCopy(metadataBytes, 0, messageBytes, lengthBytes.Length, metadataBytes.Length);
        Buffer.BlockCopy(pcmData, 0, messageBytes, lengthBytes.Length + metadataBytes.Length, pcmData.Length);

        // 5. Send
        if (dataWs != null && dataWs.State == WebSocketState.Open)
        {
            try
            {
                await dataWs.Send(messageBytes);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error sending audio data: {e.Message}");
                lastError = $"Send Error: {e.Message}";
                OnErrorReceived?.Invoke(lastError);
            }
        }
    }

    private async void SendAudioChunk(float[] samples, int sampleCount)
    {
        // KEEPING FOR COMPATIBILITY (But shouldn't be called in new flow)
        if (!isDataConnected || dataWs == null || dataWs.State != WebSocketState.Open || sampleCount == 0)
        {
            return;
        }
        // ... (Original logic omitted for brevity, but relying on existing implementation if needed)
        // Since we replaced the method body in previous replace call, we need to be careful.
        // The instruction said "overload". I will just adapt logic here to convert float->short then call overload.
        
        short[] shortSamples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
             shortSamples[i] = (short)Mathf.Clamp(samples[i] * 32767f, -32768f, 32767f);
        }
        SendAudioChunk(shortSamples, sampleCount);
    }
}


// Helper attribute for read-only fields in Inspector
public class ReadOnlyAttribute : PropertyAttribute { }

#if UNITY_EDITOR
[UnityEditor.CustomPropertyDrawer(typeof(ReadOnlyAttribute))]
public class ReadOnlyDrawer : UnityEditor.PropertyDrawer
{
    public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label)
    {
        GUI.enabled = false;
        UnityEditor.EditorGUI.PropertyField(position, property, label, true);
        GUI.enabled = true;
    }
}
#endif