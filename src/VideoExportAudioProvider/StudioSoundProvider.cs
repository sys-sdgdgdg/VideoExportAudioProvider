using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Studio;
using UnityEngine;
using VideoExport.AudioPlugins;

namespace VideoExportAudioProvider
{
    /// <summary>
    /// Captures studio sound items (Studio.Sound.SEComponent, e.g. vanilla sound effects and
    /// 3DSE sound-mod items like the OpenNSFW pack) that are triggered by Timeline's
    /// built-in "Enabled" (objectEnabled) keyframes, and renders them offline into a WAV
    /// that lines up sample-accurately with the exported video frames.
    ///
    /// VideoExport asks for the audio BEFORE it starts rendering frames, so the audio can't
    /// be captured live - instead the provider pre-computes every sound item's on/off windows
    /// from the Timeline keyframes in memory, which is exactly what the renderer will apply
    /// frame by frame.
    /// </summary>
    public class StudioSoundProvider : IAudioPlugin
    {
        private struct SoundEvent
        {
            public AudioClip Clip;
            public string ClipName;
            public float OnTime;
            public float OffTime;
            public bool Loop;
            public float Volume;
        }

        public string Name { get { return "Studio sounds"; } }

        public string SafeName { get { return "StudioSounds"; } }

        public BinaryReader MakeAudioStream(float startTime, float duration, int sampleRate)
        {
            try
            {
                List<SoundEvent> events = CollectEvents(startTime, duration);
                if (events.Count == 0)
                {
                    VideoExportAudioProviderPlugin.Logger.LogInfo("No Timeline-triggered studio sounds found for this export.");
                    return null;
                }

                int frames = Mathf.CeilToInt(duration * sampleRate);
                var mix = new float[frames * 2];

                foreach (SoundEvent e in events)
                    MixEvent(mix, e, startTime, sampleRate);

                byte[] wav = ToWav16(mix, sampleRate);
                VideoExportAudioProviderPlugin.Logger.LogInfo($"Mixed {events.Count} sound clip event(s) into {wav.Length / 1024} KB of audio.");
                return new BinaryReader(new MemoryStream(wav));
            }
            catch (Exception e)
            {
                VideoExportAudioProviderPlugin.Logger.LogError("Failed to build studio sound track: " + e);
                return null;
            }
        }

        #region Event collection

        private static List<SoundEvent> CollectEvents(float startTime, float endTime)
        {
            var result = new List<SoundEvent>();

            var timelineSelf = Traverse.Create(typeof(global::Timeline.Timeline)).Field("_self").GetValue() as global::Timeline.Timeline;
            if (timelineSelf == null)
            {
                VideoExportAudioProviderPlugin.Logger.LogWarning("Timeline is not loaded, no studio sounds can be captured.");
                return result;
            }

            var interpolables = Traverse.Create(timelineSelf).Field("_interpolables").GetValue() as IDictionary;
            if (interpolables == null || interpolables.Count == 0)
                return result;

            foreach (DictionaryEntry entry in interpolables)
            {
                var interp = entry.Value as global::Timeline.Interpolable;
                if (interp == null || !interp.enabled || interp.id != "objectEnabled")
                    continue;

                var ociItem = interp.oci as OCIItem;
                if (ociItem == null || ociItem.objectItem == null)
                    continue;

                Studio.Sound.SEComponent se = ociItem.objectItem.GetComponent<Studio.Sound.SEComponent>();
                if (se == null || se.Clip == null)
                    continue;

                SortedList<float, global::Timeline.Keyframe> kfs = interp.keyframes;
                if (kfs == null || kfs.Count == 0)
                {
                    // Not animated; if the item is on right now it will stay on for the whole export.
                    if (IsVisible(ociItem))
                        result.Add(MakeEvent(se, startTime, endTime));
                    continue;
                }

                bool state = StateAt(kfs, startTime, IsVisible(ociItem));
                float windowStart = state ? startTime : float.NegativeInfinity;

                for (int i = 0; i < kfs.Count; i++)
                {
                    float t = kfs.Keys[i];
                    if (t <= startTime)
                        continue;
                    if (t >= endTime)
                        break;

                    bool v;
                    try { v = (bool)kfs.Values[i].value; }
                    catch { continue; }

                    if (v == state)
                        continue;

                    if (v)
                    {
                        state = true;
                        windowStart = t;
                    }
                    else
                    {
                        state = false;
                        result.Add(MakeEvent(se, windowStart, t));
                    }
                }

                if (state)
                    result.Add(MakeEvent(se, windowStart, endTime));
            }

            return result;
        }

        private static bool IsVisible(OCIItem ociItem)
        {
            try { return ociItem.treeNodeObject != null && ociItem.treeNodeObject.visible; }
            catch { return false; }
        }

