# Native speech integration smoke test

This opt-in tool uses an existing isolated clip benchmark folder containing `clips.json`, `clips/*.wav`, and the `silence.wav` control. It never reads original media paths from the manifest or a live Scout database. It builds a disposable composite recording and reference catalog explicitly labeled as a synthetic fixture, then verifies Parakeet and Whisper on CPU/CUDA, silence, timestamp bounds, database persistence and the full subtitle-first matching pipeline without AI requests.

Run from the Scout checkout:

```powershell
dotnet run --project tools/Scout.SpeechSmoke -c Release -- <benchmark-folder> <isolated-output-folder> <ffmpeg.exe>
```

Requires Windows x64, an NVIDIA GPU with a compatible driver, FFmpeg/FFprobe and .NET 11. It downloads pinned speech assets into `<isolated-output-folder>/models/speech` when absent. Existing verified benchmark model files and archives may be copied there first. All reports, caches and fixture media stay under the chosen output folder; source WAVs are only read. The fixture checks integration, not full-show assignment accuracy on real episodes.
