using System.IO;
using BepInEx.Configuration;
using VideoExport.AudioPlugins;

namespace VideoExportAudioProvider
{
    /// <summary>
    /// Serves one audio file (WAV/MP3/FLAC/OGG/...) as an audio source for VideoExport.
    /// The file is muxed into the exported video as-is; VideoExport applies volume,
    /// mixing, resampling and duration trimming (atrim) on its ffmpeg side.
    /// </summary>
    public class FileAudioProvider : IAudioPlugin
    {
        private readonly ConfigEntry<string> _filePath;

        public FileAudioProvider(ConfigEntry<string> filePath)
        {
            _filePath = filePath;
        }

        public string Name { get { return "Audio file"; } }

        public string SafeName { get { return "AudioFile"; } }

        public BinaryReader MakeAudioStream(float startTime, float duration, int sampleRate)
        {
            string path = _filePath.Value;
            if (string.IsNullOrEmpty(path))
            {
                VideoExportAudioProviderPlugin.Logger.LogWarning("No audio file configured (BepInEx/config/VideoExportAudioProvider.cfg). Skipping audio.");
                return null;
            }

            if (!File.Exists(path))
            {
                VideoExportAudioProviderPlugin.Logger.LogWarning($"Audio file does not exist: {path}. Skipping audio.");
                return null;
            }

            VideoExportAudioProviderPlugin.Logger.LogInfo($"Providing audio file for export: {path} (start {startTime:0.00}s, duration {duration:0.00}s, mix {sampleRate}Hz)");
            return new BinaryReader(File.OpenRead(path));
        }
    }
}
