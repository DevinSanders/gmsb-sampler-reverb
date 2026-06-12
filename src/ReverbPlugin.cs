using System;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NAudio.Wave;
using SoundBoard.PluginApi;

namespace ReverbPlugin;

/// <summary>
/// Reverb FX plugin for Game Master Sound Board. Adds spatial ambience
/// to any audio source the host plays. Implemented as a Schroeder reverb
/// (4 parallel comb filters + 2 series allpass filters per channel)
/// derived from the Freeverb topology.
///
/// <para>Knobs: a <b>preset selector</b> (Cathedral / Cave / Hall / Room /
/// Small Room) and a <b>wet/dry mix</b> (0..1, default 0.3). Both are
/// live-editable while audio is flowing — the wet/dry knob is published
/// via a volatile float bit-pattern, the preset via an immutable record
/// reference, and the audio thread reads both at the top of every
/// <c>Read</c> buffer.</para>
/// </summary>
public sealed class ReverbPlugin : IAudioSamplerPlugin
{
    public string Id => "sampler.reverb";
    public string Name => "Reverb";
    public string Description => "Adds spatial ambience (Cathedral / Cave / Hall / Room / Small Room). Single wet/dry knob.";
    public string Version => PluginVersion.OfAssembly(typeof(ReverbPlugin));
    public string Author => "Devin Sanders";

    public SamplerAttachmentPoints SupportedAttachments => SamplerAttachmentPoints.All;

    public void Initialize(IPluginContext context) { }
    public void Shutdown() { }

    public ISamplerInstance CreateInstance() => new ReverbInstance();
}

/// <summary>
/// Immutable per-preset DSP coefficient set. Swap the whole record
/// reference via <see cref="System.Threading.Volatile.Write{T}(ref T, T)"/>
/// when the user changes preset; never mutate fields in place — the audio
/// thread may be reading.
/// </summary>
internal sealed record ReverbPreset(
    string Id,
    string DisplayName,
    float CombFeedback,
    float Damping)
{
    public static readonly ReverbPreset Cathedral  = new("cathedral",  "Cathedral",  0.95f, 0.10f);
    public static readonly ReverbPreset Cave       = new("cave",       "Cave",       0.92f, 0.05f);
    public static readonly ReverbPreset Hall       = new("hall",       "Hall",       0.90f, 0.20f);
    public static readonly ReverbPreset Room       = new("room",       "Room",       0.85f, 0.30f);
    public static readonly ReverbPreset SmallRoom  = new("small-room", "Small Room", 0.78f, 0.40f);

    public static readonly ReverbPreset[] All = { Cathedral, Cave, Hall, Room, SmallRoom };

    public static ReverbPreset FromId(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? Room;
}

internal sealed class ReverbInstance : ISamplerInstance
{
    // ── Live-editable state, published from the UI thread, read from the audio thread.
    //
    // _wetDryBits is the IEEE-754 bit pattern of a float in [0,1]. We use
    // an int + Volatile to get atomic publish without locking — see the
    // host's Attenuator example.
    private int _wetDryBits = BitConverter.SingleToInt32Bits(0.30f);

    // _preset is an immutable record; swap the reference, never mutate.
    private ReverbPreset _preset = ReverbPreset.Room;

    public float GetWetDry() => BitConverter.Int32BitsToSingle(System.Threading.Volatile.Read(ref _wetDryBits));
    public void SetWetDry(float value)
    {
        if (float.IsNaN(value)) value = 0f;
        value = Math.Clamp(value, 0f, 1f);
        System.Threading.Volatile.Write(ref _wetDryBits, BitConverter.SingleToInt32Bits(value));
    }

    public ReverbPreset GetPreset() => System.Threading.Volatile.Read(ref _preset!);
    public void SetPreset(ReverbPreset preset)
    {
        if (preset is null) return;
        System.Threading.Volatile.Write(ref _preset!, preset);
    }

    public string SerializeConfig()
    {
        var dto = new ConfigDto { Preset = GetPreset().Id, WetDry = GetWetDry() };
        return JsonSerializer.Serialize(dto);
    }

    public void DeserializeConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        ConfigDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ConfigDto>(json);
        }
        catch (JsonException)
        {
            return; // malformed — keep defaults
        }
        if (dto is null) return;
        SetPreset(ReverbPreset.FromId(dto.Preset));
        SetWetDry(dto.WetDry);
    }

    public ISampleProvider CreateEffect(ISampleProvider source)
    {
        // The host contract is stereo IEEE-float. If something off-contract
        // arrives we pass through rather than scrambling channels.
        //
        // Log a warning when we hit this path so a confused user
        // ("why doesn't my reverb work?") has a breadcrumb in the
        // log. The host normalises everything to stereo before the FX
        // chain in practice, so this branch only fires if a plugin
        // earlier in the chain dropped channels — which is itself
        // a bug worth noticing.
        if (source.WaveFormat.Channels != 2)
        {
            Console.WriteLine($"[gmsb-sampler-reverb] Source is {source.WaveFormat.Channels}-channel; reverb requires stereo and is bypassing for this instance.");
            return source;
        }
        return new ReverbSampleProvider(source, this);
    }

    public object? CreateControl()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(8),
            Spacing = 6,
            MinWidth = 220,
        };

        // Preset selector.
        panel.Children.Add(new TextBlock { Text = "Preset" });
        var combo = new ComboBox
        {
            ItemsSource = ReverbPreset.All.Select(p => p.DisplayName).ToList(),
            SelectedIndex = Array.IndexOf(ReverbPreset.All, GetPreset()),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        combo.SelectionChanged += (_, _) =>
        {
            var i = combo.SelectedIndex;
            if (i >= 0 && i < ReverbPreset.All.Length)
                SetPreset(ReverbPreset.All[i]);
        };
        panel.Children.Add(combo);

        // Wet/dry slider.
        var label = new TextBlock { Text = FormatWetDryLabel(GetWetDry()) };
        panel.Children.Add(label);
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = GetWetDry(),
            TickFrequency = 0.05,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        slider.ValueChanged += (_, e) =>
        {
            var v = (float)e.NewValue;
            SetWetDry(v);
            label.Text = FormatWetDryLabel(v);
        };
        panel.Children.Add(slider);

        return panel;
    }

    public void Dispose() { }

    private static string FormatWetDryLabel(float v) =>
        $"Wet/Dry: {(int)Math.Round(v * 100)}%";

    private sealed class ConfigDto
    {
        public string Preset { get; set; } = ReverbPreset.Room.Id;
        public float WetDry { get; set; } = 0.30f;
    }
}

