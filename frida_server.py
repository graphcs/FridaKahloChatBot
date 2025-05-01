from flask import Flask, request, jsonify, send_file
import os
import tempfile
import base64
from openai import OpenAI
import time

app = Flask(__name__)

# Get API key from environment variable
api_key = os.environ.get("OPENAI_API_KEY")
if not api_key:
    raise ValueError(
        "No API key found. Please set the OPENAI_API_KEY environment variable."
    )

# Initialize the OpenAI client
client = OpenAI(api_key=api_key)

# Frida Kahlo prompt with instructions for shorter responses
FRIDA_PROMPT = """You are Frida Kahlo, the Mexican painter known for your bold art and emotional insight. 
Respond with her voice, tone, and knowledge.
Keep your responses concise (2-3 sentences maximum) but impactful and authentic to Frida's character.
Use vivid language that reflects her artistic nature."""

# Store conversation sessions
sessions = {}


@app.route("/start_session", methods=["POST"])
def start_session():
    """Start a new conversation session."""
    session_id = str(int(time.time()))
    sessions[session_id] = []
    return jsonify({"session_id": session_id})


@app.route("/transcribe", methods=["POST"])
def transcribe_audio():
    """Transcribe audio sent from Unity."""
    # Check if request has the audio file
    if "audio" not in request.files:
        return jsonify({"error": "No audio file provided"}), 400

    audio_file = request.files["audio"]

    # Save the audio to a temporary file
    temp_audio = tempfile.NamedTemporaryFile(suffix=".wav", delete=False)
    audio_file.save(temp_audio.name)
    temp_audio.close()

    try:
        # Transcribe the audio
        with open(temp_audio.name, "rb") as file:
            transcript = client.audio.transcriptions.create(
                model="whisper-1", file=file
            )

        return jsonify({"text": transcript.text})

    finally:
        # Clean up
        if os.path.exists(temp_audio.name):
            os.remove(temp_audio.name)


@app.route("/get_response", methods=["POST"])
def get_response():
    """Generate a response from Frida and return as speech."""
    data = request.json

    if not data or "text" not in data:
        return jsonify({"error": "No text provided"}), 400

    user_text = data["text"]
    session_id = data.get("session_id", "default")

    # Get or create conversation history
    if session_id not in sessions:
        sessions[session_id] = []

    conversation_history = sessions[session_id]

    # Add user message to conversation history
    conversation_history.append({"role": "user", "content": user_text})

    # Generate Frida's response
    messages = [{"role": "system", "content": FRIDA_PROMPT}]
    messages.extend(conversation_history)

    response = client.chat.completions.create(
        model="gpt-4", messages=messages, max_tokens=150
    )

    frida_response = response.choices[0].message.content

    # Add Frida's response to conversation history
    conversation_history.append({"role": "assistant", "content": frida_response})
    sessions[session_id] = conversation_history

    # Convert to speech
    speech_response = client.audio.speech.create(
        model="tts-1", voice="shimmer", input=frida_response
    )

    # Save speech to temporary file
    temp_audio = tempfile.NamedTemporaryFile(suffix=".mp3", delete=False)
    temp_audio.write(speech_response.content)
    temp_audio.close()

    # Convert audio to base64 for sending to Unity
    with open(temp_audio.name, "rb") as audio_file:
        audio_data = base64.b64encode(audio_file.read()).decode("utf-8")

    # Clean up
    os.remove(temp_audio.name)

    return jsonify({"text": frida_response, "audio_base64": audio_data})


@app.route("/end_session", methods=["POST"])
def end_session():
    """End a conversation session."""
    data = request.json
    session_id = data.get("session_id")

    if session_id and session_id in sessions:
        del sessions[session_id]
        return jsonify({"status": "Session ended successfully"})

    return jsonify({"error": "Session not found"}), 404


if __name__ == "__main__":
    # Use port 5001 by default to avoid conflicts with AirPlay on macOS
    port = int(os.environ.get("PORT", 5001))
    app.run(host="0.0.0.0", port=port)
