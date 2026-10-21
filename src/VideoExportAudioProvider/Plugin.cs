using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace VideoExportAudioProvider
{
    [BepInPlugin(GUID, Name, Version)]
    [BepInProcess("CharaStudio")]
    [BepInDependency(global::VideoExport.VideoExport.GUID)]
    public class VideoExportAudioProviderPlugin : BaseUnityPlugin
    {
        public const string GUID = "VideoExportAudioProvider";
        public const string Name = "VideoExportAudioProvider";
        public const string Version = "1.5.0";

        internal static new ManualLogSource Logger;
        internal static ConfigEntry<bool> MuteDuringExport;
        internal static ConfigEntry<bool> ApplyChannelVolume;
        internal static ConfigEntry<bool> SpatialAudioApproximation;

        private ConfigEntry<string> _audioFilePath;

        private void Awake()
        {
            Logger = base.Logger;

            _audioFilePath = Config.Bind(
                "General",
                "Audio file",
                "",
                "Full path to the audio file mixed into exported videos (Audio file provider). WAV, MP3, FLAC, OGG or any other format the bundled ffmpeg can read.");

            MuteDuringExport = Config.Bind(
                "General",
                "Mute during export",
                true,
                "Mutes the game's live audio while VideoExport is rendering so the slow-motion playback of studio sounds doesn't blast out of your speakers. The exported video still gets its audio from the offline-rendered track.");

            ApplyChannelVolume = Config.Bind(
                "General",
                "Apply game channel volume",
                true,
                "Apply the game's AudioMixer channel and master volumes (the sound sliders in the game options) to the exported audio, matching what you hear in-game. Channel volumes are read live at export time.");

            SpatialAudioApproximation = Config.Bind(
                "General",
                "3D audio approximation",
                true,
                "Approximate Unity 3D audio in the export: distance rolloff from the studio camera (position at export start) with the item's own rolloff mode and range, plus constant-power stereo panning from the camera-relative direction. Items farther than their max distance are silent, like Unity's voice culling.");

            new Harmony(GUID).PatchAll(typeof(Patches).Assembly);
        }

        private void Start()
        {
            var videoExport = FindObjectOfType<global::VideoExport.VideoExport>();
            if (videoExport == null)
            {
                Logger.LogWarning("VideoExport not found (guid " + global::VideoExport.VideoExport.GUID + "). Install VideoExport r2.24 or newer from IllusionMods/HSPlugins releases - this plugin does nothing without it.");
                enabled = false;
                return;
            }

            DumpSoundTypeTruth();

            Register(videoExport, new StudioSoundProvider());
            Register(videoExport, new FileAudioProvider(_audioFilePath));
        }

        /// <summary>
        /// Logs the measured AudioSource prefab properties of every game sound type
        /// (spatial blend, mixer routing) - the ground truth the StudioSoundProvider's
        /// spatial approximation is driven by.
        /// </summary>
        private void DumpSoundTypeTruth()
        {
            try
            {
                foreach (Manager.Sound.Type type in Enum.GetValues(typeof(Manager.Sound.Type)))
                {
                    Logger.LogInfo("[StudioSoundTruth] type " + type + " (" + (int)type + "): " +
                                   StudioSoundProvider.DescribeType(type));
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[StudioSoundTruth] failed to dump sound types: " + e.Message);
            }
        }

        private void Register(global::VideoExport.VideoExport videoExport, VideoExport.AudioPlugins.IAudioPlugin provider)
        {
            if (videoExport.AddAudioPlugin(provider, out var error))
                Logger.LogInfo($"Registered audio provider '{provider.Name}' with VideoExport.");
            else
                Logger.LogWarning($"Failed to register audio provider '{provider.Name}' with VideoExport: {error}");
        }

        private void OnDisable()
        {
            Patches.RestoreVolume();
        }
    }

    internal static class Patches
    {
        private static float _savedVolume = 1f;
        private static bool _muted;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(global::VideoExport.VideoExport), nameof(global::VideoExport.VideoExport.RecordVideo))]
        internal static void OnRecordStart()
        {
            if (!VideoExportAudioProviderPlugin.MuteDuringExport.Value || _muted)
                return;
            _savedVolume = AudioListener.volume;
            AudioListener.volume = 0f;
            _muted = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(global::VideoExport.VideoExport), nameof(global::VideoExport.VideoExport.StopRecording))]
        internal static void OnRecordStop()
        {
            RestoreVolume();
        }

        internal static void RestoreVolume()
        {
            if (!_muted)
                return;
            AudioListener.volume = _savedVolume;
            _muted = false;
        }
    }
}