/// <summary>
/// Audio-thread Schroeder reverb. All delay buffers are pre-allocated in
/// the ctor; <see cref="Read"/> allocates nothing. Per-buffer it snapshots
/// the wet/dry knob and preset coefficients from <see cref="ReverbInstance"/>
/// and applies them uniformly across the buffer (one volatile read per
/// buffer, not per sample).
/// </summary>
internal sealed class ReverbSampleProvider : ISampleProvider
{
    // Freeverb tunings (samples at 44.1 kHz). Scaled to the source sample
    // rate at construction. Right-channel taps are offset by stereoSpread
    // to decorrelate the two channels — the perceptual cue for "width".
    //
    // These constants come from Jezar at Dreampoint's original Freeverb
    // implementation (released into the public domain), as referenced by
    // CCRMA's Physical Audio Signal Processing course materials. Algorithm
    // attribution: https://ccrma.stanford.edu/~jos/pasp/Freeverb.html
    private static readonly int[] CombTunings44k    = { 1116, 1188, 1277, 1356 };
    private static readonly int[] AllpassTunings44k = { 556, 441 };
    private const int StereoSpread44k = 23;

    // Freeverb's "fixedgain" — keeps the recursive comb filters from
    // saturating at high feedback values.
    private const float InputGain = 0.015f;

    private readonly ISampleProvider _source;
    private readonly ReverbInstance _owner;

