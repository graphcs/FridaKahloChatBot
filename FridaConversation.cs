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
    [SerializeField] private Text responseText;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private int recordingDuration = 5;
    
    // SALSA lip sync support
    [Tooltip("Reference to the Salsa component on your character")]
    [SerializeField] private MonoBehaviour salsaComponent; // Change to Salsa3D when you've added SALSA
    
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
        // Initialize UI elements
        if (recordButton != null)
        {
            recordButton.onClick.AddListener(ToggleRecording);
        }
        
        if (endSessionButton != null)
        {
            endSessionButton.onClick.AddListener(EndSession);
        }
        
        // Initialize AudioSource if not set
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        
        // Start a new session
        StartCoroutine(StartSession());
    }
    
    private IEnumerator StartSession()
    {
        string url = $"{serverUrl}/start_session";
        
        using (UnityWebRequest www = UnityWebRequest.Post(url, ""))
        {
            yield return www.SendWebRequest();
            
            if (www.result == UnityWebRequest.Result.Success)
            {
                SessionResponse response = JsonUtility.FromJson<SessionResponse>(www.downloadHandler.text);
                sessionId = response.session_id;
                Debug.Log($"Session started with ID: {sessionId}");
                
                // Display welcome message
                if (responseText != null)
                {
                    responseText.text = response.welcome_text;
                }
                
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
        
        // Update UI - Add null check for Text component
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
            return null;
        }
        
        try
        {
            int sampleRate = 44100;
            recordingClip = Microphone.Start(null, false, 30, sampleRate); // Max 30 seconds
            
            if (recordingClip == null)
            {
                Debug.LogError("Failed to start microphone recording");
                isRecording = false;
                return null;
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
            
            // Update UI - Add null check for Text component
            if (recordButton != null)
            {
                Text buttonText = recordButton.GetComponentInChildren<Text>();
                if (buttonText != null)
                {
                    buttonText.text = "Processing...";
                }
            }
            
            // Convert AudioClip to WAV
            byte[] wavData = AudioClipToWav(recordingClip);
            
            // Send to server for transcription
            yield return StartCoroutine(TranscribeAudio(wavData));
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error during dynamic recording: {e.Message}");
        }
        
        isRecording = false;
        
        // Update UI - Add null check for Text component
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
        
        // Update UI - Add null check for Text component
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
            return null;
        }
        
        try
        {
            // Start recording
            recordingClip = Microphone.Start(null, false, recordingDuration, 44100);
            
            if (recordingClip == null)
            {
                Debug.LogError("Failed to start microphone recording");
                isRecording = false;
                return null;
            }
            
            Debug.Log($"Recording for {recordingDuration} seconds...");
            
            // Wait for the recording to complete
            yield return new WaitForSeconds(recordingDuration);
            
            // Stop recording
            Microphone.End(null);
            Debug.Log("Recording finished");
            
            // Update UI - Add null check for Text component
            if (recordButton != null)
            {
                Text buttonText = recordButton.GetComponentInChildren<Text>();
                if (buttonText != null)
                {
                    buttonText.text = "Processing...";
                }
            }
            
            // Convert AudioClip to WAV
            byte[] wavData = AudioClipToWav(recordingClip);
            
            // Send to server for transcription
            yield return StartCoroutine(TranscribeAudio(wavData));
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error during recording: {e.Message}");
        }
        
        isRecording = false;
        
        // Update UI - Add null check for Text component
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
        
        using (UnityWebRequest www = UnityWebRequest.Post(url, form))
        {
            yield return www.SendWebRequest();
            
            if (www.result == UnityWebRequest.Result.Success)
            {
                TranscriptionResponse response = JsonUtility.FromJson<TranscriptionResponse>(www.downloadHandler.text);
                string transcribedText = response.text;
                Debug.Log($"Transcription: {transcribedText}");
                
                // Play a filler immediately
                yield return StartCoroutine(PlayFiller());
                
                // Get Frida's response
                yield return StartCoroutine(GetFridaResponse(transcribedText));
            }
            else
            {
                Debug.LogError($"Transcription failed: {www.error}");
                if (responseText != null)
                {
                    responseText.text = "Error: Could not transcribe audio";
                }
            }
        }
    }
    
    private IEnumerator PlayFiller()
    {
        string url = $"{serverUrl}/get_filler";
        
        using (UnityWebRequest www = UnityWebRequest.Post(url, ""))
        {
            yield return www.SendWebRequest();
            
            if (www.result == UnityWebRequest.Result.Success)
            {
                FillerResponse response = JsonUtility.FromJson<FillerResponse>(www.downloadHandler.text);
                
                // Convert base64 to audio and play
                byte[] audioBytes = Convert.FromBase64String(response.audio_base64);
                PlayAudioFromBytes(audioBytes, response.text, response.estimated_duration);
            }
            else
            {
                Debug.LogError($"Failed to get filler: {www.error}");
            }
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
        
        using (UnityWebRequest www = new UnityWebRequest(url, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            
            yield return www.SendWebRequest();
            
            if (www.result == UnityWebRequest.Result.Success)
            {
                // Start checking for response completion
                checkResponseCoroutine = StartCoroutine(CheckResponseStatus());
            }
            else
            {
                Debug.LogError($"Failed to start response generation: {www.error}");
                if (responseText != null)
                {
                    responseText.text = "Error: Could not get Frida's response";
                }
                isProcessingResponse = false;
                isWaitingForResponse = false;
            }
        }
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
            using (UnityWebRequest www = new UnityWebRequest(url, "POST"))
            {
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                
                yield return www.SendWebRequest();
                
                if (www.result == UnityWebRequest.Result.Success)
                {
                    StatusResponse response = JsonUtility.FromJson<StatusResponse>(www.downloadHandler.text);
                    
                    if (response.completed)
                    {
                        isComplete = true;
                        
                        // Update UI with Frida's text response
                        if (responseText != null)
                        {
                            responseText.text = response.text;
                        }
                        
                        // Convert base64 to audio and play with SALSA lip sync data
                        byte[] audioBytes = Convert.FromBase64String(response.audio_base64);
                        PlayAudioFromBytes(audioBytes, response.text, response.duration, response.phoneme_data);
                        break;
                    }
                }
                else
                {
                    Debug.LogError($"Failed to check response status: {www.error}");
                    break;
                }
            }
            
            retryCount++;
            yield return new WaitForSeconds(0.5f); // Poll every half second
        }
        
        if (!isComplete)
        {
            Debug.LogWarning("Timed out waiting for response");
        }
        
        isProcessingResponse = false;
        isWaitingForResponse = false;
    }
    
    private void PlayAudioFromBytes(byte[] audioBytes, string text, float duration, PhonemeData[] phonemeData = null)
    {
        // Save to temporary file (Unity can't create AudioClip directly from MP3 bytes)
        string tempPath = Path.Combine(Application.temporaryCachePath, "frida_response.mp3");
        File.WriteAllBytes(tempPath, audioBytes);
        
        StartCoroutine(PlayAudioFromFile(tempPath, text, duration, phonemeData));
    }
    
    private IEnumerator PlayAudioFromFile(string filePath, string text, float duration, PhonemeData[] phonemeData = null)
    {
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.MPEG))
        {
            yield return www.SendWebRequest();
            
            if (www.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                audioSource.clip = clip;
                audioSource.Play();
                
                // Handle SALSA lip sync
                if (salsaComponent != null && phonemeData != null)
                {
                    SetupSalsaLipSync(text, duration, phonemeData);
                }
                
                // Clean up temp file after playing
                yield return new WaitForSeconds(clip.length);
                File.Delete(filePath);
            }
            else
            {
                Debug.LogError($"Error loading audio: {www.error}");
            }
        }
    }
    
    private void SetupSalsaLipSync(string text, float duration, PhonemeData[] phonemeData)
    {
        // This method would integrate with SALSA
        // The exact implementation depends on your SALSA version and setup
        
        /* Example SALSA integration - uncomment and adapt when you have SALSA
        
        // Assuming salsaComponent is a Salsa3D instance
        Salsa3D salsa = salsaComponent as Salsa3D;
        if (salsa != null)
        {
            // Set the audio clip the same as our AudioSource
            salsa.SetAudioClip(audioSource.clip);
            
            // If you have advanced viseme controls, you can use the phoneme data
            // to drive more precise lip sync by creating a custom SalsaVisemeMap
            
            // For advanced usage with phoneme data:
            foreach (PhonemeData phoneme in phonemeData)
            {
                // Map word to appropriate viseme
                // This would require converting words to phonemes & then to visemes
                float startTime = phoneme.start_time;
                float endTime = phoneme.end_time;
                string word = phoneme.word;
                
                // Advanced integration would go here
            }
            
            // Start lip sync
            salsa.Play();
        }
        */
        
        Debug.Log($"SALSA lip sync would be performed here for: {text} with duration: {duration}");
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
        
        using (UnityWebRequest www = new UnityWebRequest(url, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            
            yield return www.SendWebRequest();
            
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