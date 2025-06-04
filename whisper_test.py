import os
import wave
import tempfile
import struct
import math
import re
import time
import random
import subprocess
import threading
from openai import OpenAI
import argparse

# Check for PyAudio availability
try:
    import pyaudio
except ImportError:
    raise ImportError(
        "PyAudio is required. Please install it with 'pip install pyaudio'"
    )

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

# Model configurations
CHAT_MODEL = "gpt-3.5-turbo"  # Faster than gpt-4
TTS_MODEL = "tts-1"
TTS_VOICE = "shimmer"
TRANSCRIPTION_MODEL = "whisper-1"

# Exit phrases that will end the conversation
EXIT_PHRASES = ["goodbye", "bye", "exit", "quit", "end conversation", "stop"]
THANK_PHRASES = ["thank you", "thanks"]

# TTS cache to avoid regenerating common responses
tts_cache = {}
filler_audio_files = {}  # Cache for filler statements


def get_rms(data, width=2):
    """Calculate the root mean square of a chunk of audio data."""
    # Convert binary data to short integers
    count = len(data) // width
    format = f"{count}h" if width == 2 else f"{count}B"
    shorts = struct.unpack(format, data)

    # Calculate RMS
    sum_squares = sum(s * s for s in shorts)
    return math.sqrt(sum_squares / count) if count > 0 else 0


def record_audio(max_seconds=30, sample_rate=16000):
    """Record audio from microphone dynamically, stopping when silence is detected."""
    chunk = 1024
    audio_format = pyaudio.paInt16
    channels = 1

    # Silence detection parameters
    threshold = 300  # Lowered from 500 to 300 to detect quieter voices
    silence_limit = 2.5

    p = pyaudio.PyAudio()

    stream = p.open(
        format=audio_format,
        channels=channels,
        rate=sample_rate,
        input=True,
        frames_per_buffer=chunk,
    )

    print("Listening... (speak now)")

    frames = []
    silent_chunks = 0
    audio_started = False
    max_chunks = int(sample_rate / chunk * max_seconds)

    # Pre-buffer to avoid cutting off the beginning of speech
    pre_buffer = []
    num_pre_buffer_chunks = int(0.5 * sample_rate / chunk)  # 0.5 seconds pre-buffer

    for i in range(0, max_chunks):
        data = stream.read(chunk)

        # Calculate audio energy
        rms = get_rms(data)

        # If not started speaking yet, fill pre-buffer
        if not audio_started:
            pre_buffer.append(data)
            # Keep pre-buffer size limited
            if len(pre_buffer) > num_pre_buffer_chunks:
                pre_buffer.pop(0)

            # Check if speaking has started
            if rms > threshold:
                audio_started = True
                frames.extend(pre_buffer)  # Add pre-buffer to frames
                frames.append(data)
                print("Speech detected, recording...")
                silent_chunks = 0
        else:
            # Speech already started, so add this chunk to frames
            frames.append(data)

            # Check for silence
            if rms < threshold:
                silent_chunks += 1
                silence_duration = silent_chunks * chunk / sample_rate
                if silence_duration >= silence_limit:
                    print("Silence detected, stopping recording.")
                    break
            else:
                silent_chunks = 0

    stream.stop_stream()
    stream.close()
    p.terminate()

    # If no speech was detected at all
    if not audio_started:
        print("No speech detected.")
        return None

    # Create a temporary file to store the recording
    temp_file = tempfile.NamedTemporaryFile(suffix=".wav", delete=False)

    # Save the recording as a WAV file
    with wave.open(temp_file.name, "wb") as wf:
        wf.setnchannels(channels)
        wf.setsampwidth(p.get_sample_size(audio_format))
        wf.setframerate(sample_rate)
        wf.writeframes(b"".join(frames))

    return temp_file.name


def get_frida_response(user_text, conversation_history=None):
    """Generate a response as Frida Kahlo."""
    if conversation_history is None:
        conversation_history = []

    # Keep conversation history limited to last 10 exchanges to maintain speed
    if len(conversation_history) > 20:
        # Keep the first exchange (welcome) and the last 9 exchanges
        conversation_history = [conversation_history[0]] + conversation_history[-18:]

    messages = [{"role": "system", "content": FRIDA_PROMPT}]
    messages.extend(conversation_history)
    messages.append({"role": "user", "content": user_text})

    # Show progress during API call
    print("Thinking", end="", flush=True)
    start_time = time.time()

    response = client.chat.completions.create(
        model=CHAT_MODEL,
        messages=messages,
        max_tokens=150,  # Limit token count for shorter responses
    )

    elapsed = time.time() - start_time
    print(f" ({elapsed:.1f}s)")

    return response.choices[0].message.content


