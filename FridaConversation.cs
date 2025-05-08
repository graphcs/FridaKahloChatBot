using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Networking;
using System.Text;
using System.IO;
// Replace Newtonsoft.Json with Unity's built-in SimpleJSON
// using Newtonsoft.Json;

// Add main SALSA namespace - exact types can vary by version
using CrazyMinnow.SALSA;

public class FridaConversation : MonoBehaviour
{
    [SerializeField] private string serverUrl = "http://localhost:5001";
    [SerializeField] private Button recordButton;
    [SerializeField] private Button endSessionButton;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private int recordingDuration = 5;
    
    // Use MonoBehaviour reference to avoid compile-time errors with different SALSA versions
    [Tooltip("Reference to the Salsa component on your character")]
    [SerializeField] private MonoBehaviour salsaComponent; // Using generic MonoBehaviour to support any SALSA version
    
    // Microphone settings
    [SerializeField] private bool useDynamicListening = false;
    [SerializeField] private float silenceThreshold = 0.01f;
    [SerializeField] private float silenceTimeToStop = 1.5f;
    
    private string sessionId;
    private bool isRecording = false;
    private AudioClip recordingClip;
    private bool isProcessingResponse = false;
    private bool isWaitingForResponse = false;
    private Coroutine checkResponseCoroutine;
    private Coroutine dynamicRecordingCoroutine;
    
    // Structure for API responses
    [Serializable]
    private class SessionResponse
    {
        public string session_id;
        public string welcome_text;
        public string welcome_audio;
        public float estimated_duration;
    }
    
    [Serializable]
    private class TranscriptionResponse
    {
        public string text;
    }
    
    [Serializable]
    private class FridaResponse
    {
        public string text;
        public string audio_base64;
    }
    
    [Serializable]
    private class FillerResponse
    {
        public string text;
        public string audio_base64;
        public float estimated_duration;
    }
    
    [Serializable]
    private class StatusResponse
    {
        public bool completed;
        public string status;
        public string text;
        public string audio_base64;
        public float duration;
        public PhonemeData[] phoneme_data;
    }
    
    [Serializable]
    private class PhonemeData
    {
        public string word;
        public float start_time;
        public float end_time;
    }
    
    // Request data classes
    [Serializable]
    private class TextRequestData
    {
        public string text;
        public string session_id;
    }
    
    [Serializable]
    private class SessionRequestData
    {
        public string session_id;
    }
    
    void Start()
    {
        try
        {
            Debug.Log("Starting Frida Conversation...");
            
            // Initialize UI elements
            if (recordButton != null)
            {
                recordButton.onClick.AddListener(ToggleRecording);
                Debug.Log("Record button connected");
            }
            else
            {
                Debug.LogWarning("Record button not assigned!");
            }
            
            if (endSessionButton != null)
            {
                endSessionButton.onClick.AddListener(EndSession);
                Debug.Log("End session button connected");
            }
            else
            {
                Debug.LogWarning("End session button not assigned!");
            }
            
            // Initialize AudioSource if not set
            if (audioSource == null)
            {
                Debug.Log("Adding AudioSource component");
                audioSource = gameObject.AddComponent<AudioSource>();
            }
            
            // Start a new session
            StartCoroutine(StartSession());
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error in Start: {e.Message}");
        }
    }
    
