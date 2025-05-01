import os
import pyaudio
import wave
import tempfile
from openai import OpenAI

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
EXIT_PHRASES = ["goodbye", "bye", "thank you", "thanks", "exit", "quit", "end"]


def record_audio(seconds=5, sample_rate=16000):
    """Record audio from microphone for specified duration."""
    chunk = 1024
    audio_format = pyaudio.paInt16
    channels = 1

    p = pyaudio.PyAudio()

    print(f"Recording for {seconds} seconds...")

    stream = p.open(
        format=audio_format,
        channels=channels,
        rate=sample_rate,
        input=True,
        frames_per_buffer=chunk,
    )

    frames = []

    for i in range(0, int(sample_rate / chunk * seconds)):
        data = stream.read(chunk)
        frames.append(data)

    print("Recording finished.")

    stream.stop_stream()
    stream.close()
    p.terminate()

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
    """Check if the user wants to end the conversation."""
    text_lower = text.lower()
    return any(phrase in text_lower for phrase in EXIT_PHRASES)


# Main function
def main():
    print("===== Frida Kahlo Conversation =====")
    print(
        "Speak to Frida! Say something like 'goodbye' or 'thank you' to end the conversation."
    )

    conversation_history = []

    while True:
        # Record audio from microphone
        audio_file_path = record_audio(seconds=5)

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
            if os.path.exists(audio_file_path):
                os.remove(audio_file_path)


if __name__ == "__main__":
    main()
