# Ensure proper encoding for non-ASCII characters
import sys
import os
os.environ['PYTHONIOENCODING'] = 'utf-8'
sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')

from flask import Flask, request, jsonify, send_file, make_response
from flask_swagger_ui import get_swaggerui_blueprint
from openai import OpenAI
import base64
import tempfile
import time
import threading
import random

app = Flask(__name__)

# Enable CORS for all routes - this is crucial for WebGL builds
@app.after_request
def add_cors_headers(response):
    response.headers.add('Access-Control-Allow-Origin', '*')  # Allow requests from any origin
    response.headers.add('Access-Control-Allow-Headers', 'Content-Type,Authorization')
    response.headers.add('Access-Control-Allow-Methods', 'GET,POST,OPTIONS')
    # Handle preflight requests
    if request.method == 'OPTIONS':
        return response
    return response

# Add a route to handle preflight OPTIONS requests explicitly
@app.route('/', defaults={'path': ''}, methods=['OPTIONS'])
@app.route('/<path:path>', methods=['OPTIONS'])
def handle_options(path):
    response = make_response()
    response.headers.add('Access-Control-Allow-Origin', '*')
    response.headers.add('Access-Control-Allow-Headers', 'Content-Type,Authorization')
    response.headers.add('Access-Control-Allow-Methods', 'GET,POST,OPTIONS')
    return response

# Set up Swagger UI
SWAGGER_URL = "/api/docs"  # URL for exposing Swagger UI
API_URL = "/static/swagger.json"  # Our API url (can of course be a local resource)

# Call factory function to create our blueprint
swaggerui_blueprint = get_swaggerui_blueprint(
    SWAGGER_URL,
    API_URL,
    config={"app_name": "Frida Kahlo Conversation API"},  # Swagger UI config overrides
)

# Register blueprint at URL
app.register_blueprint(swaggerui_blueprint, url_prefix=SWAGGER_URL)

# Create directory for static files if it doesn't exist
os.makedirs(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "static"), exist_ok=True
)

# Create a swagger.json file
swagger_json = """
{
  "swagger": "2.0",
  "info": {
    "title": "Frida Kahlo Conversation API",
    "description": "API for conversing with Frida Kahlo AI character",
    "version": "1.0.0"
  },
  "host": "localhost:5001",
  "basePath": "/",
  "schemes": [
    "http"
  ],
  "paths": {
    "/start_session": {
      "post": {
        "summary": "Start a new conversation session",
        "produces": ["application/json"],
        "responses": {
          "200": {
            "description": "Session started successfully",
            "schema": {
              "type": "object",
              "properties": {
                "session_id": {"type": "string"},
                "welcome_text": {"type": "string"},
                "welcome_audio": {"type": "string"},
                "estimated_duration": {"type": "number"}
              }
            }
          }
        }
      }
    },
    "/transcribe": {
      "post": {
        "summary": "Transcribe audio to text",
        "consumes": ["multipart/form-data"],
        "parameters": [
          {
            "name": "audio",
            "in": "formData",
            "description": "Audio file to transcribe",
            "required": true,
            "type": "file"
          }
        ],
        "responses": {
          "200": {
            "description": "Transcription successful",
            "schema": {
              "type": "object",
              "properties": {
                "text": {"type": "string"}
              }
            }
          }
        }
      }
    },
    "/get_filler": {
      "post": {
        "summary": "Get a random filler statement",
        "produces": ["application/json"],
        "responses": {
          "200": {
            "description": "Filler retrieved successfully",
            "schema": {
              "type": "object",
              "properties": {
                "text": {"type": "string"},
                "audio_base64": {"type": "string"},
                "estimated_duration": {"type": "number"}
              }
            }
          }
        }
      }
    },
    "/get_response": {
      "post": {
        "summary": "Start generating a response from Frida",
        "consumes": ["application/json"],
        "parameters": [
          {
            "name": "body",
            "in": "body",
            "required": true,
            "schema": {
              "type": "object",
              "properties": {
                "text": {"type": "string"},
                "session_id": {"type": "string"}
              },
              "required": ["text", "session_id"]
            }
          }
        ],
        "responses": {
          "200": {
            "description": "Response generation started",
            "schema": {
              "type": "object",
              "properties": {
                "status": {"type": "string"}
              }
            }
          }
        }
      }
    },
    "/check_response": {
      "post": {
        "summary": "Check if a response is ready",
        "consumes": ["application/json"],
        "parameters": [
          {
            "name": "body",
            "in": "body",
            "required": true,
            "schema": {
              "type": "object",
              "properties": {
                "session_id": {"type": "string"}
              },
              "required": ["session_id"]
            }
          }
        ],
        "responses": {
          "200": {
            "description": "Response status",
            "schema": {
              "type": "object",
              "properties": {
                "completed": {"type": "boolean"},
                "status": {"type": "string"},
                "text": {"type": "string"},
                "audio_base64": {"type": "string"},
                "duration": {"type": "number"},
                "phoneme_data": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "word": {"type": "string"},
                      "start_time": {"type": "number"},
                      "end_time": {"type": "number"}
                    }
                  }
                }
              }
            }
          }
        }
      }
    },
    "/end_session": {
      "post": {
        "summary": "End a conversation session",
        "consumes": ["application/json"],
        "parameters": [
          {
            "name": "body",
            "in": "body",
            "required": true,
            "schema": {
              "type": "object",
              "properties": {
                "session_id": {"type": "string"}
              },
              "required": ["session_id"]
            }
          }
        ],
        "responses": {
          "200": {
            "description": "Session ended successfully",
            "schema": {
              "type": "object",
              "properties": {
                "status": {"type": "string"}
              }
            }
          }
        }
      }
    },
    "/get_audio": {
      "get": {
        "summary": "Download the most recent audio directly as MP3",
        "parameters": [
          {
            "name": "session_id",
            "in": "query",
            "required": true,
            "type": "string"
          }
        ],
        "responses": {
          "200": {
            "description": "Audio retrieved successfully",
            "schema": {
              "type": "string"
            }
          },
          "404": {
            "description": "Audio not found"
          }
        }
      }
    },
    "/get_response_audio": {
      "post": {
        "summary": "Alternative endpoint to get audio in WAV format",
        "consumes": ["application/json"],
        "parameters": [
          {
            "name": "session_id",
            "in": "body",
            "required": true,
            "schema": {
              "type": "string"
            }
          }
        ],
        "responses": {
          "200": {
            "description": "Audio URL retrieved successfully",
            "schema": {
              "type": "object",
              "properties": {
                "audio_url": {"type": "string"}
              }
            }
          },
          "404": {
            "description": "Audio not found"
          }
        }
      }
    }
  }
}
"""

