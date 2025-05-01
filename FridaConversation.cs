using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using System.Text;
using System.IO;
using Newtonsoft.Json;

public class FridaConversation : MonoBehaviour
{
    [SerializeField] private string serverUrl = "http://localhost:5001";
    [SerializeField] private Button recordButton;
    [SerializeField] private Button endSessionButton;
    [SerializeField] private Text responseText;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private int recordingDuration = 5;
    
    private string sessionId;
    private bool isRecording = false;
    private AudioClip recordingClip;
    
    // Structure for API responses
    [Serializable]
    private class SessionResponse
    {
        public string session_id;
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
            }
            else
            {
                Debug.LogError($"Failed to start session: {www.error}");
            }
        }
    }
    
    public void ToggleRecording()
    {
        if (!isRecording)
        {
            StartCoroutine(RecordAndProcess());
        }
    }
    
    private IEnumerator RecordAndProcess()
    {
        isRecording = true;
        
        // Update UI
        if (recordButton != null)
        {
            recordButton.GetComponentInChildren<Text>().text = "Recording...";
        }
        
        // Start recording
        recordingClip = Microphone.Start(null, false, recordingDuration, 44100);
        Debug.Log($"Recording for {recordingDuration} seconds...");
        
        // Wait for the recording to complete
        yield return new WaitForSeconds(recordingDuration);
        
        // Stop recording
        Microphone.End(null);
        Debug.Log("Recording finished");
        
        // Update UI
        if (recordButton != null)
        {
            recordButton.GetComponentInChildren<Text>().text = "Processing...";
        }
        
        // Convert AudioClip to WAV
        byte[] wavData = AudioClipToWav(recordingClip);
        
        // Send to server for transcription
        yield return StartCoroutine(TranscribeAudio(wavData));
        
        isRecording = false;
        
        // Update UI
        if (recordButton != null)
        {
            recordButton.GetComponentInChildren<Text>().text = "Record";
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
    
    private IEnumerator GetFridaResponse(string userText)
    {
        string url = $"{serverUrl}/get_response";
        
        // Create the request body
        Dictionary<string, string> requestData = new Dictionary<string, string>
        {
            { "text", userText },
            { "session_id", sessionId }
        };
        
        string jsonData = JsonConvert.SerializeObject(requestData);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
        
        using (UnityWebRequest www = new UnityWebRequest(url, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            
            yield return www.SendWebRequest();
            
            if (www.result == UnityWebRequest.Result.Success)
            {
                // Parse response
                FridaResponse response = JsonUtility.FromJson<FridaResponse>(www.downloadHandler.text);
                
                // Update UI with Frida's text response
                if (responseText != null)
                {
                    responseText.text = response.text;
                }
                
                // Convert base64 to audio and play
                byte[] audioBytes = Convert.FromBase64String(response.audio_base64);
                PlayAudioFromBytes(audioBytes);
            }
            else
            {
                Debug.LogError($"Failed to get Frida's response: {www.error}");
                if (responseText != null)
                {
                    responseText.text = "Error: Could not get Frida's response";
                }
            }
        }
    }
    
    private void PlayAudioFromBytes(byte[] audioBytes)
    {
        // Save to temporary file (Unity can't create AudioClip directly from MP3 bytes)
        string tempPath = Path.Combine(Application.temporaryCachePath, "frida_response.mp3");
        File.WriteAllBytes(tempPath, audioBytes);
        
        StartCoroutine(PlayAudioFromFile(tempPath));
    }
    
    private IEnumerator PlayAudioFromFile(string filePath)
    {
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.MPEG))
        {
            yield return www.SendWebRequest();
            
            if (www.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                audioSource.clip = clip;
                audioSource.Play();
                
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
        
        Dictionary<string, string> requestData = new Dictionary<string, string>
        {
            { "session_id", sessionId }
        };
        
        string jsonData = JsonConvert.SerializeObject(requestData);
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
    }
} 