    private IEnumerator StartSession()
    {
        Debug.Log("Starting Frida session...");
        string url = $"{serverUrl}/start_session";
        
        UnityWebRequest www = null;
        
        try
        {
            www = UnityWebRequest.PostWwwForm(url, "");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error creating web request in StartSession: {e.Message}");
            yield break;
        }
        
        // Move yield outside of try-catch
        yield return www.SendWebRequest();
        
        try
        {
            if (www.result == UnityWebRequest.Result.Success)
            {
                SessionResponse response = JsonUtility.FromJson<SessionResponse>(www.downloadHandler.text);
                sessionId = response.session_id;
                Debug.Log($"Session started with ID: {sessionId}");
                
                // Display welcome message
                Debug.Log("Welcome message: " + response.welcome_text);
                
                // Play welcome audio
                if (!string.IsNullOrEmpty(response.welcome_audio))
                {
                    byte[] audioBytes = Convert.FromBase64String(response.welcome_audio);
                    PlayAudioFromBytes(audioBytes, response.welcome_text, response.estimated_duration);
                }
            }
            else
            {
                Debug.LogError($"Failed to start session: {www.error}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error processing session response: {e.Message}");
        }
        finally
        {
            www.Dispose();
        }
    }
    
    public void ToggleRecording()
    {
        if (isProcessingResponse || isWaitingForResponse)
        {
            Debug.Log("Still processing previous request, please wait");
            return;
        }
        
        if (!isRecording)
        {
            if (useDynamicListening)
            {
                dynamicRecordingCoroutine = StartCoroutine(DynamicRecordAndProcess());
            }
            else
            {
                StartCoroutine(RecordAndProcess());
            }
        }
    }
    
    // New dynamic recording method similar to the Python implementation
    private IEnumerator DynamicRecordAndProcess()
    {
        isRecording = true;
        
        // Update UI
        if (recordButton != null)
        {
            Text buttonText = recordButton.GetComponentInChildren<Text>();
            if (buttonText != null)
            {
                buttonText.text = "Recording...";
            }
            else
            {
                Debug.LogWarning("Record button does not have a Text component child");
            }
        }
        
        // Check if microphone is available
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("No microphone detected. Please connect a microphone.");
            isRecording = false;
            yield break;
        }
        
        AudioClip tempClip = null;
        int sampleRate = 44100; // Moved out of try block to be accessible throughout method
        
        try
        {
            recordingClip = Microphone.Start(null, false, 30, sampleRate); // Max 30 seconds
            tempClip = recordingClip; // Store reference for cleanup
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error starting microphone: {e.Message}");
            isRecording = false;
            
            // Update UI
            if (recordButton != null)
            {
                Text buttonText = recordButton.GetComponentInChildren<Text>();
                if (buttonText != null)
                {
                    buttonText.text = "Record";
                }
            }
            yield break;
        }
        
        if (recordingClip == null)
        {
            Debug.LogError("Failed to start microphone recording");
            isRecording = false;
            yield break;
        }
        
        float[] samples = new float[1024];
        int startPosition = 0;
        float silenceTime = 0f;
        bool hasSpeechStarted = false;
        
        Debug.Log("Dynamic recording started, waiting for speech...");
        
        while (isRecording)
        {
            int currentPosition = Microphone.GetPosition(null);
            if (currentPosition < startPosition) currentPosition = recordingClip.samples;
            
            int diff = currentPosition - startPosition;
            if (diff < samples.Length) 
            {
                yield return null;
                continue;
            }
            
            recordingClip.GetData(samples, startPosition % recordingClip.samples);
            startPosition = (startPosition + samples.Length) % recordingClip.samples;
            
            // Calculate volume/energy in the sample
            float sum = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += Mathf.Abs(samples[i]);
            }
            float rms = sum / samples.Length;
            
            // Check if speech started
            if (!hasSpeechStarted)
            {
                if (rms > silenceThreshold)
                {
                    hasSpeechStarted = true;
                    Debug.Log("Speech detected!");
                }
            }
            else
            {
                // Check for silence
                if (rms < silenceThreshold)
                {
                    silenceTime += samples.Length / (float)sampleRate;
                    if (silenceTime >= silenceTimeToStop)
                    {
                        Debug.Log("Silence detected, stopping recording");
                        break;
                    }
                }
                else
                {
                    silenceTime = 0;
                }
            }
            
            yield return null;
        }
        
        // Stop recording
        Microphone.End(null);
        Debug.Log("Recording finished");
        
        if (!hasSpeechStarted)
        {
            Debug.Log("No speech detected, aborting");
            isRecording = false;
            if (recordButton != null)
            {
                Text buttonText = recordButton.GetComponentInChildren<Text>();
                if (buttonText != null)
                {
                    buttonText.text = "Record";
                }
            }
            yield break;
        }
        
