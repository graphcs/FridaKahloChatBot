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
    
    // Microphone and listening settings
    [Tooltip("Whether to use dynamic listening (stops when silence is detected) versus fixed-duration recording")]
    [SerializeField] private bool useDynamicListening = true;
    [Tooltip("Whether to continuously listen for speech without requiring button presses")]
    [SerializeField] private bool useAutomaticListening = true;
    [Tooltip("Audio level threshold below which audio is considered silence. Lower values (0.005-0.01) make it more sensitive, higher values (0.02-0.05) require louder speech.")]
    [SerializeField] private float silenceThreshold = 0.005f;
    [Tooltip("How long (in seconds) to wait for silence before ending recording. Increase this if users need more time to think between sentences.")]
    [SerializeField] private float silenceTimeToStop = 5.0f;
    [Tooltip("How often to check for new speech in automatic mode (seconds)")]
    [SerializeField] private float listeningCheckInterval = 0.1f;
    [Tooltip("Duration of audio buffer to keep before speech is detected (seconds). Helps prevent cutting off the start of sentences.")]
    [SerializeField] private float preBufferDuration = 0.5f;
    [Tooltip("Time to wait after Frida finishes speaking before resuming listening (seconds). Helps prevent echo detection.")]
    [SerializeField] private float postSpeechWaitTime = 2.0f;
    
    [Tooltip("Maximum recording duration in seconds, even if silence isn't detected")]
    [SerializeField] private float maxRecordingDuration = 30.0f;
    
    [Tooltip("Whether to log audio levels for threshold tuning")]
    [SerializeField] private bool debugAudioLevels = false;
    
    private string sessionId;
    private bool isRecording = false;
    private AudioClip recordingClip;
    private bool isProcessingResponse = false;
    private bool isWaitingForResponse = false;
    private Coroutine checkResponseCoroutine;
    private Coroutine dynamicRecordingCoroutine;
    private Coroutine automaticListeningCoroutine;
    private bool isListening = false;
    private bool shouldStopListening = false;
    private bool isSpeaking = false;
    private Coroutine speakingMonitorCoroutine;
    
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
                
                // If using automatic listening, update the button text
                if (useAutomaticListening)
                {
                    Text buttonText = recordButton.GetComponentInChildren<Text>();
                    if (buttonText != null)
                    {
                        buttonText.text = "Pause Listening";
                    }
                }
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
            
            // Start a new session - we'll start automatic listening after welcome message finishes
            StartCoroutine(StartSession());
            
            // NOTE: We no longer start automatic listening here - it will be started after welcome
            // message in StartSession method
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
        
        bool sessionStarted = false;
        SessionResponse response = null;
        byte[] welcomeAudioBytes = null;
        string welcomeText = null;
        float welcomeDuration = 0;
        
        try
        {
            if (www.result == UnityWebRequest.Result.Success)
            {
                response = JsonUtility.FromJson<SessionResponse>(www.downloadHandler.text);
                sessionId = response.session_id;
                welcomeText = response.welcome_text;
                welcomeDuration = response.estimated_duration;
                Debug.Log($"Session started with ID: {sessionId}");
                
                // Enhanced logging of welcome message
                Debug.Log("=========================================");
                Debug.Log($"FRIDA WELCOME: \"{welcomeText}\"");
                Debug.Log("=========================================");
                
                // Extract welcome audio if available
                if (!string.IsNullOrEmpty(response.welcome_audio))
                {
                    welcomeAudioBytes = Convert.FromBase64String(response.welcome_audio);
                }
                
                sessionStarted = true;
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
        
        // After successfully processing the response, play welcome audio and continue
        if (sessionStarted)
        {
            // Mark that we're speaking to prevent automatic listening from starting
            isSpeaking = true;
            
            // Play welcome audio if available - outside try block
            if (welcomeAudioBytes != null)
            {
                // Play the welcome audio - this does NOT start automatic listening
                yield return StartCoroutine(PlayWelcomeAudio(welcomeAudioBytes, welcomeText, welcomeDuration));
            }
            
            // Wait a bit after welcome message before starting to listen - outside try block
            yield return new WaitForSeconds(postSpeechWaitTime);
            
            // No longer speaking
            isSpeaking = false;
            
            // Start automatic listening if enabled (after welcome audio is done)
            if (useAutomaticListening)
            {
                StartAutomaticListening();
            }
        }
        else
        {
            // Start automatic listening even if there was an error, to ensure functionality
            if (useAutomaticListening && !isListening)
            {
                StartAutomaticListening();
            }
        }
    }
    
    // Special version of audio playback for welcome message that doesn't trigger automatic listening
    private IEnumerator PlayWelcomeAudio(byte[] audioBytes, string text, float estimatedDuration = 0)
    {
        Debug.Log("==== STARTING WELCOME AUDIO PLAYBACK - SYSTEM IS IN SPEAKING MODE ====");
        
        // Make sure isSpeaking flag is set
        isSpeaking = true;
        
        if (audioSource == null)
        {
            Debug.LogError("AudioSource is null. Cannot play welcome audio.");
            yield break;
        }
        
        // Save audio to temporary file with MP3 extension
        string tempFileName = "frida_welcome_" + DateTime.Now.Ticks + ".mp3";
        string tempPath = Path.Combine(Application.temporaryCachePath, tempFileName);
        
        try
        {
            Debug.Log($"Saving welcome audio to temporary file: {tempPath}");
            File.WriteAllBytes(tempPath, audioBytes);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error saving welcome audio file: {e.Message}");
            yield break;
        }
        
        // Load as MP3 (what we expect from OpenAI TTS)
        UnityWebRequest www = null;
        
        try
        {
            www = UnityWebRequestMultimedia.GetAudioClip("file://" + tempPath, AudioType.MPEG);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error creating web request for welcome audio: {e.Message}");
            yield break;
        }
        
        // Move yield outside of try-catch
        yield return www.SendWebRequest();
        
        bool success = false;
        AudioClip clip = null;
        
        try
        {
            if (www.result == UnityWebRequest.Result.Success)
            {
                clip = DownloadHandlerAudioClip.GetContent(www);
                if (clip != null && clip.length > 0)
                {
                    Debug.Log($"Welcome MP3 audio loaded successfully. Length: {clip.length}s");
                    audioSource.clip = clip;
                    audioSource.Play();
                    
                    // Set up SALSA lip sync with the welcome audio
                    SetupSalsaLipSync(text, clip.length, null);
                    
                    success = true;
                }
                else
                {
                    Debug.LogWarning("Welcome audio clip is null or empty");
                }
            }
            else
            {
                Debug.LogWarning($"Failed to load welcome MP3 audio: {www.error}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error processing welcome MP3 audio: {e.Message}");
        }
        finally
        {
            www.Dispose();
        }
        
        // Wait for the audio to finish playing - outside try block
        if (success && clip != null)
        {
            // Wait until audio is actively playing
            yield return new WaitUntil(() => audioSource.isPlaying);
            
            float waitTime = clip.length + 0.2f; // Add a small buffer
            Debug.Log($"==== WELCOME MESSAGE IS PLAYING: Waiting {waitTime:F2}s for completion... ====");
            
            // Option 1: Wait for fixed duration
            yield return new WaitForSeconds(waitTime);
            
            // Option 2: Also check if it's still playing after that time
            if (audioSource.isPlaying)
            {
                Debug.Log("Audio is still playing after expected duration, waiting more...");
                yield return new WaitUntil(() => !audioSource.isPlaying);
            }
            
            Debug.Log("==== WELCOME MESSAGE PLAYBACK COMPLETED ====");
        }
        
        // Clean up the temp file after use
        if (File.Exists(tempPath))
        {
            try
            {
                File.Delete(tempPath);
                Debug.Log($"Deleted temporary welcome audio file: {tempPath}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not delete welcome temp file: {e.Message}");
            }
        }
        
        if (!success)
        {
            Debug.LogWarning("Welcome audio playback failed, but text is still available");
        }
        
        // Extra safety buffer wait at the end
        Debug.Log($"==== APPLYING ADDITIONAL {postSpeechWaitTime}s SAFETY BUFFER AFTER WELCOME AUDIO ====");
        yield return new WaitForSeconds(postSpeechWaitTime);
    }
    
    public void StartAutomaticListening()
    {
        if (automaticListeningCoroutine == null && !isListening && !isSpeaking)
        {
            shouldStopListening = false;
            automaticListeningCoroutine = StartCoroutine(AutomaticListeningLoop());
            isListening = true;
            
            // Update button text if available
            if (recordButton != null)
            {
                Text buttonText = recordButton.GetComponentInChildren<Text>();
                if (buttonText != null)
                {
                    buttonText.text = "Pause Listening";
                }
            }
            
            Debug.Log("Started automatic listening");
        }
    }
    
    public void StopAutomaticListening()
    {
        shouldStopListening = true;
        
        // Update button text if available
        if (recordButton != null)
        {
            Text buttonText = recordButton.GetComponentInChildren<Text>();
            if (buttonText != null)
            {
                buttonText.text = "Resume Listening";
            }
        }
        
        Debug.Log("Stopping automatic listening");
    }
    
    private void PauseListeningForSpeech()
    {
        Debug.Log("^^^ EXPLICITLY PAUSING LISTENING SYSTEM ^^^");
        
        // IMMEDIATELY set isSpeaking flag to prevent any new recordings
        isSpeaking = true;
        
        // First stop any ongoing recording
        if (isRecording && dynamicRecordingCoroutine != null)
        {
            Debug.Log("Stopping active recording because Frida is about to speak");
            StopCoroutine(dynamicRecordingCoroutine);
            dynamicRecordingCoroutine = null;
            isRecording = false;
            
            // Also end any ongoing microphone recording
            if (Microphone.IsRecording(null))
            {
                Debug.Log("Ending active microphone recording");
                Microphone.End(null);
            }
        }
        
        // Then stop the listening loop
        if (isListening && automaticListeningCoroutine != null)
        {
            Debug.Log("Stopping automatic listening loop while Frida speaks");
            StopCoroutine(automaticListeningCoroutine);
            automaticListeningCoroutine = null;
            
            // We're still technically "listening" in the sense that we'll resume
            // after speaking, so don't set isListening to false here
        }
        
        // Start monitoring when to resume listening
        if (speakingMonitorCoroutine != null)
        {
            StopCoroutine(speakingMonitorCoroutine);
        }
        speakingMonitorCoroutine = StartCoroutine(MonitorSpeechCompletionAndResume());
    }
    
    private IEnumerator MonitorSpeechCompletionAndResume()
    {
        Debug.Log(">>> PAUSED LISTENING: Frida is speaking <<<");
        
        // First make sure audio is actually playing
        if (audioSource != null && audioSource.clip != null) 
        {
            // Get the expected duration from the clip
            float expectedDuration = audioSource.clip.length;
            Debug.Log($">>> Audio duration: {expectedDuration:F2}s, waiting for completion... <<<");
            
            // Wait until audio is no longer playing
            while (audioSource.isPlaying)
            {
                yield return new WaitForSeconds(0.1f);
            }
            
            Debug.Log(">>> Audio playback has FINISHED <<<");
        }
        else
        {
            Debug.Log(">>> No audio playing, waiting default time <<<");
            // If no audio, just wait a default time
            yield return new WaitForSeconds(0.5f);
        }
        
        // Add extra delay AFTER audio has definitely stopped playing
        Debug.Log($">>> WAITING additional {postSpeechWaitTime:F1}s buffer before resuming listening <<<");
        yield return new WaitForSeconds(postSpeechWaitTime);
        
        // Reset speaking state BEFORE restarting listening
        isSpeaking = false;
        
        // Check if we should resume (user didn't manually stop listening during speech)
        if (isListening && automaticListeningCoroutine == null && !shouldStopListening)
        {
            Debug.Log(">>> RESUMING LISTENING: Speech finished + buffer time elapsed <<<");
            
            // Make sure isProcessingResponse and isWaitingForResponse are reset
            isProcessingResponse = false;
            isWaitingForResponse = false;
            
            // Double-check and cancel any existing listening coroutine to prevent duplicates
            if (automaticListeningCoroutine != null)
            {
                StopCoroutine(automaticListeningCoroutine);
            }
            
            // Start a new listening loop
            automaticListeningCoroutine = StartCoroutine(AutomaticListeningLoop());
        }
        else
        {
            Debug.LogWarning($">>> NOT resuming listening after Frida's speech: isListening={isListening}, automaticListeningCoroutine={(automaticListeningCoroutine==null ? "null" : "not null")}, shouldStopListening={shouldStopListening} <<<");
        }
        
        speakingMonitorCoroutine = null;
    }
    
    private IEnumerator AutomaticListeningLoop()
    {
        Debug.Log(">>> STARTING AUTOMATIC LISTENING LOOP <<<");
        
        // Set the listening state
        isListening = true;
        
        // Wait a moment for everything to initialize
        yield return new WaitForSeconds(0.5f);
        
        int loopIterations = 0;
        
        while (!shouldStopListening)
        {
            loopIterations++;
            
            // Log periodic status updates
            if (loopIterations % 10 == 0)
            {
                Debug.Log($">>> LISTENING LOOP ACTIVE (iteration {loopIterations}) <<<");
            }
            
            // Don't start a new recording if we're already processing something or speaking
            if (isRecording)
            {
                Debug.Log("Already recording, waiting for completion...");
                yield return new WaitForSeconds(0.5f);
                continue;
            }
            
            if (isProcessingResponse || isWaitingForResponse)
            {
                Debug.Log("Processing response, waiting to complete...");
                yield return new WaitForSeconds(0.5f);
                continue;
            }
            
            if (isSpeaking)
            {
                Debug.Log("Frida is speaking, waiting to finish...");
                yield return new WaitForSeconds(0.5f);
                continue;
            }
            
            // Check if we already have an active recording coroutine
            if (dynamicRecordingCoroutine != null)
            {
                Debug.LogWarning("Found existing dynamicRecordingCoroutine - stopping it before starting a new one");
                StopCoroutine(dynamicRecordingCoroutine);
                dynamicRecordingCoroutine = null;
            }
            
            Debug.Log(">>> STARTING TO LISTEN FOR SPEECH... <<<");
            dynamicRecordingCoroutine = StartCoroutine(DynamicRecordAndProcess());
            
            // Wait until the dynamic recording is complete
            while (isRecording)
            {
                yield return new WaitForSeconds(0.1f);
            }
            
            // Wait a moment before checking again
            yield return new WaitForSeconds(0.5f);
        }
        
        isListening = false;
        automaticListeningCoroutine = null;
        Debug.Log(">>> AUTOMATIC LISTENING LOOP ENDED <<<");
    }
    
    public void ToggleRecording()
    {
        if (useAutomaticListening)
        {
            // Toggle automatic listening on/off
            if (isListening)
            {
                StopAutomaticListening();
            }
            else
            {
                StartAutomaticListening();
            }
        }
        else
        {
            // Original manual recording behavior
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
    }
    
    // New dynamic recording method similar to the Python implementation
    private IEnumerator DynamicRecordAndProcess()
    {
        // Reset isSpeaking state to make sure we can record
        isSpeaking = false;
        
        // Set recording state FIRST to mark that we're actively recording
        isRecording = true;
        
        Debug.Log(">>> STARTING DYNAMIC RECORDING SESSION <<<");
        
        // Update UI if not in automatic mode
        if (!useAutomaticListening && recordButton != null)
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
            recordingClip = Microphone.Start(null, true, 30, sampleRate); // Max 30 seconds, loop recording
            tempClip = recordingClip; // Store reference for cleanup
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error starting microphone: {e.Message}");
            isRecording = false;
            
            // Update UI if not in automatic mode
            if (!useAutomaticListening && recordButton != null)
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
        
        // Calculate buffer sizes based on sample rate
        int preBufferSampleSize = Mathf.RoundToInt(preBufferDuration * sampleRate);
        int bufferSize = 1024; // Size of buffer to analyze at once
        float[] samples = new float[bufferSize];
        
        // Pre-buffer setup
        float[] preBuffer = new float[preBufferSampleSize];
        int preBufferPosition = 0;
        int preBufferFilled = 0;
        
        int startPosition = 0;
        float silenceTime = 0f;
        bool hasSpeechStarted = false;
        int lastPosition = 0;
        int currentPosition = 0; // Declare here to make it available outside the loop
        
        Debug.Log("Dynamic recording started, waiting for speech...");
        
        // If automatic mode, set a max total recording time (prevent endless waiting)
        float totalRecordingTime = 0f;
        float maxWaitTime = 15f; // Maximum time to wait for speech to begin
        
        while (isRecording)
        {
            // Get current microphone position
            currentPosition = Microphone.GetPosition(null);
            
            // Handle looping in the audio clip
            if (currentPosition < lastPosition) 
            {
                startPosition = 0;
            }
            lastPosition = currentPosition;
            
            // Calculate how many new samples we have
            int newSamples = 0;
            if (currentPosition > startPosition)
            {
                newSamples = currentPosition - startPosition;
            }
            else if (currentPosition < startPosition)
            {
                newSamples = (recordingClip.samples - startPosition) + currentPosition;
            }
            
            // Skip if we don't have enough new samples
            if (newSamples < bufferSize)
            {
                yield return new WaitForSeconds(0.01f);
                
                // Update total recording time
                totalRecordingTime += 0.01f;
                
                // Check for max recording duration (only if speech has started)
                if (hasSpeechStarted && totalRecordingTime >= maxRecordingDuration)
                {
                    Debug.Log($"Maximum recording duration of {maxRecordingDuration}s reached, stopping recording");
                    break;
                }
                
                // Update total waiting time for speech detection
                if (!hasSpeechStarted)
                {
                    // If we've been waiting too long, abort
                    if (useAutomaticListening && totalRecordingTime > maxWaitTime)
                    {
                        Debug.Log("Reached maximum wait time, no speech detected");
                        break;
                    }
                }
                
                continue;
            }
            
            // Get samples from recording
            recordingClip.GetData(samples, startPosition % recordingClip.samples);
            
            // Update position for next iteration
            startPosition = (startPosition + bufferSize) % recordingClip.samples;
            
            // Calculate audio level (RMS)
            float sum = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += Mathf.Abs(samples[i]);
            }
            float rms = sum / samples.Length;
            
            // Log audio levels for threshold tuning if enabled
            if (debugAudioLevels && Time.frameCount % 10 == 0) // Only log every 10 frames to avoid console spam
            {
                string levelIndicator = "";
                int barCount = Mathf.FloorToInt(rms * 1000); // Scale up for visibility
                
                // Cap the number of bars for display
                barCount = Mathf.Min(barCount, 50);
                
                // Add bars for visual indication of level
                for (int i = 0; i < barCount; i++)
                {
                    levelIndicator += "|";
                }
                
                // Show threshold marker
                int thresholdPosition = Mathf.FloorToInt(silenceThreshold * 1000);
                thresholdPosition = Mathf.Min(thresholdPosition, 50);
                
                string thresholdIndicator = "";
                for (int i = 0; i < thresholdPosition; i++)
                {
                    thresholdIndicator += " ";
                }
                thresholdIndicator += "T";
                
                // Log RMS level and visual indicator
                Debug.Log($"Audio level: {rms:F4} {levelIndicator}\nThreshold: {silenceThreshold:F4} {thresholdIndicator}");
            }
            
            // If speech has not started yet, fill pre-buffer
            if (!hasSpeechStarted)
            {
                // Add samples to pre-buffer (circular buffer)
                int copyLength = Mathf.Min(bufferSize, preBuffer.Length - preBufferPosition);
                Array.Copy(samples, 0, preBuffer, preBufferPosition, copyLength);
                
                if (copyLength < bufferSize && preBufferPosition + copyLength >= preBuffer.Length)
                {
                    // Wrap around to start of pre-buffer
                    Array.Copy(samples, copyLength, preBuffer, 0, bufferSize - copyLength);
                    preBufferPosition = bufferSize - copyLength;
                }
                else
                {
                    preBufferPosition = (preBufferPosition + copyLength) % preBuffer.Length;
                }
                
                preBufferFilled = Mathf.Min(preBufferFilled + bufferSize, preBuffer.Length);
                
                // Check if speech started
                if (rms > silenceThreshold)
                {
                    // Log audio level that triggered speech detection
                    Debug.Log($"***SPEECH DETECTED*** Audio level: {rms:F4} (Threshold: {silenceThreshold:F4})");
                    
                    hasSpeechStarted = true;
                    Debug.Log("Speech detected!");
                }
            }
            else
            {
                // Speech already started, check for silence
                if (rms < silenceThreshold)
                {
                    silenceTime += (float)bufferSize / sampleRate;
                    
                    // Log silence detection progress every half second to show countdown
                    if (Mathf.FloorToInt(silenceTime * 2) > Mathf.FloorToInt((silenceTime - (float)bufferSize / sampleRate) * 2))
                    {
                        Debug.Log($"Silence detected for {silenceTime:F1}s / {silenceTimeToStop:F1}s required to stop");
                    }
                    
                    if (silenceTime >= silenceTimeToStop)
                    {
                        Debug.Log($"Silence threshold reached after {silenceTime:F2}s, stopping recording");
                        break;
                    }
                }
                else
                {
                    // Reset silence timer if sound is detected
                    if (silenceTime > 0)
                    {
                        silenceTime = 0;
                        Debug.Log("Sound detected again, resetting silence timer");
                    }
                }
            }
            
            yield return new WaitForSeconds(0.01f);
        }
        
        // Stop recording
        Microphone.End(null);
        Debug.Log("Recording finished");
        
        if (!hasSpeechStarted)
        {
            Debug.Log("No speech detected, aborting");
            isRecording = false;
            
            // Update UI if not in automatic mode
            if (!useAutomaticListening && recordButton != null)
            {
                Text buttonText = recordButton.GetComponentInChildren<Text>();
                if (buttonText != null)
                {
                    buttonText.text = "Record";
                }
            }
            yield break;
        }
        
        // Update UI to show processing
        if (recordButton != null)
        {
            Text buttonText = recordButton.GetComponentInChildren<Text>();
            if (buttonText != null)
            {
                buttonText.text = "Processing...";
            }
        }
        
        // Create a new audio clip with the recorded data plus pre-buffer
        int recordedSamples = currentPosition;
        if (recordedSamples < startPosition) 
            recordedSamples += recordingClip.samples;
        
        int totalSamples = recordedSamples + preBufferFilled;
        AudioClip processClip = AudioClip.Create("ProcessedRecording", totalSamples, 1, sampleRate, false);
        
        // Copy pre-buffer to new clip
        float[] allSamples = new float[totalSamples];
        if (preBufferFilled > 0)
        {
            // First copy the pre-buffer data
            Array.Copy(preBuffer, preBufferPosition, allSamples, 0, preBuffer.Length - preBufferPosition);
            if (preBufferPosition > 0)
            {
                Array.Copy(preBuffer, 0, allSamples, preBuffer.Length - preBufferPosition, preBufferPosition);
            }
        }
        
        // Copy main recorded data
        float[] recordedData = new float[recordedSamples];
        recordingClip.GetData(recordedData, 0);
        Array.Copy(recordedData, 0, allSamples, preBufferFilled, recordedSamples);
        
        // Set the data to the new clip
        processClip.SetData(allSamples, 0);
        
        byte[] wavData = null;
        
        try
        {
            // Convert AudioClip to WAV
            wavData = AudioClipToWav(processClip);
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
                    if (useAutomaticListening)
                    {
                        buttonText.text = "Pause Listening";
                    }
                    else
                    {
                        buttonText.text = "Record";
                    }
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
                if (useAutomaticListening)
                {
                    buttonText.text = "Pause Listening";
                }
                else
                {
                    buttonText.text = "Record";
                }
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
        Debug.Log(">>> STARTING AUDIO TRANSCRIPTION <<<");
        
        // Keep track that we are still in the "recording" flow
        // This prevents automatic listening from starting a new recording session
        isProcessingResponse = true;
        
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
            
            // Reset processing state on error
            isProcessingResponse = false;
            
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
                
                // Enhanced logging of user input with more visibility
                Debug.Log("========================================");
                Debug.Log($"USER INPUT: \"{transcribedText}\"");
                Debug.Log("========================================");
                
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
        else
        {
            // Reset processing state if we didn't get a valid transcription
            Debug.LogWarning(">>> TRANSCRIPTION FAILED OR EMPTY - RESETTING STATE <<<");
            isProcessingResponse = false;
            
            // Ensure listening resumes
            if (useAutomaticListening && !isListening && automaticListeningCoroutine == null && !shouldStopListening)
            {
                Debug.Log(">>> RESTARTING LISTENING AFTER TRANSCRIPTION FAILURE <<<");
                isListening = true;
                automaticListeningCoroutine = StartCoroutine(AutomaticListeningLoop());
            }
        }
    }
    
    private IEnumerator GetFridaResponse(string userText)
    {
        Debug.Log(">>> REQUESTING FRIDA'S RESPONSE <<<");
        
        // Set both processing flags to prevent new recordings during this phase
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
            
            // Reset states on error
            isProcessingResponse = false;
            isWaitingForResponse = false;
            
            // Ensure listening resumes
            if (useAutomaticListening && !isListening && automaticListeningCoroutine == null && !shouldStopListening)
            {
                Debug.Log(">>> RESTARTING LISTENING AFTER RESPONSE REQUEST FAILURE <<<");
                isListening = true;
                automaticListeningCoroutine = StartCoroutine(AutomaticListeningLoop());
            }
            
            yield break;
        }
        
        // Move yield outside of try-catch
        yield return www.SendWebRequest();
        
        bool responseStarted = false;
        
        if (www.result == UnityWebRequest.Result.Success)
        {
            // Start checking for response completion
            Debug.Log(">>> RESPONSE GENERATION STARTED, POLLING FOR COMPLETION <<<");
            checkResponseCoroutine = StartCoroutine(CheckResponseStatus());
            responseStarted = true;
        }
        else
        {
            Debug.LogError($"Failed to start response generation: {www.error}");
            Debug.LogError("Error: Could not get Frida's response");
            
            // Reset states on error
            isProcessingResponse = false;
            isWaitingForResponse = false;
        }
        
        www.Dispose();
        
        // If response didn't start, ensure listening is restarted
        if (!responseStarted)
        {
            // Ensure listening resumes if response generation failed to start
            if (useAutomaticListening && !isListening && automaticListeningCoroutine == null && !shouldStopListening)
            {
                Debug.Log(">>> RESTARTING LISTENING AFTER FAILED RESPONSE GENERATION <<<");
                isListening = true;
                automaticListeningCoroutine = StartCoroutine(AutomaticListeningLoop());
            }
        }
    }
    
    private IEnumerator PlayFiller()
    {
        // IMMEDIATELY set isSpeaking to prevent any listening during filler prep
        isSpeaking = true;
        
        // First cancel any active recording or listening
        if (dynamicRecordingCoroutine != null)
        {
            StopCoroutine(dynamicRecordingCoroutine);
            dynamicRecordingCoroutine = null;
        }
        
        if (automaticListeningCoroutine != null)
        {
            StopCoroutine(automaticListeningCoroutine);
            automaticListeningCoroutine = null;
        }
        
        // Explicitly call PauseListeningForSpeech to ensure we're in the right state
        PauseListeningForSpeech();
        
        string url = $"{serverUrl}/get_filler";
        
        UnityWebRequest www = null;
        
        try
        {
            www = UnityWebRequest.PostWwwForm(url, "");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error creating web request in PlayFiller: {e.Message}");
            
            // Make sure we resume listening even in case of error
            StartCoroutine(MonitorSpeechCompletionAndResume());
            
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
                        // Enhanced logging of filler text
                        Debug.Log("----------------FILLER----------------");
                        Debug.Log($"FRIDA FILLER: \"{response.text}\"");
                        Debug.Log("----------------------------------------");
                        
                        Debug.Log($"Received filler audio data, length: {response.audio_base64.Length} characters");
                        byte[] audioBytes = Convert.FromBase64String(response.audio_base64);
                        Debug.Log($"Decoded filler audio data size: {audioBytes.Length} bytes");
                        
                        // Check file signature (first few bytes)
                        if (audioBytes.Length > 4) {
                            string signature = System.Text.Encoding.ASCII.GetString(audioBytes, 0, 4);
                            Debug.Log($"Filler audio data signature: {signature}");
                        }
                        
                        // PlayAudioFromBytes calls PauseListeningForSpeech internally
                        PlayAudioFromBytes(audioBytes, response.text, response.estimated_duration);
                    }
                    catch (System.Exception e) {
                        Debug.LogError($"Error processing filler audio data: {e.Message}");
                        
                        // Make sure listening is resumed if there's an error
                        if (isSpeaking)
                        {
                            StartCoroutine(MonitorSpeechCompletionAndResume());
                        }
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
    
    private IEnumerator CheckResponseStatus()
    {
        Debug.Log(">>> CHECKING FOR RESPONSE STATUS <<<");
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
        
        // Debug log so we can track the full cycle
        Debug.Log(">>> STARTING RESPONSE POLLING LOOP <<<");
        
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
                        
                        // Enhanced logging of Frida's response with more visibility
                        Debug.Log("----------------------------------------");
                        Debug.Log($"FRIDA RESPONSE: \"{response.text}\"");
                        Debug.Log("----------------------------------------");
                        
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
            
            // ERROR RECOVERY: Make sure we reset states even if there was a problem
            isProcessingResponse = false;
            isWaitingForResponse = false;
            
            // Try to resume listening if we weren't able to get a response
            if (isSpeaking)
            {
                Debug.Log("Forcing resume of listening due to response timeout");
                isSpeaking = false;
                
                // Make sure we're not in any intermediate states
                if (isListening && automaticListeningCoroutine == null && !shouldStopListening)
                {
                    automaticListeningCoroutine = StartCoroutine(AutomaticListeningLoop());
                }
            }
        }
        else
        {
            // Success case - make sure we reset the response flags 
            // Note: speaking flags are handled by the audio playback system
            Debug.Log(">>> RESPONSE POLLING COMPLETED SUCCESSFULLY <<<");
            isProcessingResponse = false;
            isWaitingForResponse = false;
        }
    }
    
    private IEnumerator TryGetAudioAlternative(string sessionId, string text, float duration)
    {
        // This is a non-blocking way to try alternative audio download methods
        // We attempt this without blocking the main response processing
        
        // IMMEDIATELY set isSpeaking to true at the very start of the method
        // before any network operations
        isSpeaking = true;
        
        // First cancel any active recording or listening coroutines
        if (dynamicRecordingCoroutine != null)
        {
            StopCoroutine(dynamicRecordingCoroutine);
            dynamicRecordingCoroutine = null;
        }
        
        if (automaticListeningCoroutine != null)
        {
            StopCoroutine(automaticListeningCoroutine);
            automaticListeningCoroutine = null;
        }
        
        // Method 1: Direct audio URL
        string directAudioUrl = $"{serverUrl}/get_audio?session_id={sessionId}";
        Debug.Log($"Attempting alternative audio download from: {directAudioUrl}");
        
        UnityWebRequest audioWww = null;
        
        try
        {
            audioWww = UnityWebRequestMultimedia.GetAudioClip(directAudioUrl, AudioType.MPEG);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error creating request for alternative audio: {e.Message}");
            
            // Make sure we resume listening even in case of error
            StartCoroutine(MonitorSpeechCompletionAndResume());
            
            yield break;
        }
        
        // Move yield outside try-catch
        yield return audioWww.SendWebRequest();
        
        bool success = false;
        
        try
        {
            if (audioWww.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(audioWww);
                if (clip != null)
                {
                    Debug.Log("Successfully downloaded alternative audio");
                    
                    // Frida is already speaking at this point, so we don't need to call PauseListeningForSpeech again
                    
                    audioSource.clip = clip;
                    audioSource.Play();
                    
                    // Set up SALSA lip sync with the alternative audio
                    SetupSalsaLipSync(text, clip.length, null);
                    
                    success = true;
                    
                    // Now explicitly start the monitor to resume listening when done
                    if (speakingMonitorCoroutine != null)
                    {
                        StopCoroutine(speakingMonitorCoroutine);
                    }
                    speakingMonitorCoroutine = StartCoroutine(MonitorSpeechCompletionAndResume());
                }
            }
            else
            {
                Debug.LogWarning($"Alternative audio download failed: {audioWww.error}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error processing alternative audio: {e.Message}");
        }
        finally
        {
            audioWww.Dispose();
        }
        
        if (!success)
        {
            // If we get here, the direct method failed, text is still displayed at least
            Debug.Log("Using text-only fallback for response");
            
            // Make sure we resume listening if the audio playback failed
            StartCoroutine(MonitorSpeechCompletionAndResume());
        }
    }
    
    private void PlayAudioFromBytes(byte[] audioBytes, string text, float estimatedDuration = 0)
    {
        // IMMEDIATELY set isSpeaking to true at the very start of the method
        // before any file operations that might take time
        isSpeaking = true;
        
        // First cancel any active recording or listening coroutines
        if (dynamicRecordingCoroutine != null)
        {
            StopCoroutine(dynamicRecordingCoroutine);
            dynamicRecordingCoroutine = null;
        }
        
        if (automaticListeningCoroutine != null)
        {
            StopCoroutine(automaticListeningCoroutine);
            automaticListeningCoroutine = null;
        }
        
        try
        {
            if (audioSource == null)
            {
                Debug.LogError("AudioSource is null. Cannot play audio.");
                
                // Make sure we resume listening even if there's an error
                if (isSpeaking)
                {
                    StartCoroutine(MonitorSpeechCompletionAndResume());
                }
                return;
            }
            
            // Pause listening while Frida speaks to avoid self-feedback
            PauseListeningForSpeech();
            
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
            
            // Make sure we resume listening even if there's an error
            if (isSpeaking)
            {
                StartCoroutine(MonitorSpeechCompletionAndResume());
            }
        }
    }
    
    private IEnumerator LoadAndPlayAudioFromPath(string filePath, string text)
    {
        Debug.Log($"Loading audio from path: {filePath}");
        
        // If audioSource is missing, don't continue
        if (audioSource == null)
        {
            Debug.LogError("AudioSource is null. Cannot load audio.");
            
            // Make sure we resume listening
            if (isSpeaking)
            {
                isSpeaking = false;
                if (isListening && automaticListeningCoroutine == null && !shouldStopListening)
                {
                    automaticListeningCoroutine = StartCoroutine(AutomaticListeningLoop());
                }
            }
            
            yield break;
        }
        
        // Load as MP3 first since that's what we know we're getting from OpenAI TTS
        UnityWebRequest www = null;
        
        try
        {
            www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.MPEG);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error creating web request for audio: {e.Message}");
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
            
            try
            {
                www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.WAV);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error creating web request for WAV audio: {e.Message}");
                
                // Clean up and resume listening since we're exiting
                CleanupAudioAndResume(filePath);
                yield break;
            }
            
            // Move yield outside try-catch
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
        
        // Wait a moment before deleting the file - outside try blocks
        yield return new WaitForSeconds(1.0f);
        
        // Call helper to clean up and resume listening
        CleanupAudioAndResume(filePath, success);
    }
    
    // Helper method to clean up and resume listening
    private void CleanupAudioAndResume(string filePath, bool playbackSuccess = false)
    {
        // Clean up the temp file
        if (File.Exists(filePath))
        {
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
        
        if (!playbackSuccess)
        {
            Debug.LogWarning("Audio playback failed, but text response is still available");
        }
        
        // Make sure we resume listening even if playback failed
        if (isSpeaking)
        {
            isSpeaking = false;
            if (isListening && automaticListeningCoroutine == null && !shouldStopListening)
            {
                automaticListeningCoroutine = StartCoroutine(AutomaticListeningLoop());
            }
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
            
            // Pause listening while playing
            PauseListeningForSpeech();
            
            // Start a coroutine to load the audio file
            StartCoroutine(LoadAndPlayAudio(filePath, text));
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error playing audio from file: {e.Message}");
            
            // Make sure we resume listening even if there's an error
            if (isSpeaking)
            {
                StartCoroutine(MonitorSpeechCompletionAndResume());
            }
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
                    // Wait a bit before deleting to ensure it's not in use - outside try block
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
    
    // Helper method to convert AudioClip to WAV format
    private byte[] AudioClipToWav(AudioClip clip)
    {
        if (clip == null)
        {
            Debug.LogError("AudioClip is null");
            return null;
        }
        
        float[] samples = new float[clip.samples];
        clip.GetData(samples, 0);
        
        // Convert float samples to Int16 (16-bit PCM)
        Int16[] intData = new Int16[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            // Convert float to Int16
            intData[i] = (Int16)(samples[i] * 32767);
        }
        
        using (MemoryStream memoryStream = new MemoryStream())
        {
            using (BinaryWriter writer = new BinaryWriter(memoryStream))
            {
                // WAV file header
                // "RIFF" chunk descriptor
                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + intData.Length * 2); // File size - 8 (size of "RIFF" + size field)
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });
                
                // "fmt " sub-chunk
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16); // Size of fmt chunk
                writer.Write((short)1); // Audio format (1 = PCM)
                writer.Write((short)1); // Number of channels
                writer.Write(clip.frequency); // Sample rate
                writer.Write(clip.frequency * 2); // Byte rate (SampleRate * NumChannels * BitsPerSample/8)
                writer.Write((short)2); // Block align (NumChannels * BitsPerSample/8)
                writer.Write((short)16); // Bits per sample
                
                // "data" sub-chunk
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(intData.Length * 2); // Size of data chunk
                
                // Audio data (PCM samples)
                for (int i = 0; i < intData.Length; i++)
                {
                    writer.Write(intData[i]);
                }
            }
            
            return memoryStream.ToArray();
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
        if (automaticListeningCoroutine != null)
        {
            StopCoroutine(automaticListeningCoroutine);
        }
        if (speakingMonitorCoroutine != null)
        {
            StopCoroutine(speakingMonitorCoroutine);
        }
    }
} 