using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using System.Text;
using System.IO;
// Replace Newtonsoft.Json with Unity's built-in SimpleJSON
// using Newtonsoft.Json;

// Add this if you have SALSA in your project
// using CrazyMinnow.SALSA;

public class FridaConversation : MonoBehaviour
{
    [SerializeField] private string serverUrl = "http://localhost:5001";
    [SerializeField] private Button recordButton;
    [SerializeField] private Button endSessionButton;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private int recordingDuration = 5;
    
    // SALSA lip sync support - commented out to avoid errors
    /*
    [Tooltip("Reference to the Salsa component on your character")]
    [SerializeField] private MonoBehaviour salsaComponent; // Change to Salsa3D when you've added SALSA
    */
    
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
                        
                        // Log Frida's text response
                        Debug.Log("Frida response: " + response.text);
                        
                        // Check if the server supports direct audio download
                        if (sessionId != null)
                        {
                            // Try direct audio download first
                            StartCoroutine(DownloadAudioDirectly(sessionId, response.text, response.duration));
                        }
                        // Fall back to base64 if direct download not available or fails
                        else if (!string.IsNullOrEmpty(response.audio_base64))
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
                                
                                // Try direct playback first to avoid any file system issues
                                StartCoroutine(TryDirectAudioPlayback(audioBytes, response.text, response.duration));
                            }
                            catch (System.Exception e) {
                                Debug.LogError($"Error processing audio data: {e.Message}");
                            }
                        }
                        else
                        {
                            Debug.LogWarning("No audio received in response");
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
    
    private IEnumerator DownloadAudioDirectly(string sessionId, string text, float duration)
    {
        // Build URL to download audio directly as MP3 instead of base64
        string directAudioUrl = $"{serverUrl}/get_audio?session_id={sessionId}";
        Debug.Log($"Attempting to download audio directly from: {directAudioUrl}");
        
        // Create request using DownloadHandlerAudioClip to handle audio directly
        UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(directAudioUrl, AudioType.MPEG);
        
        yield return www.SendWebRequest();
        
        if (www.result == UnityWebRequest.Result.Success)
        {
            try
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                if (clip != null)
                {
                    Debug.Log($"Successfully downloaded audio directly! Samples: {clip.samples}, Frequency: {clip.frequency}, Channels: {clip.channels}");
                    audioSource.clip = clip;
                    audioSource.Play();
                    
                    // Also try SALSA lip sync if available
                    // SetupSalsaLipSync(text, duration, null);  // Commented out to avoid SALSA errors
                }
                else
                {
                    Debug.LogError("Downloaded audio clip is null");
                    // Try alternative methods
                    StartCoroutine(FallbackGetResponseAudio(sessionId, text, duration));
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error playing direct downloaded audio: {e.Message}");
                // Try alternative methods
                StartCoroutine(FallbackGetResponseAudio(sessionId, text, duration));
            }
        }
        else
        {
            Debug.LogError($"Failed to download audio directly: {www.error}");
            // Try alternative methods
            StartCoroutine(FallbackGetResponseAudio(sessionId, text, duration));
        }
        
        www.Dispose();
    }
    
    private IEnumerator FallbackGetResponseAudio(string sessionId, string text, float duration)
    {
        // This is a fallback method to try to get the audio in a simpler format like WAV
        string url = $"{serverUrl}/get_response_audio";
        
        // Create the request body
        string jsonData = JsonUtility.ToJson(new SessionRequestData { session_id = sessionId });
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
        
        UnityWebRequest www = new UnityWebRequest(url, "POST");
        www.uploadHandler = new UploadHandlerRaw(bodyRaw);
        www.downloadHandler = new DownloadHandlerBuffer();
        www.SetRequestHeader("Content-Type", "application/json");
        
        yield return www.SendWebRequest();
        
        if (www.result == UnityWebRequest.Result.Success)
        {
            // Try to get a direct audio URL from response
            if (www.downloadHandler.text.Contains("audio_url"))
            {
                try
                {
                    // Parse response to get direct URL
                    string audioUrl = www.downloadHandler.text.Replace("\"", "").Replace("{", "").Replace("}", "").Split(':')[1].Trim();
                    
                    if (!string.IsNullOrEmpty(audioUrl))
                    {
                        Debug.Log($"Got fallback audio URL: {audioUrl}");
                        
                        // Download the audio file - moved outside try block
                        StartCoroutine(DownloadAndPlayAudioFromURL(audioUrl, text));
                    }
                    else
                    {
                        Debug.LogError("Empty audio URL in fallback response");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error parsing audio URL: {e.Message}");
                }
            }
            else
            {
                Debug.LogError("No audio URL found in fallback response");
            }
        }
        else
        {
            Debug.LogError($"Failed to get fallback response: {www.error}");
        }
        
        www.Dispose();
    }
    
    private IEnumerator DownloadAndPlayAudioFromURL(string audioUrl, string text)
    {
        // Download the audio file
        UnityWebRequest audioWww = UnityWebRequestMultimedia.GetAudioClip(audioUrl, AudioType.MPEG);
        yield return audioWww.SendWebRequest();
        
        try 
        {
            if (audioWww.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(audioWww);
                if (clip != null)
                {
                    audioSource.clip = clip;
                    audioSource.Play();
                    Debug.Log("Successfully played fallback audio!");
                }
                else
                {
                    Debug.LogError("Downloaded audio clip is null");
                }
            }
            else
            {
                Debug.LogError($"Failed to get fallback audio: {audioWww.error}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error playing fallback audio: {e.Message}");
        }
        finally 
        {
            audioWww.Dispose();
        }
    }
    
    private IEnumerator TryDirectAudioPlayback(byte[] audioBytes, string text, float duration)
    {
        Debug.Log("Attempting direct audio playback from memory...");
        
        // Create a direct request with the audio data
        UnityWebRequest www = new UnityWebRequest();
        www.url = "data:audio/mp3;base64," + Convert.ToBase64String(audioBytes);
        www.downloadHandler = new DownloadHandlerAudioClip(www.url, AudioType.MPEG);
        www.method = UnityWebRequest.kHttpVerbGET;
        
        // Send the request
        yield return www.SendWebRequest();
        
        bool success = false;
        
        if (www.result == UnityWebRequest.Result.Success)
        {
            try
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                if (clip != null)
                {
                    audioSource.clip = clip;
                    audioSource.Play();
                    Debug.Log("Successfully played audio directly from memory!");
                    success = true;
                }
                else
                {
                    Debug.LogError("Downloaded audio clip is null");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error playing direct audio: {e.Message}");
            }
        }
        else
        {
            Debug.LogError($"Direct audio request failed: {www.error}");
        }
        
        www.Dispose();
        
        // If direct approach failed, fall back to file-based method
        if (!success)
        {
            Debug.Log("Direct playback failed, falling back to file-based method");
            PlayAudioFromBytes(audioBytes, text, duration);
        }
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

            // First, try to save with specific MP3 header structure
            try 
            {
                // Need to ensure this is a proper MP3 file - check if it's missing an ID3 tag
                if (audioBytes.Length > 2 && !(audioBytes[0] == 'I' && audioBytes[1] == 'D' && audioBytes[2] == '3') &&
                    !(audioBytes[0] == 0xFF && (audioBytes[1] & 0xE0) == 0xE0))
                {
                    Debug.Log("Audio doesn't appear to have a proper MP3 header, adding MP3 sync header");
                    
                    // Add a minimal MP3 sync header (0xFF 0xFB) for constant bitrate MP3
                    byte[] fixedMP3 = new byte[audioBytes.Length + 2];
                    fixedMP3[0] = 0xFF;  // Sync word
                    fixedMP3[1] = 0xFB;  // MPEG-1 Layer 3, no CRC
                    Buffer.BlockCopy(audioBytes, 0, fixedMP3, 2, audioBytes.Length);
                    audioBytes = fixedMP3;
                    
                    Debug.Log($"Fixed audio with MP3 header, new size: {audioBytes.Length} bytes");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error fixing MP3 header: {e.Message}");
            }

            // OpenAI TTS always returns MP3 format according to their documentation
            string fileExtension = ".mp3"; 
            
            Debug.Log($"Using file extension: {fileExtension} for audio data of size: {audioBytes.Length} bytes");

            // Create a temporary file with a unique name to avoid conflicts
            string tempFileName = "frida_audio_" + DateTime.Now.Ticks + fileExtension;
            string tempPath = Path.Combine(Application.temporaryCachePath, tempFileName);
            File.WriteAllBytes(tempPath, audioBytes);
            
            Debug.Log($"Saved audio to temporary file: {tempPath}");
            
            // Start coroutine to load and play the audio
            StartCoroutine(LoadAndPlayAudioFromPath(tempPath, text));
            
            // Log the text
            Debug.Log("Response text: " + text);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error playing audio from bytes: {e.Message}");
        }
    }
    
    private IEnumerator LoadAndPlayAudioFromPath(string filePath, string text)
    {
        Debug.Log($"Loading audio from path: {filePath}");
        
        // First try as MP3 since OpenAI TTS returns MP3 format
        UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.MPEG);
        Debug.Log("Attempting to load as MP3...");
        yield return www.SendWebRequest();
        
        if (www.result == UnityWebRequest.Result.Success)
        {
            try
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                Debug.Log($"MP3 audio loaded successfully. Samples: {clip.samples}, Frequency: {clip.frequency}, Channels: {clip.channels}");
                audioSource.clip = clip;
                audioSource.Play();
                www.Dispose();
                
                // Clean up file after successful playback
                StartCoroutine(DelayedFileDelete(filePath, 5.0f)); // Longer delay to ensure playback completes
                yield break;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error processing MP3 content: {e.Message}");
                www.Dispose();
                // Continue to try other formats
            }
        }
        else
        {
            Debug.LogError($"Failed to load as MP3: {www.error}. ResponseCode: {www.responseCode}");
            Debug.LogError($"Detailed error: {www.downloadHandler?.error ?? "No detailed error"}");
            www.Dispose();
        }
        
        // Try as WAV
        www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.WAV);
        Debug.Log("Attempting to load as WAV...");
        yield return www.SendWebRequest();
        
        if (www.result == UnityWebRequest.Result.Success)
        {
            try
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                Debug.Log($"WAV audio loaded successfully. Samples: {clip.samples}, Frequency: {clip.frequency}, Channels: {clip.channels}");
                audioSource.clip = clip;
                audioSource.Play();
                www.Dispose();
                
                // Clean up file after successful playback
                StartCoroutine(DelayedFileDelete(filePath, 5.0f));
                yield break;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error processing WAV content: {e.Message}");
                www.Dispose();
                // Continue to try other formats
            }
        }
        else
        {
            Debug.LogError($"Failed to load as WAV: {www.error}");
            www.Dispose();
        }
        
        // Try as OGG
        www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.OGGVORBIS);
        Debug.Log("Attempting to load as OGG...");
        yield return www.SendWebRequest();
        
        if (www.result == UnityWebRequest.Result.Success)
        {
            try
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                Debug.Log($"OGG audio loaded successfully. Samples: {clip.samples}, Frequency: {clip.frequency}, Channels: {clip.channels}");
                audioSource.clip = clip;
                audioSource.Play();
                www.Dispose();
                
                // Clean up file after successful playback
                StartCoroutine(DelayedFileDelete(filePath, 5.0f));
                yield break;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error processing OGG content: {e.Message}");
                www.Dispose();
                // Continue to fallback
            }
        }
        else
        {
            Debug.LogError($"Failed to load as OGG: {www.error}");
            www.Dispose();
        }
        
        Debug.LogError("Failed to load audio with any format, trying alternative playback method...");
        
        // Fallback method - try direct file loading
        StartCoroutine(TryDirectFilePlayback(filePath));
    }
    
    private IEnumerator TryDirectFilePlayback(string filePath)
    {
        Debug.Log("Attempting direct file playback...");
        
        // First verify the file exists
        if (!File.Exists(filePath))
        {
            Debug.LogError($"Audio file does not exist: {filePath}");
            yield break;
        }
        
        byte[] audioBytes = null;
        string hexHeader = null;
        
        // Read file and gather diagnostics - keep try/catch blocks small with no yields
        try
        {
            // Read the raw bytes for diagnostics
            audioBytes = File.ReadAllBytes(filePath);
            Debug.Log($"Raw audio file size: {audioBytes.Length} bytes");
            
            // Log the first few bytes for format identification
            if (audioBytes.Length > 16)
            {
                hexHeader = BitConverter.ToString(audioBytes, 0, 16).Replace("-", " ");
                Debug.Log($"File header bytes: {hexHeader}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error reading audio file: {e.Message}");
            
            // Try to clean up before exiting
            try 
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch {}
            
            yield break;
        }
        
        // Now that we've read the file safely, try to decode it - no yields in try blocks
        StartCoroutine(TryDecodeAudioDirectly(filePath));
        
        // Wait a bit to let the decoding attempt finish before continuing
        yield return new WaitForSeconds(2.0f);
    }
    
    private IEnumerator TryDecodeAudioDirectly(string filePath)
    {
        byte[] audioData = null;
        
        try
        {
            // Read the audio data from the temp file
            audioData = File.ReadAllBytes(filePath);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error reading audio file: {e.Message}");
            yield break;
        }
        
        // Check if this actually contains OGG data
        if (audioData.Length > 4 && audioData[0] == 'O' && audioData[1] == 'g' && audioData[2] == 'g' && audioData[3] == 'S')
        {
            // Try loading as OGG instead
            UnityWebRequest www = null;
            
            try
            {
                www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.OGGVORBIS);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error creating OGG web request: {e.Message}");
                yield break;
            }
            
            // Move yield outside of try-catch
            yield return www.SendWebRequest();
            
            bool success = false;
            
            try
            {
                if (www.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                    audioSource.clip = clip;
                    audioSource.Play();
                    success = true;
                }
                else
                {
                    Debug.LogError($"Failed to load audio as OGG: {www.error}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error processing OGG audio: {e.Message}");
            }
            finally
            {
                www.Dispose();
            }
            
            if (success)
            {
                yield break;
            }
        }
        
        try
        {
            // If we get here, we need to try parsing as WAV manually
            // Skip WAV header (44 bytes) to get to PCM data
            int headerSize = 44;
            if (audioData.Length <= headerSize)
            {
                Debug.LogError("Audio data too short to be valid WAV");
                yield break;
            }
            
            // Extract sample rate from WAV header (bytes 24-27)
            int sampleRate = audioData[24] | (audioData[25] << 8) | (audioData[26] << 16) | (audioData[27] << 24);
            
            // Extract number of channels from WAV header (bytes 22-23)
            int channels = audioData[22] | (audioData[23] << 8);
            
            // Get audio format (bytes 20-21), 1 = PCM
            int audioFormat = audioData[20] | (audioData[21] << 8);
            
            // Get bits per sample (bytes 34-35)
            int bitsPerSample = audioData[34] | (audioData[35] << 8);
            
            Debug.Log($"WAV info: Format={audioFormat}, Channels={channels}, Rate={sampleRate}, BitsPerSample={bitsPerSample}");
            
            if (audioFormat != 1)
            {
                Debug.LogError("Only PCM WAV format is supported for direct decoding");
                yield break;
            }
            
            // Calculate number of samples
            int bytesPerSample = bitsPerSample / 8;
            int numSamples = (audioData.Length - headerSize) / (bytesPerSample * channels);
            
            // Create audio clip
            AudioClip clip = AudioClip.Create("Speech", numSamples, channels, sampleRate, false);
            
            // Convert PCM data to float samples
            float[] samples = new float[numSamples * channels];
            int sampleIndex = 0;
            
            for (int i = headerSize; i < audioData.Length; i += bytesPerSample)
            {
                if (sampleIndex >= samples.Length) break;
                
                if (bitsPerSample == 16)
                {
                    // 16-bit samples
                    short sample = (short)((audioData[i+1] << 8) | audioData[i]);
                    samples[sampleIndex++] = sample / 32768.0f;
                }
                else if (bitsPerSample == 8)
                {
                    // 8-bit samples
                    samples[sampleIndex++] = (audioData[i] - 128) / 128.0f;
                }
            }
            
            // Set the audio data and play
            clip.SetData(samples, 0);
            audioSource.clip = clip;
            audioSource.Play();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error in direct audio decoding: {e.Message}");
        }
        
        // Finally, clean up the temp file
        try
        {
            if (File.Exists(filePath))
            {
                // Use the DelayedFileDelete coroutine instead of yielding within try block
                StartCoroutine(DelayedFileDelete(filePath, 2.0f));
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error cleaning up temp file: {e.Message}");
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
                    StartCoroutine(DelayedFileDelete(filePath, 2.0f));
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error setting up file cleanup: {e.Message}");
            }
        }
    }
    
    private IEnumerator DelayedFileDelete(string filePath, float delay)
    {
        yield return new WaitForSeconds(delay);
        
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Debug.Log($"Cleaned up temporary file: {filePath}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error deleting file: {e.Message}");
        }
    }
    
    /* Commented out SALSA integration to avoid errors
    private void SetupSalsaLipSync(string text, float duration, PhonemeData[] phonemeData)
    {
        // This method would integrate with SALSA
        // The exact implementation depends on your SALSA version and setup
        
        // Example SALSA integration - uncomment and adapt when you have SALSA
        
        // Assuming salsaComponent is a Salsa3D instance
        // Salsa3D salsa = salsaComponent as Salsa3D;
        // if (salsa != null)
        // {
        //     // Set the audio clip the same as our AudioSource
        //     salsa.SetAudioClip(audioSource.clip);
        //     
        //     // If you have advanced viseme controls, you can use the phoneme data
        //     // to drive more precise lip sync by creating a custom SalsaVisemeMap
        //     
        //     // For advanced usage with phoneme data:
        //     foreach (PhonemeData phoneme in phonemeData)
        //     {
        //         // Map word to appropriate viseme
        //         // This would require converting words to phonemes & then to visemes
        //         float startTime = phoneme.start_time;
        //         float endTime = phoneme.end_time;
        //         string word = phoneme.word;
        //         
        //         // Advanced integration would go here
        //     }
        //     
        //     // Start lip sync
        //     salsa.Play();
        // }
        
        Debug.Log($"SALSA lip sync would be performed here for: {text} with duration: {duration}");
    }
    */
    
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