# Write swagger.json to static directory
swagger_path = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "static", "swagger.json"
)
with open(swagger_path, "w") as f:
    f.write(swagger_json)

# Get API key from environment variable
api_key = os.environ.get("OPENAI_API_KEY")
if not api_key:
    # Print bright red warning
    print("\033[91m" + "*" * 80)
    print("ERROR: OPENAI_API_KEY environment variable is not set!")
    print("Please set your OpenAI API key using:")
    print("export OPENAI_API_KEY=your_api_key_here")
    print("*" * 80 + "\033[0m")
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


@app.route("/get_audio", methods=["GET"])
def get_audio():
    """Download the most recent audio directly as MP3."""
    session_id = request.args.get('session_id')
    
    if not session_id or session_id not in response_threads:
        return jsonify({"error": "No audio available for this session"}), 404
    
    # Get the audio from the response
    _, result_dict = response_threads[session_id]
    if not result_dict["completed"] or not result_dict["audio"]:
        return jsonify({"error": "Audio not ready or unavailable"}), 404
    
    # Create a response with MP3 data
    response = make_response(result_dict["audio"])
    response.headers.set('Content-Type', 'audio/mpeg')
    response.headers.set('Content-Disposition', 'attachment', filename='frida_response.mp3')
    return response


@app.route("/get_response_audio", methods=["POST"])
def get_response_audio():
    """Alternative endpoint to get audio in WAV format."""
    data = request.json
    session_id = data.get("session_id", "default")
    
    if session_id not in response_threads:
        return jsonify({"error": "No response found for this session"}), 404
    
    _, result_dict = response_threads[session_id]
    if not result_dict["completed"] or not result_dict["audio"]:
        return jsonify({"error": "Audio not ready"}), 404
    
    # Serve the audio URL (could be modified to serve a WAV file instead)
    temp_url = f"{serverUrl}/get_audio?session_id={session_id}"
    return jsonify({"audio_url": temp_url})


if __name__ == "__main__":
    # Use port 5001 by default to avoid conflicts with AirPlay on macOS
    port = int(os.environ.get("PORT", 5001))
    app.run(host="0.0.0.0", port=port)
