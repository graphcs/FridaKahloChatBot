import os
import wave
import tempfile
import struct
import math
import re
from openai import OpenAI

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
Use vivid language that reflects her artistic nature."""

# Exit phrases that will end the conversation
EXIT_PHRASES = ["goodbye", "bye", "exit", "quit", "end conversation", "stop"]
THANK_PHRASES = ["thank you", "thanks"]


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
    threshold = 500  # Adjust this threshold based on your microphone and environment
    silence_limit = 1.5  # Stop recording after 1.5 seconds of silence

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

    messages = [{"role": "system", "content": FRIDA_PROMPT}]
    messages.extend(conversation_history)
    messages.append({"role": "user", "content": user_text})

    response = client.chat.completions.create(
        model="gpt-4",
        messages=messages,
        max_tokens=150,  # Limit token count for shorter responses
    )

    return response.choices[0].message.content


def text_to_speech(text):
    """Convert text to speech and return the audio bytes."""
    response = client.audio.speech.create(
        model="tts-1",
        voice="shimmer",  # Using a female voice that sounds good for Frida
        input=text,
    )
    return response.content


def play_audio(audio_file_path):
    """Play audio file using macOS afplay."""
    os.system(f"afplay {audio_file_path}")


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


# Main function
def main():
    print("===== Frida Kahlo Conversation =====")
    print(
        "Speak to Frida! Say something like 'goodbye' or 'thank you' to end the conversation."
    )

    conversation_history = []

    # Initial welcome message from Frida
    welcome_message = "Hola! I am Frida Kahlo. I am here to share my thoughts on art, life, and passion. What would you like to talk about?"
    print("\nFrida:", welcome_message)

    # Convert welcome to speech
    print("Converting welcome to speech...")
    welcome_speech = text_to_speech(welcome_message)

    # Save and play the welcome message
    welcome_file = "frida_welcome.mp3"
    with open(welcome_file, "wb") as f:
        f.write(welcome_speech)

    print("Playing welcome...")
    play_audio(welcome_file)

    # Add Frida's welcome to conversation history
    conversation_history.append({"role": "assistant", "content": welcome_message})

    while True:
        # Record audio from microphone
        audio_file_path = record_audio(max_seconds=30)

        # If no speech was detected, continue listening
        if audio_file_path is None:
            print("No speech detected. Please try again.")
            continue

        try:
            # Transcribe the audio
            with open(audio_file_path, "rb") as audio_file:
                transcript = client.audio.transcriptions.create(
                    model="whisper-1", file=audio_file
                )

            user_text = transcript.text
            print("\nYou said:", user_text)

            # Check if the conversation should end
            if should_exit(user_text):
                print(
                    "\nFrida: Adiós, mi querido amigo. Until we meet again in the realm of art and passion."
                )
                break

            # Add user message to conversation history
            conversation_history.append({"role": "user", "content": user_text})

            # Generate Frida's response
            print("\nGenerating Frida's response...")
            frida_response = get_frida_response(user_text, conversation_history)
            print("Frida:", frida_response)

            # Add Frida's response to conversation history
            conversation_history.append(
                {"role": "assistant", "content": frida_response}
            )

            # Convert to speech
            print("\nConverting to speech...")
            speech_audio = text_to_speech(frida_response)

            # Save and play the audio response
            response_file = "frida_response.mp3"
            with open(response_file, "wb") as f:
                f.write(speech_audio)

            print("Playing response...")
            play_audio(response_file)

        finally:
            # Clean up the temporary file
            if audio_file_path and os.path.exists(audio_file_path):
                os.remove(audio_file_path)


if __name__ == "__main__":
    main()
