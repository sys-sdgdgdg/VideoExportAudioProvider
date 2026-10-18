# VideoExportAudioProvider

Audio provider plugins for the [VideoExport](https://github.com/IllusionMods/HSPlugins/tree/main/VideoExport.KK) studio plugin (Koikatsu / CharaStudio).
VideoExport renders studio animations to video files frame by frame, and since [HSPlugins r2.24](https://github.com/IllusionMods/HSPlugins/releases/tag/r2.24)
it ships with an audio plugin API (`IAudioPlugin` / `AddAudioPlugin`) — but no audio provider is included in the release.
This plugin registers audio sources with VideoExport so your exported videos include sound. **No fork or patch of VideoExport is needed.**

## Providers

### Studio sounds (primary)

Captures **studio sound items** — vanilla sound-effect items and 3DSE sound-mod items (e.g. the [OpenNSFW sound pack port](https://github.com/AX-MMD/Koikatsu-OpenNSFW-Sound-Pack-port), which turns every clip into a `Studio.Sound.SEComponent` studio item) — that are triggered from **Timeline's built-in "Enabled" keyframes**.

Because VideoExport asks for the audio *before* it starts rendering frames, live capture can't work. Instead the provider
pre-computes each sound item's on/off windows from the Timeline keyframes in memory — exactly the states the renderer will
apply frame by frame — and renders the clips offline into a WAV that lines up **sample-accurately** with the exported frames:

- One-shot and looping clips
- Clips already playing when the export range starts (correct in-clip offset, including mid-loop)
- Items that are simply on for the whole scene (no keyframes needed)
- Per-item volume from the item's own volume setting

By default the plugin also **mutes the game's live audio during export** (otherwise the slow-motion rendering would stutter
the studio sounds out of your speakers). Toggle this in `BepInEx/config/VideoExportAudioProvider.cfg`.

Not captured (yet): 3D distance attenuation, pitch effects, H-scene voices tied to animation state, and "outside sound"
(外部声音) objects that are played manually instead of being Timeline-toggled.

### Audio file (secondary)

Mixes one configured audio file (WAV, MP3, FLAC, OGG or anything the bundled ffmpeg can decode) into the export,
e.g. the song of a dance scene. Set `Audio file` in the config file.

Enable/disable each provider and adjust its volume in VideoExport's **Audio settings** section; codec, sample rate and
duration trimming are handled by VideoExport itself.

## Requirements

- Koikatsu / Koikatsu Party (CharaStudio) with **BepInEx 5**
- **Timeline** (the provider reads its keyframes)
- **VideoExport r2.24 or newer** (plugin version 2.1.x, from [IllusionMods/HSPlugins releases](https://github.com/IllusionMods/HSPlugins/releases)) — r2.25 is recommended: it fixes the bundled ffmpeg failing silently on some systems and the first exported frame being captured mid-frame

## Installation

1. Install VideoExport and its requirements first.
2. Download `VideoExportAudioProvider.zip` from [releases](../../releases) and extract the dll into `BepInEx/plugins`.
3. In the VideoExport window, expand **Audio settings**, tick **Include audio**, keep the providers you want enabled, pick an audio codec and export as usual (MP4 / MKV / WEBM / MOV — GIF, WEBP and AVIF carry no audio).

## Building from source

- .NET SDK (net35 targeting pack is pulled automatically via NuGet)
- A Koikatsu install with BepInEx, Timeline and VideoExport in place

```
dotnet build src/VideoExportAudioProvider -p:GameDir="C:\Path\To\Koikatsu"
```

`GameDir` can also be supplied through the `KK_DIR` environment variable.

## Roadmap

- [ ] H-scene voice/SFX driven by animation state
- [ ] 外部声音 (outside sound) objects
- [ ] 3D distance attenuation following the exported camera
- [ ] External script audio (VNGE scenes, MMDD music)

## Credits

- [joan6694](https://github.com/joan6694) — original VideoExport plugin
- [starstormhun](https://github.com/starstormhun), [Lusiocc](https://github.com/Lusiocc), [IllusionMods](https://github.com/IllusionMods) — audio plugin API and current maintenance
- [AX-MMD](https://github.com/AX-MMD) — reference for the 3DSE sound-mod item format

## License

[CC BY-NC-SA 4.0](./LICENSE) — you may share and adapt this code for **non-commercial** purposes, as long as you give credit and license derivatives under the same terms. Not affiliated with Illusion.
