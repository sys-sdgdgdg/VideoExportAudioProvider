# VideoExportAudioProvider

Audio provider plugins for the [VideoExport](https://github.com/IllusionMods/HSPlugins/tree/main/VideoExport.KK) studio plugin (Koikatsu / CharaStudio).
VideoExport renders studio animations to video files, and since [HSPlugins r2.24](https://github.com/IllusionMods/HSPlugins/releases/tag/r2.24)
it ships with an audio plugin API (`IAudioPlugin` / `AddAudioPlugin`) — but no audio provider is included in the release.
This plugin registers audio sources with VideoExport so your exported videos include sound. **No fork or patch of VideoExport is needed.**

## What it does

- Adds an **"Audio file"** source to VideoExport's Audio section.
- Mixes one configured audio file (WAV, MP3, FLAC, OGG or anything the bundled ffmpeg can decode) into the exported video.
- Volume / enable / sample-rate / codec controls come from VideoExport itself; trimming to the exported duration is handled by VideoExport's ffmpeg pipeline (`amix` + `atrim` + `aresample`).

## Requirements

- Koikatsu / Koikatsu Party (CharaStudio) with **BepInEx 5**
- **VideoExport r2.24 or newer** (plugin version 2.1.1, from [IllusionMods/HSPlugins releases](https://github.com/IllusionMods/HSPlugins/releases)) — older versions have no audio API

## Installation

1. Install VideoExport and its requirements (Screencap, KKAPI) first.
2. Download `VideoExportAudioProvider.zip` from [releases](../../releases) and extract the dll into `BepInEx/plugins`.
3. Launch CharaStudio once to generate `BepInEx/config/VideoExportAudioProvider.cfg`.
4. Open the config file and set `Audio file` to the full path of your audio file (e.g. `D:\Music\dance.mp3`).
5. In the VideoExport window, expand the **Audio settings** section, tick **Include audio**, keep **Audio file** enabled, pick an audio codec and export as usual (MP4 / MKV / WEBM / MOV — GIF, WEBP and AVIF carry no audio).

## Building from source

- .NET SDK with net35 targeting pack (or Visual Studio with .NET Framework 3.5)
- A Koikatsu install with BepInEx and VideoExport already in place

```
dotnet build src/VideoExportAudioProvider -p:GameDir="C:\Path\To\Koikatsu"
```

`GameDir` can also be supplied through the `KK_DIR` environment variable.

## Limitations / roadmap

- [ ] Honor the Timeline start position (`startTime`) so the audio can start mid-song
- [ ] Multiple simultaneous audio files
- [ ] WASAPI loopback capture of in-game studio sound

## Credits

- [joan6694](https://github.com/joan6694) — original VideoExport plugin
- [starstormhun](https://github.com/starstormhun), [Lusiocc](https://github.com/Lusiocc), [IllusionMods](https://github.com/IllusionMods) — audio plugin API and current maintenance
- Audio codec/container compatibility table comes straight from VideoExport's source

## License

[CC BY-NC-SA 4.0](./LICENSE) — you may share and adapt this code for **non-commercial** purposes, as long as you give credit and license derivatives under the same terms. Not affiliated with Illusion.