def text_to_speech(text, use_cache=True):
    """Convert text to speech using OpenAI's API."""
    # Check if we have this text cached
    if use_cache and text in tts_cache:
        print("Using cached TTS audio")
        return tts_cache[text]

    print("Converting to speech (OpenAI)", end="", flush=True)
    start_time = time.time()

    response = client.audio.speech.create(
        model=TTS_MODEL, voice=TTS_VOICE, input=text, speed=1
    )

    elapsed = time.time() - start_time
    print(f" ({elapsed:.1f}s)")

    # Cache the result
    if use_cache:
        tts_cache[text] = response.content

    # Save to a temporary file
    output_file = f"frida_speech_{hash(text) % 10000}.mp3"
    with open(output_file, "wb") as f:
        f.write(response.content)

    return output_file


def prepare_filler_statements(all_temp_files):
    """Pre-generate audio for filler statements."""
    print("Preparing filler statements...")

    for statement in FILLER_STATEMENTS:
        filler_file = text_to_speech(statement)
        filler_audio_files[statement] = filler_file
        all_temp_files.add(filler_file)

    print(f"Prepared {len(filler_audio_files)} filler statements")


def play_filler_statement():
    """Play a random filler statement."""
    if not filler_audio_files:
        return

    filler = random.choice(list(filler_audio_files.keys()))
    audio_file = filler_audio_files[filler]

    print(f"Frida (filler): {filler}")
    play_audio(audio_file)


def play_audio(audio_file_path):
    """Play audio file using macOS afplay."""
    os.system(f"afplay {audio_file_path}")


def generate_response_in_background(user_text, conversation_history):
    """Generate Frida's response and TTS in a background thread."""
    response_result = {"response": None, "audio_file": None}

    def generate():
        # Generate the text response
        response = get_frida_response(user_text, conversation_history)
        response_result["response"] = response

        # Convert to speech
        audio_file = text_to_speech(response)
        response_result["audio_file"] = audio_file

    thread = threading.Thread(target=generate)
    thread.daemon = True
    thread.start()

    return thread, response_result


def should_exit(text):
    """Check if the user wants to end the conversation.
    Only returns True if it's clear the user intends to end the conversation.
    """
    text_lower = text.lower().strip()

    # First split into sentences to handle multi-part messages
    sentences = re.split(r"[.!?;]+", text_lower)

    # For each sentence, check if it contains exit intent
    for sentence in sentences:
        sentence = sentence.strip()
        if not sentence:
            continue

        # 1. Direct exit phrase matches
        if any(exit_phrase == sentence for exit_phrase in EXIT_PHRASES):
            return True

        # 2. If the sentence contains a question, don't consider it an exit
        # This helps with cases like "Can you tell me about your art? Thanks."
        if "?" in sentence or any(
            q_word + " " in sentence
            for q_word in ["what", "why", "how", "when", "where", "who", "can"]
        ):
            continue

        # 3. Check for standalone thank phrases - only at the end of the conversation
        # Be cautious with "thanks" - only consider it exit if it's:
        # - Very short sentence (≤ 4 words)
        # - AND doesn't ask for more info
        words = sentence.split()
        if len(words) <= 4 and any(
            thank_phrase in sentence for thank_phrase in THANK_PHRASES
        ):
            # Make sure it doesn't contain words suggesting continuation
            continuation_words = ["more", "also", "another", "tell", "about"]
            if not any(cont_word in words for cont_word in continuation_words):
                # If it looks like a standalone thanks, it could be an exit
                return True

    # If no clear exit intent was found
    return False