        // Update UI
        if (recordButton != null)
        {
            Text buttonText = recordButton.GetComponentInChildren<Text>();
            if (buttonText != null)
            {
                buttonText.text = "Processing...";
            }
        }
        
        byte[] wavData = null;
        
        try
        {
            // Convert AudioClip to WAV
            wavData = AudioClipToWav(recordingClip);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error converting audio to WAV: {e.Message}");
            isRecording = false;
            
            // Update UI
            if (recordButton != null)
            {
                Text buttonText = recordButton.GetComponentInChildren<Text>();
                if (buttonText != null)
                {
                    buttonText.text = "Record";
                }
            }
            yield break;
        }
        
        // Send to server for transcription
        yield return StartCoroutine(TranscribeAudio(wavData));
        
        isRecording = false;
        
        // Update UI
        if (recordButton != null)
        {
            Text buttonText = recordButton.GetComponentInChildren<Text>();
            if (buttonText != null)
            {
                buttonText.text = "Record";
            }
        }
    }
    
    private IEnumerator RecordAndProcess()
    {
        isRecording = true;
        
        // Update UI
        if (recordButton != null)
        {
            Text buttonText = recordButton.GetComponentInChildren<Text>();
            if (buttonText != null)
            {
                buttonText.text = "Recording...";
            }
            else
            {
                Debug.LogWarning("Record button does not have a Text component child");
            }
        }
        
        // Check if microphone is available
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("No microphone detected. Please connect a microphone.");
            isRecording = false;
            yield break;
        }
        
        AudioClip tempClip = null;
        
        try
        {
            // Start recording
            recordingClip = Microphone.Start(null, false, recordingDuration, 44100);
            tempClip = recordingClip; // Store reference for cleanup
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error starting microphone: {e.Message}");
            isRecording = false;
            
            // Update UI
            if (recordButton != null)
            {
                Text buttonText = recordButton.GetComponentInChildren<Text>();
                if (buttonText != null)
                {
                    buttonText.text = "Record";
                }
            }
            yield break;
        }
        
        if (recordingClip == null)
        {
            Debug.LogError("Failed to start microphone recording");
            isRecording = false;
            yield break;
        }
        
        Debug.Log($"Recording for {recordingDuration} seconds...");
        
        // Wait for the recording to complete - outside try-catch
        yield return new WaitForSeconds(recordingDuration);
        
        // Stop recording
        Microphone.End(null);
        Debug.Log("Recording finished");
        
        // Update UI
        if (recordButton != null)
        {
            Text buttonText = recordButton.GetComponentInChildren<Text>();
            if (buttonText != null)
            {
                buttonText.text = "Processing...";
            }
        }
        
        byte[] wavData = null;
        
        try
        {
            // Convert AudioClip to WAV
            wavData = AudioClipToWav(recordingClip);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error converting audio to WAV: {e.Message}");
            isRecording = false;
            
            // Update UI
            if (recordButton != null)
            {
                Text buttonText = recordButton.GetComponentInChildren<Text>();
                if (buttonText != null)
                {
                    buttonText.text = "Record";
                }
            }
            yield break;
        }
        
        // Send to server for transcription - outside try-catch
        yield return StartCoroutine(TranscribeAudio(wavData));
        
        isRecording = false;
        
        // Update UI
        if (recordButton != null)
        {
            Text buttonText = recordButton.GetComponentInChildren<Text>();
            if (buttonText != null)
            {
                buttonText.text = "Record";
            }
        }
    }
    
    private IEnumerator TranscribeAudio(byte[] audioData)
    {
        string url = $"{serverUrl}/transcribe";
        
        WWWForm form = new WWWForm();
        form.AddBinaryData("audio", audioData, "recording.wav", "audio/wav");
        
        UnityWebRequest www = null;
        
        try
        {
            www = UnityWebRequest.Post(url, form);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error creating web request in TranscribeAudio: {e.Message}");
            yield break;
        }
        
        // Move yield outside of try-catch
        yield return www.SendWebRequest();
        
        TranscriptionResponse response = null;
        string transcribedText = null;
        bool success = false;
        
        try
        {
            if (www.result == UnityWebRequest.Result.Success)
            {
                response = JsonUtility.FromJson<TranscriptionResponse>(www.downloadHandler.text);
                transcribedText = response.text;
                Debug.Log($"Transcription: {transcribedText}");
                success = true;
            }
            else
            {
                Debug.LogError($"Transcription failed: {www.error}");
                Debug.LogError("Error: Could not transcribe audio");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error processing transcription response: {e.Message}");
        }
        finally
        {
            www.Dispose();
        }
        
        // Only proceed if we successfully got a transcription
        if (success && !string.IsNullOrEmpty(transcribedText))
        {
            // Play a filler immediately
            yield return StartCoroutine(PlayFiller());
            
            // Get Frida's response
            yield return StartCoroutine(GetFridaResponse(transcribedText));
        }
    }
    
    private IEnumerator PlayFiller()
    {
        string url = $"{serverUrl}/get_filler";
        
        UnityWebRequest www = null;
        
        try
        {
            www = UnityWebRequest.PostWwwForm(url, "");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error creating web request in PlayFiller: {e.Message}");
            yield break;
        }
        
        // Move yield outside of try-catch
        yield return www.SendWebRequest();
        
        try
        {
            if (www.result == UnityWebRequest.Result.Success)
            {
                FillerResponse response = JsonUtility.FromJson<FillerResponse>(www.downloadHandler.text);
                
                // Convert base64 to audio and play
                if (!string.IsNullOrEmpty(response.audio_base64))
                {
                    try {
                        Debug.Log($"Received filler audio data, length: {response.audio_base64.Length} characters");
                        byte[] audioBytes = Convert.FromBase64String(response.audio_base64);
                        Debug.Log($"Decoded filler audio data size: {audioBytes.Length} bytes");
                        
                        // Check file signature (first few bytes)
                        if (audioBytes.Length > 4) {
                            string signature = System.Text.Encoding.ASCII.GetString(audioBytes, 0, 4);
                            Debug.Log($"Filler audio data signature: {signature}");
                        }
                        
                        PlayAudioFromBytes(audioBytes, response.text, response.estimated_duration);
                    }
                    catch (System.Exception e) {
                        Debug.LogError($"Error processing filler audio data: {e.Message}");
                    }
                }
                else
                {
                    Debug.LogWarning("No audio in filler response");
                }
            }
            else
            {
                Debug.LogError($"Failed to get filler: {www.error}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error processing filler response: {e.Message}");
        }
        finally
        {
            www.Dispose();
        }
    }
    
    private IEnumerator GetFridaResponse(string userText)
    {
        isProcessingResponse = true;
        isWaitingForResponse = true;
        
        string url = $"{serverUrl}/get_response";
        
        // Create the request body using Unity's JsonUtility
        TextRequestData requestData = new TextRequestData
        {
            text = userText,
            session_id = sessionId
        };
        
        string jsonData = JsonUtility.ToJson(requestData);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
        
        UnityWebRequest www = null;
        
        try
        {
            www = new UnityWebRequest(url, "POST");
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error in GetFridaResponse: {e.Message}");
            isProcessingResponse = false;
            isWaitingForResponse = false;
            yield break;
        }
        
        // Move yield outside of try-catch
        yield return www.SendWebRequest();
        
        if (www.result == UnityWebRequest.Result.Success)
        {
            // Start checking for response completion
            checkResponseCoroutine = StartCoroutine(CheckResponseStatus());
        }
        else
        {
            Debug.LogError($"Failed to start response generation: {www.error}");
            Debug.LogError("Error: Could not get Frida's response");
            isProcessingResponse = false;
            isWaitingForResponse = false;
        }
        
        www.Dispose();
    }
    
    private IEnumerator CheckResponseStatus()
    {
        string url = $"{serverUrl}/check_response";
        
        // Create the request body using Unity's JsonUtility
        SessionRequestData requestData = new SessionRequestData
        {
            session_id = sessionId
        };
        
        string jsonData = JsonUtility.ToJson(requestData);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
        
        // Poll until response is complete or error occurs
        bool isComplete = false;
        int retryCount = 0;
        int maxRetries = 30; // Prevent infinite polling
        
        while (!isComplete && retryCount < maxRetries)
        {
            UnityWebRequest www = null;
            bool requestCreationError = false;
            
            try
            {
                www = new UnityWebRequest(url, "POST");
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error creating request in CheckResponseStatus: {e.Message}");
                requestCreationError = true;
                retryCount++;
            }
            
            // If there was an error creating the request, wait and continue
            if (requestCreationError)
            {
                yield return new WaitForSeconds(0.5f);
                continue;
            }
            
            // Move yield outside of try-catch
            yield return www.SendWebRequest();
            
            bool shouldContinue = false;
            
            if (www.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    StatusResponse response = JsonUtility.FromJson<StatusResponse>(www.downloadHandler.text);
                    
                    if (response.completed)
                    {
                        isComplete = true;
                        
                        // Log Frida's text response - this always works
                        Debug.Log("Frida response: " + response.text);
                        
                        // IMPORTANT: Always handle text response first to ensure that works
                        bool audioHandled = false;
                        
                        // APPROACH 1: Use base64 audio data directly if available
                        if (!string.IsNullOrEmpty(response.audio_base64))
                        {
                            try {
                                Debug.Log($"Received audio data, length: {response.audio_base64.Length} characters, estimated duration: {response.duration}s");
                                byte[] audioBytes = Convert.FromBase64String(response.audio_base64);
                                Debug.Log($"Decoded audio data size: {audioBytes.Length} bytes");
                                
                                // Check file signature (first few bytes)
                                string signature = "";
                                if (audioBytes.Length > 4) {
                                    signature = System.Text.Encoding.ASCII.GetString(audioBytes, 0, 4);
                                    Debug.Log($"Audio data signature: {signature}");
                                    
                                    // Also log hex bytes for better diagnosis
                                    if (audioBytes.Length > 16) {
                                        string hexHeader = BitConverter.ToString(audioBytes, 0, 16).Replace("-", " ");
                                        Debug.Log($"Audio header bytes: {hexHeader}");
                                    }
                                }
                                
                                // Use the bytes to play audio
                                PlayAudioFromBytes(audioBytes, response.text, response.duration);
                                audioHandled = true;
                            }
                            catch (System.Exception e) {
                                Debug.LogError($"Error processing audio data: {e.Message}");
                                // Will try alternative methods if this fails
                            }
                        }
                        
                        // APPROACH 2: Only try if base64 failed and session exists
                        if (!audioHandled && sessionId != null)
                        {
                            // Try to request audio directly, but don't wait for it
                            StartCoroutine(TryGetAudioAlternative(sessionId, response.text, response.duration));
                        }
                    }
                    else
                    {
                        // Response not complete yet, continue polling
                        shouldContinue = true;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Error processing response: {e.Message}");
                    shouldContinue = true;
                }
            }
            else
            {
                Debug.LogError($"Failed to check response status: {www.error}");
                shouldContinue = true;
            }
            
            www.Dispose();
            
            if (shouldContinue)
            {
                retryCount++;
                yield return new WaitForSeconds(0.5f); // Poll every half second
            }
        }
        
        if (!isComplete)
        {
            Debug.LogWarning("Timed out waiting for response");
        }
        
        isProcessingResponse = false;
        isWaitingForResponse = false;
    }
    
    private IEnumerator TryGetAudioAlternative(string sessionId, string text, float duration)
    {
        // This is a non-blocking way to try alternative audio download methods
        // We attempt this without blocking the main response processing
        
        // Method 1: Direct audio URL
        string directAudioUrl = $"{serverUrl}/get_audio?session_id={sessionId}";
        Debug.Log($"Attempting alternative audio download from: {directAudioUrl}");
        
        using (UnityWebRequest audioWww = UnityWebRequestMultimedia.GetAudioClip(directAudioUrl, AudioType.MPEG))
        {
            yield return audioWww.SendWebRequest();
            
            if (audioWww.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(audioWww);
                if (clip != null)
                {
                    Debug.Log("Successfully downloaded alternative audio");
                    audioSource.clip = clip;
                    audioSource.Play();
                    
                    // Set up SALSA lip sync with the alternative audio
                    SetupSalsaLipSync(text, clip.length, null);
                    
                    yield break;
                }
            }
            else
            {
                Debug.LogWarning($"Alternative audio download failed: {audioWww.error}");
            }
        }
        
        // If we get here, the direct method failed, text is still displayed at least
        Debug.Log("Using text-only fallback for response");
    }
    
    private void PlayAudioFromBytes(byte[] audioBytes, string text, float estimatedDuration = 0)
    {
        try
        {
            if (audioSource == null)
            {
                Debug.LogError("AudioSource is null. Cannot play audio.");
                return;
            }
            
            // Save audio to temporary file with MP3 extension
            string tempFileName = "frida_response_" + DateTime.Now.Ticks + ".mp3";
            string tempPath = Path.Combine(Application.temporaryCachePath, tempFileName);
            
            Debug.Log($"Saving audio to temporary file: {tempPath}");
            File.WriteAllBytes(tempPath, audioBytes);
            
            // Start coroutine to load and play the audio
            StartCoroutine(LoadAndPlayAudioFromPath(tempPath, text));
            
            // Log the text response for debugging
            Debug.Log($"Playing response text: {text}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error playing audio from bytes: {e.Message}");
        }
    }
    
    private IEnumerator LoadAndPlayAudioFromPath(string filePath, string text)
    {
        Debug.Log($"Loading audio from path: {filePath}");
        
        // If audioSource is missing, don't continue
        if (audioSource == null)
        {
            Debug.LogError("AudioSource is null. Cannot load audio.");
            yield break;
        }
        
        // Load as MP3 first since that's what we know we're getting from OpenAI TTS
        UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.MPEG);
        yield return www.SendWebRequest();
        
        bool success = false;
        
        try
        {
            if (www.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                if (clip != null && clip.length > 0)
                {
                    Debug.Log($"MP3 audio loaded successfully. Length: {clip.length}s, Samples: {clip.samples}, Channels: {clip.channels}");
                    audioSource.clip = clip;
                    audioSource.Play();
                    
                    // Try to set up SALSA lip sync with the new audio clip
                    SetupSalsaLipSync(text, clip.length, null);
                    
                    success = true;
                }
                else
                {
                    Debug.LogWarning("Audio clip is null or empty");
                }
            }
            else
            {
                Debug.LogWarning($"Failed to load MP3 audio: {www.error}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error playing MP3 audio: {e.Message}");
        }
        finally
        {
            www.Dispose();
        }
        
        // If MP3 failed, try WAV as fallback
        if (!success)
        {
            Debug.Log("Trying WAV format as fallback...");
            www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.WAV);
            yield return www.SendWebRequest();
            
            try
            {
                if (www.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                    if (clip != null && clip.length > 0)
                    {
                        Debug.Log($"WAV audio loaded as fallback. Length: {clip.length}s");
                        audioSource.clip = clip;
                        audioSource.Play();
                        
                        // Try to set up SALSA lip sync with the fallback audio
                        SetupSalsaLipSync(text, clip.length, null);
                        
                        success = true;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error with WAV fallback: {e.Message}");
            }
            finally
            {
                www.Dispose();
            }
        }
        
        // Clean up the temp file after a delay
        if (File.Exists(filePath))
        {
            yield return new WaitForSeconds(1.0f);
            try
            {
                File.Delete(filePath);
                Debug.Log($"Deleted temporary audio file: {filePath}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not delete temp file: {e.Message}");
            }
        }
        
        if (!success)
        {
            Debug.LogWarning("Audio playback failed, but text response is still available");
        }
    }
    
    private void PlayAudioFromFile(string filePath, string text)
    {
        try
        {
            if (audioSource == null)
            {
                Debug.LogError("AudioSource is null. Cannot play audio.");
                return;
            }
            
            // Start a coroutine to load the audio file
            StartCoroutine(LoadAndPlayAudio(filePath, text));
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error playing audio from file: {e.Message}");
        }
    }
    
    private IEnumerator LoadAndPlayAudio(string filePath, string text)
    {
        if (audioSource == null)
        {
            Debug.LogError("AudioSource is null. Cannot load and play audio.");
            yield break;
        }
        
        UnityWebRequest www = null;
        
        try
        {
            www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.WAV);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error creating web request in LoadAndPlayAudio: {e.Message}");
            yield break;
        }
        
        // Move yield outside of try-catch
        yield return www.SendWebRequest();
        
        try
        {
            if (www.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                audioSource.clip = clip;
                audioSource.Play();
                
                // Log the text
                Debug.Log("Response text: " + text);
            }
            else
            {
                Debug.LogError($"Failed to load audio file: {www.error}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error in LoadAndPlayAudio: {e.Message}");
        }
        finally
        {
            www.Dispose();
            
            // Clean up temp file
            try
            {
                if (File.Exists(filePath))
                {
                    // Wait a bit before deleting to ensure it's not in use
                    StartCoroutine(DelaySimpleFileDelete(filePath, 2.0f));
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error setting up file cleanup: {e.Message}");
            }
        }
    }
    
    // Simple file delete helper to avoid redundant methods
    private IEnumerator DelaySimpleFileDelete(string filePath, float delay)
    {
        yield return new WaitForSeconds(delay);
        
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Debug.Log($"Cleaned up file: {filePath}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error deleting file: {e.Message}");
        }
    }
    
    /// <summary>
    /// Sets up SALSA lip sync using reflection to support any SALSA version
    /// </summary>
    private void SetupSalsaLipSync(string text, float duration, PhonemeData[] phonemeData)
    {
        if (salsaComponent == null)
        {
            // No SALSA component assigned, nothing to do
            Debug.LogWarning("SALSA component is not assigned! Assign your character's SALSA component in the Inspector.");
            return;
        }
        
        try
        {
            // Get the current audio clip
            AudioClip currentClip = audioSource?.clip;
            if (currentClip == null)
            {
                Debug.LogWarning("No audio clip available for SALSA");
                return;
            }
            
            // Get component type info for reflection
            Type salsaType = salsaComponent.GetType();
            string salsaTypeName = salsaType.Name;
            
            Debug.Log($"Setting up SALSA lip sync using component type: {salsaTypeName}");
            
            // Try to find the appropriate method to set the audio clip
            bool setupSuccessful = false;
            
            // First try: SetAudioClip method
            var setAudioClipMethod = salsaType.GetMethod("SetAudioClip", 
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null, new Type[] { typeof(AudioClip) }, null);
                
            if (setAudioClipMethod != null)
            {
                setAudioClipMethod.Invoke(salsaComponent, new object[] { currentClip });
                Debug.Log($"Set audio clip via SetAudioClip method");
                setupSuccessful = true;
            }
            // Second try: SetAudioSource method
            else
            {
                var setAudioSourceMethod = salsaType.GetMethod("SetAudioSource",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                    null, new Type[] { typeof(AudioSource) }, null);
                    
                if (setAudioSourceMethod != null)
                {
                    setAudioSourceMethod.Invoke(salsaComponent, new object[] { audioSource });
                    Debug.Log($"Set audio source via SetAudioSource method");
                    setupSuccessful = true;
                }
            }
            
            // Now try to find and call the Play or equivalent method
            bool playSuccessful = false;
            
            // Try common method names in order of likelihood
            string[] playMethodNames = new string[] { "Play", "StartAnalyzing", "Process", "DoUpdate", "Update" };
            
            foreach (string methodName in playMethodNames)
            {
                var playMethod = salsaType.GetMethod(methodName,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                    null, Type.EmptyTypes, null);
                    
                if (playMethod != null)
                {
                    playMethod.Invoke(salsaComponent, null);
                    Debug.Log($"Called {methodName}() method to start SALSA processing");
                    playSuccessful = true;
                    break;
                }
            }
            
            // Log final status
            if (setupSuccessful && playSuccessful)
            {
                Debug.Log("SALSA lip sync successfully set up!");
            }
            else if (setupSuccessful)
            {
                Debug.LogWarning("Set up audio for SALSA but couldn't find Play method - lip sync might not start");
            }
            else
            {
                Debug.LogError("Failed to set up SALSA lip sync - no compatible methods found");
                
                // Print available methods for debugging
                System.Reflection.MethodInfo[] methods = salsaType.GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                
                Debug.Log($"Available methods on {salsaTypeName} ({methods.Length} total):");
                foreach (var method in methods.Where(m => !m.IsSpecialName).Take(15))
                {
                    string parameters = string.Join(", ", 
                        method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Debug.Log($"  - {method.ReturnType.Name} {method.Name}({parameters})");
                }
                
                // Also check for common SALSA properties
                string[] commonProperties = new string[] { "audioSource", "clip", "audioClip" };
                var properties = salsaType.GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                
                Debug.Log($"Properties that might be useful:");
                foreach (var prop in properties.Where(p => commonProperties.Contains(p.Name.ToLower())))
                {
                    Debug.Log($"  - {prop.PropertyType.Name} {prop.Name}");
                }
                
                Debug.LogWarning("IMPORTANT: Make sure the SALSA component on your character is properly set up and compatible with this code.");
                Debug.LogWarning("Check the SALSA documentation for your specific version of SALSA.");
            }
        }
        catch (System.Exception e)
        {
            // Safe error handling, won't break the app
            Debug.LogError($"Error setting up SALSA lip sync: {e.Message}\n{e.StackTrace}");
        }
    }
    
    private void EndSession()
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            StartCoroutine(EndSessionCoroutine());
        }
    }
    
    private IEnumerator EndSessionCoroutine()
    {
        string url = $"{serverUrl}/end_session";
        
        // Create the request body using Unity's JsonUtility
        SessionRequestData requestData = new SessionRequestData
        {
            session_id = sessionId
        };
        
        string jsonData = JsonUtility.ToJson(requestData);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
        
        UnityWebRequest www = null;
        
        try
        {
            www = new UnityWebRequest(url, "POST");
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error creating web request in EndSessionCoroutine: {e.Message}");
            yield break;
        }
        
        // Move yield outside of try-catch
        yield return www.SendWebRequest();
        
        try
        {
            if (www.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("Session ended successfully");
                sessionId = null;
            }
            else
            {
                Debug.LogError($"Failed to end session: {www.error}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error processing end session response: {e.Message}");
        }
        finally
        {
            www.Dispose();
        }
    }
    
    // Convert Unity AudioClip to WAV format
    private byte[] AudioClipToWav(AudioClip clip)
    {
        // Get audio data
        float[] samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);
        
        // Convert to 16-bit PCM
        int sampleRate = clip.frequency;
        int channels = clip.channels;
        
        using (MemoryStream stream = new MemoryStream())
        {
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                // RIFF header
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + samples.Length * 2); // File size
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                
                // Format chunk
                writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16); // Chunk size
                writer.Write((short)1); // Audio format (PCM)
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * 2); // Byte rate
                writer.Write((short)(channels * 2)); // Block align
                writer.Write((short)16); // Bits per sample
                
                // Data chunk
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(samples.Length * 2); // Chunk size
                
                // Convert float samples to 16-bit
                foreach (float sample in samples)
                {
                    writer.Write((short)(sample * 32767));
                }
            }
            
            return stream.ToArray();
        }
    }
    
    void OnDestroy()
    {
        // End session when object is destroyed
        if (!string.IsNullOrEmpty(sessionId))
        {
            EndSession();
        }
        
        // Cancel any ongoing coroutines
        if (checkResponseCoroutine != null)
        {
            StopCoroutine(checkResponseCoroutine);
        }
        if (dynamicRecordingCoroutine != null)
        {
            StopCoroutine(dynamicRecordingCoroutine);
        }
    }
} 