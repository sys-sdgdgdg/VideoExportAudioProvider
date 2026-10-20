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
    ///
    /// Timeline's raw playback time keeps growing past the timeline length when it loops,
    /// while the scene renders at (playbackTime % duration). An export can therefore start
    /// mid-loop (or after several loops) and cover wrapped keyframe occurrences. All event
    /// math is done on the looped axis: each keyframe's occurrences inside the export range
    /// are computed and mapped to "output time" (seconds since the first audio sample).
    /// </summary>
    public class StudioSoundProvider : IAudioPlugin
    {
        private struct SoundEvent
        {
            public AudioClip Clip;
            public string ClipName;
            public float OnTimeTimeline; // timeline time the clip was enabled at (for in-clip offset)
            public float OnOutput;      // output time (s) the clip starts playing at
            public float OffOutput;     // output time (s) the clip stops at
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

                float loopedStart = LoopStartTime(startTime, duration);

                foreach (SoundEvent e in events)
                    MixEvent(mix, e, loopedStart, sampleRate);

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

        private static float PositiveMod(float value, float modulus)
        {
            float m = value % modulus;
            if (m < 0f)
                m += modulus;
            return m;
        }

        private static float LoopStartTime(float startTime, float timelineDuration)
        {
            if (timelineDuration <= 0f)
                return startTime;
            return PositiveMod(startTime, timelineDuration);
        }

        private static List<SoundEvent> CollectEvents(float startTime, float audioDuration)
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

            float duration = global::Timeline.Timeline.duration;
            if (duration <= 0f)
            {
                VideoExportAudioProviderPlugin.Logger.LogWarning("Timeline duration is zero, no sound events can be mapped.");
                return result;
            }

            float loopedStart = PositiveMod(startTime, duration);

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
                        result.Add(MakeEvent(se, loopedStart, 0f, audioDuration));
                    continue;
                }

                CollectKeyframeEvents(se, kfs, loopedStart, duration, audioDuration, IsVisible(ociItem), result);
            }

            return result;
        }

        /// <summary>
        /// Walks every occurrence of every keyframe inside the export range (accounting for
        /// timeline wraps) and records on/off windows in output time.
        /// </summary>
        private static void CollectKeyframeEvents(Studio.Sound.SEComponent se,
                                                  SortedList<float, global::Timeline.Keyframe> kfs,
                                                  float loopedStart, float duration, float audioDuration,
                                                  bool fallbackVisible,
                                                  List<SoundEvent> result)
        {
            // What renders at the first frame: the value of the last keyframe at or before
            // the looped start position (the renderer interpolates from keyframes directly).
            bool state = false;
            bool haveState = false;
            for (int i = 0; i < kfs.Count; i++)
            {
                float t = kfs.Keys[i];
                if (t > loopedStart)
                    break;
                try
                {
                    state = (bool)kfs.Values[i].value;
                    haveState = true;
                }
                catch { }
            }
            if (!haveState)
                state = fallbackVisible;

            // When (on the looped axis) the currently playing clip was enabled, rising-edge only,
            // since Timeline only rewrites visibility when the value actually changes.
            float windowOnTimeline = loopedStart;
            bool windowOpen = false;
            if (state)
            {
                windowOpen = true;
                windowOnTimeline = LastEnableTimeAt(kfs, loopedStart, duration);
            }
            float windowOnOutput = 0f;

            // Every keyframe occurrence inside the export range: [outputTime, kfTime, value]
            var occurrences = new List<float[]>();
            for (int i = 0; i < kfs.Count; i++)
            {
                float kfTime = kfs.Keys[i];
                if (kfTime > duration)
                    continue;

                bool v;
                try { v = (bool)kfs.Values[i].value; }
                catch { continue; }

                float first = PositiveMod(kfTime - loopedStart, duration);
                for (float o = first; o <= audioDuration; o += duration)
                    occurrences.Add(new[] { o, kfTime, v ? 1f : 0f });
            }

            occurrences.Sort((a, b) => a[0].CompareTo(b[0]));

            foreach (float[] occ in occurrences)
            {
                float outputTime = occ[0];
                float kfTime = occ[1];
                bool v = occ[2] > 0.5f;

                // An occurrence at output 0 is already reflected in the initial state.
                if (outputTime <= 0f)
                    continue;
                if (v == state)
                    continue;

                if (v)
                {
                    state = true;
                    windowOpen = true;
                    windowOnTimeline = kfTime;
                    windowOnOutput = outputTime;
                }
                else
                {
                    state = false;
                    if (windowOpen)
                    {
                        windowOpen = false;
                        result.Add(MakeEvent(se, windowOnTimeline, windowOnOutput, outputTime));
                    }
                }
            }

            if (windowOpen)
                result.Add(MakeEvent(se, windowOnTimeline, windowOnOutput, audioDuration));
        }

        private static bool IsVisible(OCIItem ociItem)
        {
            try { return ociItem.treeNodeObject != null && ociItem.treeNodeObject.visible; }
            catch { return false; }
        }

        /// <summary>
        /// Looping keyframe time of the last rising edge (false -> true) at or before
        /// loopedStart, i.e. when the clip that is currently playing was enabled.
        /// </summary>
        private static float LastEnableTimeAt(SortedList<float, global::Timeline.Keyframe> kfs, float loopedStart, float duration)
        {
            float onTime = loopedStart;
            bool prev = false;
            bool seen = false;
            for (int i = 0; i < kfs.Count; i++)
            {
                float t = kfs.Keys[i];
                if (t > loopedStart)
                    break;
                bool v;
                try { v = (bool)kfs.Values[i].value; }
                catch { continue; }
                if (v && !prev)
                    onTime = t;
                prev = v;
                seen = true;
            }
            if (!seen)
                return loopedStart;
            return onTime;
        }

        private static SoundEvent MakeEvent(Studio.Sound.SEComponent se, float onTimeTimeline, float onOutput, float offOutput)
        {
            var e = new SoundEvent
            {
                Clip = se.Clip,
                ClipName = se.Clip.name,
                OnTimeTimeline = onTimeTimeline,
                OnOutput = onOutput,
                OffOutput = offOutput,
                Loop = se.IsLoop,
                Volume = se.Volume
            };
            VideoExportAudioProviderPlugin.Logger.LogInfo(
                $"Sound '{e.ClipName}' {(e.Loop ? "(loop) " : "")}plays between output {e.OnOutput:0.00}s and {e.OffOutput:0.00}s (enabled at timeline {e.OnTimeTimeline:0.00}s, volume {e.Volume:0.00}).");
            return e;
        }

        #endregion

        #region Mixing

        private static void MixEvent(float[] mix, SoundEvent e, float loopedStart, int sampleRate)
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
            int outStart = Mathf.Clamp(Mathf.RoundToInt(e.OnOutput * sampleRate), 0, totalOut);
            int outEnd = Mathf.Clamp(Mathf.RoundToInt(e.OffOutput * sampleRate), 0, totalOut);
            if (outEnd <= outStart)
                return;

            // How far into the clip playback already is when the window opens. Only a window
            // starting at output 0 can begin mid-clip (the clip was enabled before the export
            // range began); every later window starts fresh at its enabling keyframe.
            float posAtStart = 0f;
            if (e.OnOutput <= 0f)
            {
                if (e.Loop && srcRate > 0)
                    posAtStart = PositiveMod(loopedStart - e.OnTimeTimeline, (float)srcFrames / srcRate) * srcRate;
                else
                    posAtStart = (loopedStart - e.OnTimeTimeline) * srcRate;
                if (posAtStart < 0f)
                    posAtStart = 0f;
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