def main(skip_welcome=False):
    print("===== Frida Kahlo Conversation =====")
    print(
        "Speak to Frida! Say something like 'goodbye' or 'thank you' to end the conversation."
    )
    print(f"Using OpenAI TTS with voice '{TTS_VOICE}'")

    conversation_history = []

    # Keep track of all temporary files
    all_temp_files = set()

    # Pre-generate filler audio files
    prepare_filler_statements(all_temp_files)

    # Initial welcome message from Frida
    welcome_message = "Hola! I am Frida Kahlo. I am here to share my thoughts on art, life, and passion. What would you like to talk about?"
    welcome_file = None

    if not skip_welcome:
        print("\nFrida:", welcome_message)

        # Convert welcome to speech
        welcome_file = text_to_speech(welcome_message)
        all_temp_files.add(welcome_file)

        print("Playing welcome...")
        play_audio(welcome_file)
    else:
        print("\nSkipping welcome message. Ready to listen...")

    # Add Frida's welcome to conversation history regardless
    # This helps the model maintain context even if we don't play it
    conversation_history.append({"role": "assistant", "content": welcome_message})

    # Main conversation loop
    audio_file_path = None

    try:
        while True:
            # Start transcription immediately after recording to reduce wait time
            print("\n---------------------------")

            # Record audio from microphone
            audio_file_path = record_audio(max_seconds=30)
            if audio_file_path:
                all_temp_files.add(audio_file_path)

            # If no speech was detected, continue listening
            if audio_file_path is None:
                print("No speech detected. Please try again.")
                continue

            # Start transcription immediately
            print("Transcribing...", end="", flush=True)
            start_time = time.time()

            with open(audio_file_path, "rb") as audio_file:
                transcript = client.audio.transcriptions.create(
                    model=TRANSCRIPTION_MODEL, file=audio_file
                )

            elapsed = time.time() - start_time
            print(f" ({elapsed:.1f}s)")

            # Clean up audio file immediately after use
            if audio_file_path and os.path.exists(audio_file_path):
                os.remove(audio_file_path)
                all_temp_files.discard(audio_file_path)
                audio_file_path = None

            user_text = transcript.text
            print("\nYou said:", user_text)

            # Check if the conversation should end
            if should_exit(user_text):
                goodbye_message = "Adiós, mi querido amigo. Until we meet again in the realm of art and passion."
                print("\nFrida:", goodbye_message)

                # Convert goodbye to speech and play it
                goodbye_file = text_to_speech(goodbye_message)
                all_temp_files.add(goodbye_file)

                print("Playing goodbye...")
                play_audio(goodbye_file)
                break

            # Add user message to conversation history
            conversation_history.append({"role": "user", "content": user_text})

            # Start generating response in background
            thread, response_result = generate_response_in_background(
                user_text, conversation_history
            )

            # Play a filler statement while generating
            play_filler_statement()

            # If response generation is still ongoing, wait for it to complete
            if thread.is_alive():
                print("Waiting for response generation to complete...")
                thread.join()

            # Get the generated response
            frida_response = response_result["response"]
            audio_file = response_result["audio_file"]

            # Add to temp files list
            if audio_file:
                all_temp_files.add(audio_file)

            print("Frida:", frida_response)

            # Add Frida's response to conversation history
            conversation_history.append(
                {"role": "assistant", "content": frida_response}
            )

            # Play the audio response
            if audio_file:
                print("Playing response...")
                play_audio(audio_file)

    except KeyboardInterrupt:
        print("\nEnding conversation...")

        # Say goodbye even if user interrupts
        try:
            goodbye_message = "Adiós. Our conversation may end, but art is eternal."
            print("\nFrida:", goodbye_message)

            # Convert to speech and play
            goodbye_file = text_to_speech(goodbye_message)
            all_temp_files.add(goodbye_file)

            print("Playing goodbye...")
            play_audio(goodbye_file)
        except Exception as e:
            print(f"Error playing goodbye: {e}")

    finally:
        # Clean up all temporary files
        for file_path in all_temp_files:
            if os.path.exists(file_path):
                try:
                    os.remove(file_path)
                except Exception as e:
                    print(f"Error removing file {file_path}: {e}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Chat with Frida Kahlo")
    parser.add_argument(
        "--model",
        choices=["gpt-4", "gpt-3.5-turbo"],
        default=CHAT_MODEL,
        help="Model to use for responses (default: %(default)s)",
    )
    parser.add_argument(
        "--skip-welcome", action="store_true", help="Skip the welcome message"
    )
    parser.add_argument(
        "--tts-voice",
        choices=["alloy", "echo", "fable", "onyx", "nova", "shimmer"],
        default=TTS_VOICE,
        help="Voice to use with OpenAI TTS (default: %(default)s)",
    )

    args = parser.parse_args()

    # Update settings based on command line arguments
    if args.model:
        CHAT_MODEL = args.model
    if args.tts_voice:
        TTS_VOICE = args.tts_voice

    main(skip_welcome=args.skip_welcome)
