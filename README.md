# OwlCore.Console.TalkBox

A quick and easy console application for common STT and TTS functionality. 

## Featuring:
- Listen and transcribe continuously via Whisper.net for STT.
- Say and speak any text using PiperSharp for TTS.
- Play any wav file using OwlCore.Storage.OpenAL.
- Tested working on both Linux and Windows. 

## Install

1. Clone the repository
2. Install dotnet
3. `dotnet run` in the `src` folder

## Usage

Continuous STT via Whisper:

```pwsh
dotnet run listen --inputDevice default
```

- Leave out the `inputDevice` param to get a list of values. 
- Combine with `-nolog` and tee/pipe the stdout to append a file.

Speak text once via Piper:
```pwsh
dotnet run say --text "testing" --voiceModelKey "en_US-norman-medium" --outputDevice "default"
```
- Leave out the `outputDevice` param to get a list of values. 
- Leave out the `voiceModelKey` param to get a list of values. 

Speak a file:
```pwsh
dotnet run say --textFile "I:\pr_draft.txt" --voiceModelKey "en_US-norman-medium" --outputDevice "default"
```

Speak a line within a file:
```pwsh
dotnet run say --textFile "I:\pr_draft.txt#L1" --voiceModelKey "en_US-norman-medium" --outputDevice "default"
```

Speak a line range within a file:
```pwsh
dotnet run say --textFile "I:\pr_draft.txt#L1-L2" --voiceModelKey "en_US-norman-medium" --outputDevice "default"
```

Speak a line *and* column range within a file:
```pwsh
dotnet run say --textFile "I:\pr_draft.txt#L1C1-L1C7" --voiceModelKey "en_US-norman-medium" --outputDevice "default"
```

## Financing

We accept donations [here](https://github.com/sponsors/Arlodotexe) and [here](https://www.patreon.com/arlodotexe), and we do not have any active bug bounties.

## Versioning

Version numbering follows the Semantic versioning approach. However, if the major version is `0`, the code is considered alpha and breaking changes may occur as a minor update.

## License

All OwlCore code is licensed under the MIT License. OwlCore is licensed under the MIT License. See the [LICENSE](./src/LICENSE.txt) file for more details.
