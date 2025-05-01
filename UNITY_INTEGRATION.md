# Integrating Frida Conversation System with Unity

This document explains how to integrate the Frida Kahlo conversational AI with a Unity application.

## Architecture Overview

The system uses a client-server architecture:

1. **Server Side**: A Flask API server that handles:
   - Speech transcription via OpenAI Whisper
   - Response generation using OpenAI GPT-4
   - Text-to-speech conversion using OpenAI TTS
   - Conversation session management

2. **Unity Client**: A C# script that communicates with the server to:
   - Record audio from the user's microphone
   - Send audio for transcription
   - Receive and play back Frida's responses

## Setup Instructions

### 1. Server Setup

1. Install required Python packages:
   ```
   pip install flask openai
   ```

2. Set up your OpenAI API key as an environment variable (IMPORTANT for security):
   
   On macOS/Linux:
   ```
   export OPENAI_API_KEY="your-api-key-here"
   ```
   
   On Windows:
   ```
   set OPENAI_API_KEY=your-api-key-here
   ```
   
   NOTE: Do NOT hardcode your API key directly in the code files. This is a security risk if the code is shared or pushed to version control.

3. Run the Flask server:
   ```
   python frida_server.py
   ```

4. The server will start on `http://localhost:5001` by default.

### 2. Unity Setup

1. Create a new Unity project or use an existing one.

2. Create a new GameObject in your scene to handle the Frida conversation.

3. Add the `FridaConversation.cs` script to this GameObject.

4. Set up the required Unity components:
   - Create UI elements for recording (Button)
   - Create UI element for displaying text responses (Text)
   - Add an AudioSource component to play responses

5. Configure the script in the Inspector:
   - Set the server URL (default is `http://localhost:5001`)
   - Assign the UI elements created in step 4
   - Set the recording duration as needed

6. Make sure to add the Newtonsoft.Json package to your Unity project:
   - Open Package Manager (Window > Package Manager)
   - Add the "com.unity.nuget.newtonsoft-json" package

## API Endpoints

The server exposes the following endpoints:

- `POST /start_session`: Starts a new conversation session
- `POST /transcribe`: Transcribes audio sent from Unity
- `POST /get_response`: Generates Frida's response based on the user's text
- `POST /end_session`: Ends a conversation session

## How It Works

1. When the FridaConversation component is initialized, it starts a new session with the server.

2. When the record button is pressed, it:
   - Records audio from the microphone for the specified duration
   - Converts the audio to WAV format
   - Sends it to the server for transcription
   - Displays the transcribed text

3. The server then:
   - Generates a response as Frida Kahlo
   - Converts the response to speech
   - Returns both text and audio to Unity

4. Unity plays the audio response and displays the text.

5. The conversation continues until the user ends the session.

## Customization

- **Modify Frida's Personality**: Edit the `FRIDA_PROMPT` in `frida_server.py`
- **Change Voice**: Modify the `voice` parameter in the text_to_speech function
- **Adjust Response Length**: Change the `max_tokens` parameter in the API call

## Troubleshooting

- **API Key Issues**: Make sure your OpenAI API key is properly set as an environment variable. Avoid hardcoding the key in the code.

- **Port Conflicts**: 
  - The server uses port 5001 by default to avoid conflicts with AirPlay on macOS
  - If you still encounter port conflicts, you can change the port by setting the `PORT` environment variable:
    ```
    export PORT=5002  # or any available port
    ```
  - If using a different port, update the `serverUrl` value in Unity accordingly

- **Server Connection**: Make sure the Flask server is running before starting the Unity application

- **Microphone Access**: Enable microphone permissions in your Unity build settings

- **Audio Playback**: Ensure that your system's audio output is properly configured 