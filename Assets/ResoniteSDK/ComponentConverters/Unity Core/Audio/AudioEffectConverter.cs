using FrooxEngine;
using UnityEngine;

// ============================================================================================
// Unity AudioReverbFilter (+ optional AudioEchoFilter) -> Resonite AudioZitaReverb
// ============================================================================================
//
// IMPORTANT / UNVERIFIED (read before touching numbers below):
//  - This converter has NOT been verified against a running Resonite client (Resonite was
//    offline at the time this was written). Compilation correctness and the *logic* of the
//    approximations below have been checked as carefully as possible, but the actual in-world
//    audible result, and even whether AudioZitaReverb works at all when attached standalone to
//    the same Slot as an AudioOutput (i.e. without being routed through an AudioListener /
//    spatial reverb zone the way the only known working sample uses it), is UNCONFIRMED.
//  - Resonite only exposes a single DSP effect component for this purpose: AudioZitaReverb.
//    There is no Resonite equivalent for a low-pass/high-pass/distortion/chorus/compressor/
//    plain-delay-echo filter, and the legacy AudioReverbZone is explicitly documented as only
//    working on non-spatialized audio, so it is intentionally not used here.
//  - AudioZitaReverb's field names (InDelay/Crossover/RT60Low/RT60Mid/HighFrequencyDamping/
//    EQ1Frequency/EQ1Level/EQ2Frequency/EQ2Level/Mix/Level) line up 1:1 with the well-known
//    open-source "Zita-Rev1" reverb algorithm by Fons Adriaensen (delay/xover/rtlow/rtmid/
//    fdamp/eq1/eq2/mix/level), which strongly suggests Resonite's component is a wrapper
//    around that exact algorithm. The mapping below leans on the *publicly documented*
//    semantics of that algorithm (delay=ms pre-delay, xover=Hz low/mid crossover, rtlow/
//    rtmid=seconds decay times per band, fdamp=Hz frequency above which decay shortens,
//    mix=0..1 dry/wet, level=dB output gain). This inference could not be double-checked
//    against Resonite's own source, so treat it as a well-reasoned best guess, not fact.
//
// UNITY SIDE - property meanings were confirmed by decompiling the exact
// UnityEngine.AudioModule.dll shipped with this project's Unity 2022.3.22f1 install
// (Editor/Data/Managed/UnityEngine/UnityEngine.AudioModule.dll), which carries Unity's own
// XML-doc comments compiled into the assembly. AudioReverbFilter mirrors the classic EAX/I3DL2
// reverb parameter set almost verbatim:
//   dryLevel          mB   -10000..0      default 0      dry (unprocessed) signal level
//   room              mB   -10000..0      default 0      overall/mid-band room (wet) level
//   roomHF            mB   -10000..0      default 0      room level attenuation at high freq
//   roomLF            mB   -10000..0      default 0      room level attenuation at low freq
//   decayTime         sec  0.1..20.0      default 1.0    doc string: "decay time AT LOW FREQUENCIES"
//   decayHFRatio      ratio 0.1..2.0      default 0.5    HF decay time relative to decayTime
//   reflectionsLevel  mB   -10000..1000   default -10000 early reflections level (no Zita eq.)
//   reflectionsDelay  sec  0.0..0.3       default 0.0    early reflections delay (no Zita eq.)
//   reverbLevel       mB   -10000..2000   default 0      late reverb level relative to room
//   reverbDelay       sec  0.0..0.1       default 0.04   late reverb delay rel. to direct sound
//   hfReference       Hz   1000..20000    default 5000   reference freq for decayHFRatio/roomHF
//   lfReference       Hz   20..1000       default 250    reference freq for roomLF
//   diffusion / density  %  0..100        (no Zita equivalent, echo density / modal density)
//   reverbPreset      enum (Off, Generic, ... , User)
//
//  NOTE ON reflectionsDelay's DOC STRING: the compiled XML doc for `reflectionsDelay` in this
//  DLL is textually IDENTICAL to `reverbLevel`'s doc ("Late reverberation level relative to
//  room effect in millibels (mB)..."), which is almost certainly a Unity documentation
//  copy/paste bug -- a property literally named "*Delay" being in millibels makes no sense,
//  and every other reverb SDK modeling the I3DL2 spec (which this 1:1 matches by name/range)
//  defines ReflectionsDelay as a TIME value in seconds. reflectionsDelay is treated as a
//  seconds value below (though ultimately unused, since Zita has no early-reflections stage).
//
//  NOTE ON PRESETS: when `reverbPreset != AudioReverbPreset.User`, Unity's native reverb filter
//  applies the preset's built-in parameter table internally. It is UNCONFIRMED whether reading
//  the individual properties (room/decayTime/etc.) back via the C# API returns the *resolved*
//  preset values or stale/default values that were never written back to the managed side, when
//  queried from an Editor (non-Play-mode) script such as this converter. A warning is logged in
//  that case so results can be spot-checked manually; if in doubt, set the preset to `User` in
//  the Inspector before conversion to guarantee the manual fields are what gets read.
//
//  RESONITE-SIDE PRESET INVESTIGATION (2026-07-13, `feature/water-and-audio-presets`):
//  Resonite's AudioZitaReverb exposes 25 "PresetXxx" members including `PresetUnderwater`
//  (matching Unity's `AudioReverbPreset.Underwater`), but every single one of them is a
//  runtime RPC METHOD, not a field. Confirmed two independent ways:
//   1. The checked-in generated binding (BindingsGenerator/.../Generated/Audio/FrooxEngine/
//      AudioZitaReverbWrapper.cs), itself produced from a live ResoniteLink session against
//      Resonite 2026.3.5.946, declares each preset as
//      `public async Task PresetUnderwater(IConversionContext context)` that builds a
//      `ResoniteLink.CallSyncMethod { MethodName = "PresetUnderwater" }` and awaits
//      `context.CallMethod(...)` - i.e. it fires a synced RPC on a live, already-spawned
//      object, and requires a connected session/TargetID.
//   2. `ilspycmd -t FrooxEngine.AudioZitaReverb` against the shipped
//      Assets/ResoniteSDK/Plugins/Resonite.UnitySDK.Bindings.dll independently decompiles to
//      the exact same shape: only 11 float Sync fields exist on the type at all (InDelay,
//      Crossover, RT60Low, RT60Mid, HighFrequencyDamping, EQ1Frequency, EQ1Level,
//      EQ2Frequency, EQ2Level, Mix, Level - the same 11 already used by SetFrom() above); no
//      enum/int/string field selecting a preset exists anywhere on the type.
//  Conclusion: Resonite's PresetUnderwater (and all other AudioZitaReverb presets) CANNOT be
//  applied statically at Editor conversion time - there is nothing to set on the component
//  data before the object is ever synced to a running Resonite session. This is a hard
//  platform limitation, not a gap in this converter; no field-based shortcut exists to wire
//  up here. (For what it's worth, if a future Resonite version adds a preset selector field,
//  the correct place to apply it would be right here in SetFrom(), gated on
//  `unityReverb.reverbPreset == AudioReverbPreset.Underwater`.)
//  As the next-best thing, Unity's own Underwater preset values were checked against the
//  existing numeric mapping below and flow through it without any special-casing needed:
//  decayTime=1.49 / decayHFRatio=0.1 -> RT60Low=1.49s, RT60Mid=0.149s (a short, heavily
//  damped high-frequency tail, consistent with the "muffled" underwater sound); room=-1000mB /
//  reverbLevel=1700mB -> Level=(-1000+1700)/100=+7dB (a notably loud wet signal, again
//  consistent with Underwater's characteristically dense reverb). Every formula involved
//  (Mathf.Pow, division, Mathf.Clamp01/Mathf.Max) is well-defined for these inputs, so no
//  divide-by-zero/NaN/exception risk exists either. This is a hand-checked arithmetic
//  sanity-check only, not an in-client audible verification (see UNCONFIRMED notes above and
//  in the class-level summary).
//
// ============================================================================================

