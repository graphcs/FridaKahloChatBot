from flask import Flask, request, jsonify, send_file
import os
import tempfile
import base64
from openai import OpenAI
import time
import random
import threading

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
Use vivid language that reflects her artistic nature.
IMPORTANT: ALWAYS end your response with a thoughtful question to the user.
Your questions should be relevant to the context, your life, art, Mexico, or what the user might be interested in.
This is crucial - every response must include a question to keep the conversation flowing."""

# Pre-recorded filler statements that play while waiting for AI response
FILLER_STATEMENTS = [
    "Hmm, let me think...",
    "Ah, interesting question...",
    "Let me reflect on that...",
    "How shall I respond...",
    "This reminds me of something...",
    "That's a thoughtful point...",
    "Thinking of my perspective...",
    "Considering this from my experience...",
    "Let me share my thoughts...",
    "Un momento, por favor...",
]

# Store conversation sessions and response generation threads
sessions = {}
response_threads = {}
filler_audio_cache = {}

# Model configurations
CHAT_MODEL = "gpt-3.5-turbo"  # Faster than gpt-4
TTS_MODEL = "tts-1"
TTS_VOICE = "shimmer"
TRANSCRIPTION_MODEL = "whisper-1"


def generate_audio(text, cache_key=None):
    """Generate audio from text using OpenAI TTS."""
    # Check cache if a key is provided
    if cache_key and cache_key in filler_audio_cache:
        return filler_audio_cache[cache_key]

    # Generate speech
    speech_response = client.audio.speech.create(
        model=TTS_MODEL, voice=TTS_VOICE, input=text
    )

    # Save to cache if needed
    if cache_key:
        filler_audio_cache[cache_key] = speech_response.content

    return speech_response.content


# Pre-generate filler statements
def init_filler_statements():
    for statement in FILLER_STATEMENTS:
        generate_audio(statement, cache_key=statement)
    print(f"Pre-generated {len(FILLER_STATEMENTS)} filler statements")


# Initialize fillers
init_filler_statements()


@app.route("/start_session", methods=["POST"])
def start_session():
    """Start a new conversation session."""
    session_id = str(int(time.time()))
    sessions[session_id] = []

    # Generate welcome message
    welcome_message = "Hola! I am Frida Kahlo. I am here to share my thoughts on art, life, and passion. What would you like to talk about?"
    welcome_audio = generate_audio(welcome_message)

    # Add to session history
    sessions[session_id].append({"role": "assistant", "content": welcome_message})

    # Encode audio to base64
    welcome_audio_b64 = base64.b64encode(welcome_audio).decode("utf-8")

    return jsonify(
        {
            "session_id": session_id,
            "welcome_text": welcome_message,
            "welcome_audio": welcome_audio_b64,
            "estimated_duration": len(welcome_message.split())
            * 0.3,  # Rough duration estimate
        }
    )


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
                model=TRANSCRIPTION_MODEL, file=file
            )

        return jsonify({"text": transcript.text})

    finally:
        # Clean up
        if os.path.exists(temp_audio.name):
            os.remove(temp_audio.name)


@app.route("/get_filler", methods=["POST"])
def get_filler():
    """Get a random filler statement for immediate feedback."""
    # Pick a random filler
    filler = random.choice(FILLER_STATEMENTS)

    # Get pre-generated audio from cache
    filler_audio = filler_audio_cache.get(filler)
    if not filler_audio:
        # Generate if not cached
        filler_audio = generate_audio(filler)

    # Convert to base64
    audio_b64 = base64.b64encode(filler_audio).decode("utf-8")

    return jsonify(
        {
            "text": filler,
            "audio_base64": audio_b64,
            "estimated_duration": len(filler.split()) * 0.3,  # Rough duration estimate
        }
    )


def generate_response_thread(user_text, session_id, result_dict):
    """Background thread to generate Frida's response."""
    conversation_history = sessions.get(session_id, [])

    # Generate Frida's response
    messages = [{"role": "system", "content": FRIDA_PROMPT}]
    messages.extend(conversation_history)
    messages.append({"role": "user", "content": user_text})

    response = client.chat.completions.create(
        model=CHAT_MODEL, messages=messages, max_tokens=150
    )

    frida_response = response.choices[0].message.content

    # Add to session history
    conversation_history.append({"role": "user", "content": user_text})
    conversation_history.append({"role": "assistant", "content": frida_response})
    sessions[session_id] = conversation_history

    # Generate speech
    speech_audio = generate_audio(frida_response)

    # Update result dictionary
    result_dict["text"] = frida_response
    result_dict["audio"] = speech_audio
    result_dict["completed"] = True


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

    # Start response generation in background
    result_dict = {"text": None, "audio": None, "completed": False}
    thread = threading.Thread(
        target=generate_response_thread, args=(user_text, session_id, result_dict)
    )
    thread.daemon = True
    thread.start()

    # Store the thread and result
    response_threads[session_id] = (thread, result_dict)

    return jsonify({"status": "processing"})


@app.route("/check_response", methods=["POST"])
def check_response():
    """Check if a response is ready."""
    data = request.json
    session_id = data.get("session_id", "default")

    # Check if thread exists
    if session_id not in response_threads:
        return jsonify({"error": "No response being generated for this session"}), 404

    thread, result_dict = response_threads[session_id]

    # Check if response is ready
    if not result_dict["completed"]:
        return jsonify({"status": "processing", "completed": False})

    # Response is ready
    frida_response = result_dict["text"]
    speech_audio = result_dict["audio"]

    # Encode audio to base64
    audio_data = base64.b64encode(speech_audio).decode("utf-8")

    # Calculate phoneme and timing data for SALSA
    words = frida_response.split()
    estimated_duration = len(words) * 0.3  # Rough estimate: 0.3 seconds per word

    # Generate simplified phoneme timing data for SALSA
    # In a real production system, you would use a proper phoneme extraction library
    phoneme_data = []
    current_time = 0.0
    for word in words:
        word_duration = len(word) * 0.075  # Rough estimate: 75ms per character
        phoneme_data.append(
            {
                "word": word,
                "start_time": current_time,
                "end_time": current_time + word_duration,
            }
        )
        current_time += word_duration + 0.1  # Add a small gap between words

    # Clean up
    del response_threads[session_id]

    return jsonify(
        {
            "completed": True,
            "text": frida_response,
            "audio_base64": audio_data,
            "duration": estimated_duration,
            "phoneme_data": phoneme_data,
        }
    )


@app.route("/end_session", methods=["POST"])
def end_session():
    """End a conversation session."""
    data = request.json
    session_id = data.get("session_id")

    if session_id and session_id in sessions:
        del sessions[session_id]
        # Also clean up any pending threads
        if session_id in response_threads:
            del response_threads[session_id]
        return jsonify({"status": "Session ended successfully"})

    return jsonify({"error": "Session not found"}), 404


if __name__ == "__main__":
    # Use port 5001 by default to avoid conflicts with AirPlay on macOS
    port = int(os.environ.get("PORT", 5001))
    app.run(host="0.0.0.0", port=port)
