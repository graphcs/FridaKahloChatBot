# Frida Kahlo Conversation

A conversational AI that simulates speaking with the Mexican artist Frida Kahlo, using speech recognition and dynamic listening.

## Features

- **Dynamic Listening**: Detects when you start and stop speaking
- **Natural Voice**: Uses Frida's persona with OpenAI's high-quality text-to-speech
- **Filler Statements**: Plays short responses immediately while generating a full answer
- **Interactive Conversations**: Frida asks questions to keep the dialog engaging

## Requirements

- Python 3.6+
- OpenAI API key
- PyAudio

## Installation

1. Clone this repository
2. Install dependencies: `pip install -r requirements.txt`
3. Set your OpenAI API key: `export OPENAI_API_KEY="your_key_here"`

## Usage

Basic usage:
```
python whisper_test.py
```

### Command-line Options

- `--model "gpt-4"`: Use a different model for responses (default: gpt-3.5-turbo)
- `--skip-welcome`: Skip the welcome message
- `--tts-voice "nova"`: Change the OpenAI TTS voice (default: shimmer)

### Available TTS Voices

- `shimmer` (default): Female voice
- `nova`: Female voice
- `alloy`: Non-binary voice
- `echo`: Male voice
- `fable`: Male voice
- `onyx`: Male voice

## Examples

Basic usage with default settings:
```
python whisper_test.py
```

Use GPT-4 for higher quality responses:
```
python whisper_test.py --model "gpt-4"
```

Try a different voice:
```
python whisper_test.py --tts-voice "nova"
```

## How It Works

1. The program listens using your microphone
2. When you speak, it dynamically detects your voice and records
3. When you stop speaking, it automatically stops recording
4. Your speech is transcribed using OpenAI Whisper
5. A quick filler phrase plays immediately to keep the conversation flowing
6. Frida responds using GPT (asking questions ~50% of the time)
7. The response is spoken using OpenAI's text-to-speech 