    private readonly Comb[] _combsL;
    private readonly Comb[] _combsR;
    private readonly Allpass[] _allpassesL;
    private readonly Allpass[] _allpassesR;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public ReverbSampleProvider(ISampleProvider source, ReverbInstance owner)
    {
        _source = source;
        _owner = owner;

        var sr = source.WaveFormat.SampleRate;
        // Scale Freeverb's 44.1 kHz tunings to whatever the host actually
        // hands us. Host contract is 48 kHz but we pay nothing to be
        // sample-rate agnostic.
        int Scale(int n44) => Math.Max(1, (int)Math.Round(n44 * (double)sr / 44100.0));
        int stereoSpread = Scale(StereoSpread44k);

        _combsL = new Comb[CombTunings44k.Length];
        _combsR = new Comb[CombTunings44k.Length];
        for (int i = 0; i < CombTunings44k.Length; i++)
        {
            _combsL[i] = new Comb(Scale(CombTunings44k[i]));
            _combsR[i] = new Comb(Scale(CombTunings44k[i]) + stereoSpread);
        }

        _allpassesL = new Allpass[AllpassTunings44k.Length];
        _allpassesR = new Allpass[AllpassTunings44k.Length];
        for (int i = 0; i < AllpassTunings44k.Length; i++)
        {
            _allpassesL[i] = new Allpass(Scale(AllpassTunings44k[i]));
            _allpassesR[i] = new Allpass(Scale(AllpassTunings44k[i]) + stereoSpread);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _source.Read(buffer, offset, count);
        if (n <= 0) return n;

        // Snapshot live-editable state once per buffer. Keeping these as
        // locals also lets the JIT hoist them into registers across the
        // inner loop.
        float wet = _owner.GetWetDry();
        float dry = 1f - wet;
        var preset = _owner.GetPreset();
        float feedback = preset.CombFeedback;
        float damping  = preset.Damping;

        // Process in stereo pairs. If the source returns an odd sample
        // count (it shouldn't on a stereo provider) we leave the trailing
        // sample untouched.
        int end = offset + (n & ~1);
        for (int i = offset; i < end; i += 2)
        {
            float inL = buffer[i];
            float inR = buffer[i + 1];
            // Mono-summed input feeds both wet networks (classic Freeverb
            // wiring). Stereo width comes from the offset delay-line taps.
            float input = (inL + inR) * InputGain;

            float wetL = 0f;
            float wetR = 0f;
            for (int c = 0; c < _combsL.Length; c++)
            {
                wetL += _combsL[c].Process(input, feedback, damping);
                wetR += _combsR[c].Process(input, feedback, damping);
            }
            for (int a = 0; a < _allpassesL.Length; a++)
            {
                wetL = _allpassesL[a].Process(wetL);
                wetR = _allpassesR[a].Process(wetR);
            }

            buffer[i]     = inL * dry + wetL * wet;
            buffer[i + 1] = inR * dry + wetR * wet;
        }

        return n;
    }
}

/// <summary>
/// Lowpass-feedback comb filter (the recursive workhorse of Schroeder /
/// Freeverb reverb). One per channel per tap. Owned and clocked
/// exclusively by the audio thread; carries no thread-safety burden.
/// </summary>
internal sealed class Comb
{
    private readonly float[] _buf;
    private int _idx;
    private float _filterStore;

    public Comb(int size) { _buf = new float[size]; }

    public float Process(float input, float feedback, float damping)
    {
        float output = _buf[_idx];
        // One-pole lowpass inside the feedback loop — the "damping" knob.
        // High damping rolls off high frequencies on each pass, simulating
        // a room with absorbent walls.
        _filterStore = output * (1f - damping) + _filterStore * damping;
        _buf[_idx] = input + _filterStore * feedback;
        if (++_idx >= _buf.Length) _idx = 0;
        return output;
    }
}

/// <summary>
/// Schroeder allpass section. Diffuses the comb-filter sum into a denser
/// echo pattern without changing the frequency response (in theory; the
/// Freeverb topology is a near-allpass — close enough for music).
/// </summary>
internal sealed class Allpass
{
    private const float Feedback = 0.5f;
    private readonly float[] _buf;
    private int _idx;

    public Allpass(int size) { _buf = new float[size]; }

    public float Process(float input)
    {
        float bufout = _buf[_idx];
        float output = -input + bufout;
        _buf[_idx] = input + bufout * Feedback;
        if (++_idx >= _buf.Length) _idx = 0;
        return output;
    }
}