        private static bool StateAt(SortedList<float, global::Timeline.Keyframe> kfs, float time, bool fallback)
        {
            bool state = fallback;
            for (int i = kfs.Count - 1; i >= 0; i--)
            {
                if (kfs.Keys[i] <= time)
                {
                    try { state = (bool)kfs.Values[i].value; }
                    catch { }
                    break;
                }
            }
            return state;
        }

        private static SoundEvent MakeEvent(Studio.Sound.SEComponent se, float onTime, float offTime)
        {
            var e = new SoundEvent
            {
                Clip = se.Clip,
                ClipName = se.Clip.name,
                OnTime = onTime,
                OffTime = offTime,
                Loop = se.IsLoop,
                Volume = se.Volume
            };
            VideoExportAudioProviderPlugin.Logger.LogInfo(
                $"Sound '{e.ClipName}' {(e.Loop ? "(loop) " : "")}plays between {e.OnTime:0.00}s and {e.OffTime:0.00}s (volume {e.Volume:0.00}).");
            return e;
        }

        #endregion

        #region Mixing

        private static void MixEvent(float[] mix, SoundEvent e, float startTime, int sampleRate)
        {
            AudioClip clip = e.Clip;
            if (clip == null || clip.samples <= 0)
                return;

            int srcCh = clip.channels;
            int srcRate = clip.frequency;
            int srcFrames = clip.samples;

            var data = new float[srcFrames * srcCh];
            try
            {
                if (!clip.GetData(data, 0))
                {
                    VideoExportAudioProviderPlugin.Logger.LogWarning($"Could not read samples of '{e.ClipName}', skipping.");
                    return;
                }
            }
            catch (Exception ex)
            {
                VideoExportAudioProviderPlugin.Logger.LogWarning($"Failed reading samples of '{e.ClipName}': {ex.Message}. Skipping.");
                return;
            }

            int totalOut = mix.Length / 2;
            int outStart = Mathf.Max(0, Mathf.RoundToInt((e.OnTime - startTime) * sampleRate));
            int outEnd = Mathf.Min(totalOut, Mathf.RoundToInt((e.OffTime - startTime) * sampleRate));
            if (outEnd <= outStart)
                return;

            // Where the clip would already be at when the export range begins
            float posAtStart = 0f;
            if (e.OnTime < startTime)
            {
                if (e.Loop)
                    posAtStart = ((startTime - e.OnTime) * srcRate) % srcFrames;
                else
                    posAtStart = (startTime - e.OnTime) * srcRate;
            }

            float ratio = (float)srcRate / sampleRate;
            int leftCh = 0;
            int rightCh = srcCh >= 2 ? 1 : 0;

            for (int n = outStart; n < outEnd; n++)
            {
                float srcPos = posAtStart + (n - outStart) * ratio;
                if (e.Loop)
                {
                    srcPos = srcPos % srcFrames;
                }
                else if (srcPos >= srcFrames)
                {
                    break;
                }

                int i0 = (int)srcPos;
                int i1 = i0 + 1;
                if (i1 >= srcFrames)
                    i1 = e.Loop ? 0 : srcFrames - 1;
                float frac = srcPos - i0;

                float l = Mathf.Lerp(data[i0 * srcCh + leftCh], data[i1 * srcCh + leftCh], frac) * e.Volume;
                float r = Mathf.Lerp(data[i0 * srcCh + rightCh], data[i1 * srcCh + rightCh], frac) * e.Volume;

                mix[n * 2] += l;
                mix[n * 2 + 1] += r;
            }
        }

        private static byte[] ToWav16(float[] mix, int sampleRate)
        {
            short[] pcm = new short[mix.Length];
            for (int i = 0; i < mix.Length; i++)
            {
                float v = mix[i];
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                pcm[i] = (short)Math.Round(v * 32767f);
            }

            int dataSize = pcm.Length * 2;
            var ms = new MemoryStream(44 + dataSize);
            var w = new BinaryWriter(ms);

            w.Write("RIFF".ToCharArray());
            w.Write(36 + dataSize);
            w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray());
            w.Write(16);
            w.Write((short)1);        // PCM
            w.Write((short)2);        // stereo
            w.Write(sampleRate);
            w.Write(sampleRate * 4);  // byte rate
            w.Write((short)4);        // block align
            w.Write((short)16);       // bits
            w.Write("data".ToCharArray());
            w.Write(dataSize);
            var pcmBytes = new byte[dataSize];
            Buffer.BlockCopy(pcm, 0, pcmBytes, 0, dataSize);
            w.Write(pcmBytes);

            return ms.ToArray();
        }

        #endregion
    }
}
