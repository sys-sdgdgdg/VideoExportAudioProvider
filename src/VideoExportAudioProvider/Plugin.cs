using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace VideoExportAudioProvider
{
    [BepInPlugin(GUID, Name, Version)]
    [BepInProcess("CharaStudio")]
    [BepInDependency(global::VideoExport.VideoExport.GUID, DependencyFlags.SoftDependency)]
    public class VideoExportAudioProviderPlugin : BaseUnityPlugin
    {
        public const string GUID = "VideoExportAudioProvider";
        public const string Name = "VideoExportAudioProvider";
        public const string Version = "1.0.0";

        internal static ManualLogSource Logger;

        private ConfigEntry<string> _audioFilePath;

        private void Awake()
        {
            Logger = base.Logger;

            _audioFilePath = Config.Bind(
                "General",
                "Audio file",
                "",
                "Full path to the audio file mixed into exported videos. WAV, MP3, FLAC, OGG or any other format the bundled ffmpeg can read.");
        }

        private void Start()
        {
            var videoExport = FindObjectOfType<global::VideoExport.VideoExport>();
            if (videoExport == null)
            {
                Logger.LogWarning($"VideoExport not found (guid {global::VideoExport.VideoExport.GUID}). Install VideoExport r2.24 or newer from IllusionMods/HSPlugins releases - this plugin does nothing without it.");
                enabled = false;
                return;
            }

            var provider = new FileAudioProvider(_audioFilePath);
            if (videoExport.AddAudioPlugin(provider, out var error))
                Logger.LogInfo($"Registered audio provider '{provider.Name}' with VideoExport.");
            else
                Logger.LogWarning($"Failed to register audio provider with VideoExport: {error}");
        }
    }
}