public static class AudioEffectHelper
{
    /// <summary>
    /// Best-effort mapping from Unity's AudioReverbFilter (and, optionally, a sibling
    /// AudioEchoFilter) to a Resonite AudioZitaReverb. See file header for the reasoning
    /// and caveats behind every formula used here.
    /// </summary>
    public static void SetFrom(this FrooxEngine.AudioZitaReverb resonite, UnityEngine.AudioReverbFilter unityReverb, UnityEngine.AudioEchoFilter unityEcho)
    {
        // Set the basics (Enabled, persistent, etc. - same helper AudioSourceConverter uses)
        resonite.SetFrom((UnityEngine.Behaviour)unityReverb);

        if (unityReverb.reverbPreset != UnityEngine.AudioReverbPreset.User)
        {
            Debug.LogWarning(
                $"AudioReverbFilter on '{unityReverb.gameObject.name}' uses preset " +
                $"'{unityReverb.reverbPreset}' instead of 'User'. Whether Unity's C# property " +
                $"getters resolve to the preset's actual values in Editor scripts (as opposed " +
                $"to stale/default values) is unconfirmed - please verify the converted " +
                $"AudioZitaReverb values manually, or switch the preset to 'User' before " +
                $"converting for a guaranteed-accurate read.");
        }

        // --- RT60Low / RT60Mid -------------------------------------------------------------
        // Unity's own doc string explicitly calls decayTime the decay time "at low-frequencies",
        // so it maps directly onto RT60Low. decayHFRatio then scales that low-frequency decay
        // time to get the higher-frequency (here: "Mid", since Zita has no separate high band)
        // decay time, which is exactly how I3DL2/EAX define DecayHFRatio: HF decay = DecayTime *
        // DecayHFRatio.
        float rt60Low = unityReverb.decayTime;
        float rt60Mid = unityReverb.decayTime * unityReverb.decayHFRatio;

        resonite.RT60Low = rt60Low;
        resonite.RT60Mid = rt60Mid;

        // --- Crossover ------------------------------------------------------------------------
        // Zita-Rev1's "xover" (low/mid crossover) is documented (in its original LADSPA/Ardour
        // form) as a low frequency, typically tens to a few hundred Hz - the same range as
        // Unity's lfReference (20..1000 Hz, default 250). hfReference (1000..20000 Hz) is far
        // too high to be a low/mid crossover point, so lfReference is the unit/range-appropriate
        // choice here, not hfReference.
        resonite.Crossover = unityReverb.lfReference;

        // --- HighFrequencyDamping -------------------------------------------------------------
        // If this component really is Zita-Rev1, "fdamp" is a frequency (typically in the low
        // kHz..~20kHz range) above which the decay time is progressively shortened - i.e. it's
        // functionally the same concept as decayHFRatio (how much faster treble decays vs bass),
        // just expressed as a frequency threshold instead of a ratio. hfReference (1000..20000
        // Hz, default 5000) is the right range for this. decayHFRatio is folded in as a
        // multiplicative bias: ratio < 1 (treble decays faster/more "damped") pulls the damping
        // frequency down (damping kicks in earlier); ratio > 1 pushes it up (less damping,
        // brighter tail). ratio == 1 (no extra HF decay) leaves it at hfReference unchanged.
        // This is a heuristic, not a verified formula - there is no Resonite documentation to
        // confirm HighFrequencyDamping's units/range, so this assumes it behaves like Zita's
        // "fdamp" based purely on the field name and its position alongside Crossover/RT60*.
        resonite.HighFrequencyDamping = Mathf.Max(1f, unityReverb.hfReference * unityReverb.decayHFRatio);

        // --- InDelay ----------------------------------------------------------------------------
        // Zita's "InDelay" is the pre-delay before the reverb algorithm's input, in ms. Unity's
        // reverbDelay is documented (correctly, unlike reflectionsDelay - see file header) as the
        // "late reverberation delay time relative to first reflection", i.e. also a pre-delay for
        // the main reverb tail. Same concept, different unit (Unity: seconds, Resonite: ms).
        // reflectionsDelay is deliberately NOT used - it represents early-reflections timing,
        // a discrete early-reflections stage that the Zita-Rev1 algorithm doesn't model at all.
        float inDelayMs = unityReverb.reverbDelay * 1000f;

        // --- Mix --------------------------------------------------------------------------------
        // Zita's Mix is documented (by the task spec) as a plain 0..1 dry/wet blend. Unity has no
        // single equivalent value - the closest is `dryLevel`, an attenuation of the DRY signal in
        // millibels (mB, where 100 mB = 1 dB). At dryLevel = 0 mB the dry signal passes through at
        // full volume (implying a mostly-dry mix); as dryLevel drops toward -10000 mB the dry
        // signal is attenuated toward silence (implying an effectively fully-wet mix). Converting
        // the mB attenuation to a linear gain and treating (1 - dryGainLinear) as the wet
        // proportion gives a reasonable, monotonic approximation:
        //   dryLevel_dB = dryLevel / 100                (millibel -> decibel)
        //   dryGainLinear = 10 ^ (dryLevel_dB / 20)      (dB -> linear amplitude)
        //   mix ~= 1 - dryGainLinear
        // This is an approximation, not an exact inverse of Unity's actual DSP mixing - Unity's
        // dryLevel and Resonite's Mix are not the same parameter, just perceptually similar knobs.
        float dryLevelDb = unityReverb.dryLevel / 100f;
        float dryGainLinear = Mathf.Pow(10f, dryLevelDb / 20f);
        float mix = Mathf.Clamp01(1f - dryGainLinear);

        // --- Level ------------------------------------------------------------------------------
        // Zita's Level is documented (by the task spec) as an overall output gain in dB. Unity's
        // `room` (I3DL2 "Room": overall/mid-band room effect level, mB) and `reverbLevel` (I3DL2
        // "Reverb": late-reverb level *relative to Room*, mB) are the two Unity parameters that
        // together describe the overall loudness of the wet signal. Summing them before
        // converting mB -> dB is a first-order approximation of "how loud the reverb effect is in
        // total", since Reverb is defined relative to Room rather than as an independent value.
        float levelDb = (unityReverb.room + unityReverb.reverbLevel) / 100f;

        // NOTE: roomHF, roomLF, reflectionsLevel, reflectionsDelay, diffusion and density are
        // intentionally NOT converted. Zita-Rev1 has no discrete early-reflections stage (so
        // reflectionsLevel/reflectionsDelay have nothing to map to) and no separate frequency-
        // dependent room gain beyond the RT60Low/RT60Mid split and single HighFrequencyDamping
        // point (so roomHF/roomLF have nothing to map to either). diffusion/density (echo
        // density / modal density, in percent) have no Zita-Rev1 equivalent at all. EQ1/EQ2 are
        // likewise left at their Resonite defaults - Unity's AudioReverbFilter has no parametric
        // EQ stage to source values from.

        // --- Optional bonus: fold in a sibling AudioEchoFilter, if present ----------------------
        // This is a rough, admittedly non-physical approximation: Unity's Echo filter produces
        // discrete, decaying repeats of the dry signal, while Zita-Rev1 produces a continuous
        // diffuse tail - they sound quite different by nature. This block only nudges the already
        //-computed reverb parameters so an Echo-filter setup doesn't get silently dropped; it does
        // NOT attempt to faithfully reproduce discrete echo taps.
        if (unityEcho != null)
        {
            // Unity's Echo `delay` is already in ms (doc: "Echo delay in ms. 10 to 5000."), so no
            // unit conversion is needed here. Take the larger of the two candidate delays so the
            // audible gap before the effect starts isn't shorter than the echo's own delay.
            inDelayMs = Mathf.Max(inDelayMs, unityEcho.delay);

            // decayRatio (0 = single repeat/fast total decay, 1 = no decay/near-infinite ringing)
            // is used to stretch the decay tail so a "long, ringing echo" setup still produces a
            // correspondingly long-sounding reverb tail. Factor range [1x .. 5x] chosen
            // arbitrarily to keep the result in a plausible RT60 range without exploding it.
            float echoExtension = 1f + Mathf.Clamp01(unityEcho.decayRatio) * 4f;
            rt60Low *= echoExtension;
            rt60Mid *= echoExtension;

            resonite.RT60Low = rt60Low;
            resonite.RT60Mid = rt60Mid;

            // Echo's dryMix/wetMix are independent 0..1 sliders (not normalized to sum to 1), so
            // derive an approximate wet proportion from their ratio, then use it only if it calls
            // for a wetter mix than what the reverb filter alone produced.
            float echoTotal = unityEcho.dryMix + unityEcho.wetMix;
            if (echoTotal > 0.0001f)
            {
                float echoWetProportion = unityEcho.wetMix / echoTotal;
                mix = Mathf.Max(mix, echoWetProportion);
            }
        }

        resonite.InDelay = inDelayMs;
        resonite.Mix = mix;
        resonite.Level = levelDb;
    }
}

public class AudioEffectConverter : ResoniteComponentConverter<AudioReverbFilter>
{
    public AudioZitaReverbWrapper Reverb;

    protected override void UpdateConversion(AudioReverbFilter target, IConversionContext context)
    {
        // Scope of this converter (per spec): only handle AudioReverbFilter (+ optional sibling
        // AudioEchoFilter) that live on the same GameObject as an AudioSource, since that's the
        // only configuration where Unity's built-in audio filter chain (and, by assumption, the
        // AudioZitaReverb attachment pattern used here) is meaningful.
        var unityAudioSource = GetComponent<UnityEngine.AudioSource>();

        if (unityAudioSource == null)
        {
            Debug.LogWarning(
                $"AudioReverbFilter on '{target.gameObject.name}' has no sibling AudioSource - " +
                $"skipping conversion. This converter only supports reverb filters attached " +
                $"alongside an AudioSource.");
            return;
        }

        // AudioSourceConverter (which handles the sibling AudioSource) is expected to have
        // already created the AudioOutputWrapper on this same GameObject by the time this runs -
        // Unity's [RequireComponent(typeof(AudioBehaviour))] on AudioReverbFilter means an
        // AudioSource normally has to be added first, so it will appear earlier in the component
        // list and get converted first within the same conversion pass. If it hasn't happened yet
        // (e.g. unusual component ordering), skip for now; UpdateConversion runs again on every
        // subsequent conversion pass, so this will pick it up as soon as it's available.
        var output = GetComponent<AudioOutputWrapper>();

        if (output == null)
        {
            Debug.LogWarning(
                $"AudioReverbFilter on '{target.gameObject.name}': no AudioOutputWrapper found " +
                $"yet (sibling AudioSource not converted yet?) - will retry on next conversion " +
                $"pass.");
            return;
        }

        if (Reverb == null)
            Reverb = gameObject.AddComponent<AudioZitaReverbWrapper>();

        // UNVERIFIED (see file header): AudioZitaReverb is simply added to the same Slot as the
        // AudioOutput, with no explicit reference wiring it to that AudioOutput - AudioDSP_Effect
        // exposes no such reference field. Whether Resonite actually picks up a standalone
        // AudioZitaReverb this way (as opposed to only working through an AudioListener/spatial
        // reverb zone setup) has not been confirmed against a running client.
        var unityEcho = GetComponent<UnityEngine.AudioEchoFilter>();

        Reverb.Data.SetFrom(target, unityEcho);
    }

    protected override void Cleanup()
    {
        if (Reverb != null)
            DestroyImmediate(Reverb);
    }
}
