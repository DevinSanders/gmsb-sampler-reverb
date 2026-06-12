using System.Text.Json;
using FluentAssertions;
using NAudio.Wave;
using ReverbPlugin;
using SoundBoard.PluginApi;
using Xunit;

namespace ReverbPlugin.Tests;

// Stateful-effect bypass caveat: the reverb owns comb-filter delay lines and
// allpass histories. The host wraps this in BypassableSamplerInstance, which
// freezes the wet chain while bypassed — so on un-bypass you'll briefly hear
// stale tail from when bypass engaged. The tests below exercise the wet DSP
// directly via CreateEffect (bypassing the wrapper); the freeze is a host-tier
// concern documented in the main app's CLAUDE.md §"FX Chain v1 limitations."

/// <summary>
/// Tests drive the plugin entirely through its public contract
/// (<see cref="IAudioSamplerPlugin"/> / <see cref="ISamplerInstance"/>) —
/// the same surface the host uses. The DSP internals (Comb / Allpass /
/// ReverbSampleProvider) stay <c>internal</c>; we assert on observable
/// behaviour, not on private structure.
///
/// <para><see cref="ISamplerInstance.CreateControl"/> is intentionally NOT
/// exercised: it builds Avalonia controls, which need a running Avalonia
/// app, and the plugin excludes Avalonia's runtime asset by design.</para>
/// </summary>
public class ReverbTests
{
    // The host mixer format, per the plugin contract.
    private static readonly WaveFormat HostFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private static ISamplerInstance NewInstance() => new ReverbPlugin().CreateInstance();

    // ── Factory ───────────────────────────────────────────────────────

    [Fact]
    public void CreateInstance_returns_distinct_instances()
    {
        var plugin = new ReverbPlugin();
        var a = plugin.CreateInstance();
        var b = plugin.CreateInstance();
        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void Factory_metadata_matches_manifest()
    {
        var plugin = new ReverbPlugin();
        plugin.Id.Should().Be("sampler.reverb");
        plugin.SupportedAttachments.Should().Be(SamplerAttachmentPoints.All);
        plugin.Version.Should().NotBeNullOrWhiteSpace(); // PluginVersion.OfAssembly
    }

    [Fact]
    public void Two_instances_share_no_dsp_state()
    {
        // Distinct CreateEffect chains fed the same impulse must produce
        // identical output — proving neither instance's reverb tail bleeds
        // into the other.
        var fxA = NewWetInstance("cathedral").CreateEffect(new ImpulseProvider(HostFormat));
        var fxB = NewWetInstance("cathedral").CreateEffect(new ImpulseProvider(HostFormat));

        var bufA = ReadAll(fxA, 24000);
        var bufB = ReadAll(fxB, 24000);

        bufA.Should().Equal(bufB);
    }

    // ── Config round-trip ───────────────────────────────────────────────

    [Fact]
    public void Default_config_is_room_preset_at_30_percent()
    {
        using var doc = JsonDocument.Parse(NewInstance().SerializeConfig());
        doc.RootElement.GetProperty("Preset").GetString().Should().Be("room");
        doc.RootElement.GetProperty("WetDry").GetSingle().Should().BeApproximately(0.30f, 1e-6f);
    }

    [Fact]
    public void DeserializeConfig_round_trips_every_knob()
    {
        var inst = NewInstance();
        inst.DeserializeConfig("""{"Preset":"cathedral","WetDry":0.75}""");

        using var doc = JsonDocument.Parse(inst.SerializeConfig());
        doc.RootElement.GetProperty("Preset").GetString().Should().Be("cathedral");
        doc.RootElement.GetProperty("WetDry").GetSingle().Should().BeApproximately(0.75f, 1e-6f);
    }

    [Theory]
    [InlineData(5.0, 1.0)]
    [InlineData(-2.0, 0.0)]
    public void WetDry_clamps_to_unit_range(double input, double expected)
    {
        var inst = NewInstance();
        inst.DeserializeConfig($$"""{"Preset":"room","WetDry":{{input}}}""");

        using var doc = JsonDocument.Parse(inst.SerializeConfig());
        doc.RootElement.GetProperty("WetDry").GetSingle().Should().BeApproximately((float)expected, 1e-6f);
    }

    [Fact]
    public void Unknown_preset_id_falls_back_to_room()
    {
        var inst = NewInstance();
        inst.DeserializeConfig("""{"Preset":"does-not-exist","WetDry":0.5}""");

        using var doc = JsonDocument.Parse(inst.SerializeConfig());
        doc.RootElement.GetProperty("Preset").GetString().Should().Be("room");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ this is : broken")]
    [InlineData("null")]
    public void DeserializeConfig_tolerates_garbage_without_throwing(string json)
    {
        var inst = NewInstance();
        var act = () => inst.DeserializeConfig(json);
        act.Should().NotThrow();

        // State is left at defaults.
        using var doc = JsonDocument.Parse(inst.SerializeConfig());
        doc.RootElement.GetProperty("Preset").GetString().Should().Be("room");
    }

    // ── CreateEffect: format + behaviour ────────────────────────────────

    [Fact]
    public void CreateEffect_preserves_wave_format()
    {
        var fx = NewInstance().CreateEffect(new ImpulseProvider(HostFormat));
        fx.WaveFormat.SampleRate.Should().Be(48000);
        fx.WaveFormat.Channels.Should().Be(2);
        fx.WaveFormat.Encoding.Should().Be(WaveFormatEncoding.IeeeFloat);
    }

    [Fact]
    public void Non_stereo_source_is_passed_through_unwrapped()
    {
        var mono = new ImpulseProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        var fx = NewInstance().CreateEffect(mono);
        fx.Should().BeSameAs(mono);
    }

    [Fact]
    public void FullyWet_impulse_produces_a_reverb_tail()
    {
        // wet=1 → dry contribution is zero, so any energy AFTER the initial
        // transient must be the comb/allpass tail. The shortest comb delay
        // is ~1116 samples @44.1k → ~1214 @48k, so look well past that.
        var fx = NewWetInstance("cathedral").CreateEffect(new ImpulseProvider(HostFormat));
        var buf = ReadAll(fx, 48000); // 0.5 s

        double tail = 0;
        for (int i = 4000; i < buf.Length; i++) tail += Math.Abs(buf[i]);

        tail.Should().BeGreaterThan(0.01, "a reverb must ring out after the impulse");
    }

    [Fact]
    public void FullyDry_passes_the_signal_through_unchanged()
    {
        var inst = NewInstance();
        inst.DeserializeConfig("""{"Preset":"cathedral","WetDry":0.0}""");
        var fx = inst.CreateEffect(new ImpulseProvider(HostFormat));

        var buf = ReadAll(fx, 8000);

        // The impulse (1,1) survives intact and nothing else is added.
        buf[0].Should().Be(1f);
        buf[1].Should().Be(1f);
        for (int i = 2; i < buf.Length; i++)
            buf[i].Should().Be(0f, "wet=0 must add no reverb energy");
    }

    [Fact]
    public void Wetter_mix_produces_more_tail_energy_than_drier_mix()
    {
        double Energy(float wet)
        {
            var inst = NewInstance();
            inst.DeserializeConfig($$"""{"Preset":"hall","WetDry":{{wet}}}""");
            var buf = ReadAll(inst.CreateEffect(new ImpulseProvider(HostFormat)), 48000);
            double e = 0;
            for (int i = 4000; i < buf.Length; i++) e += Math.Abs(buf[i]);
            return e;
        }

        Energy(0.8f).Should().BeGreaterThan(Energy(0.2f));
    }

    // ── Thread-safety smoke ─────────────────────────────────────────────

    [Fact]
    public void Live_config_push_during_read_never_throws()
    {
        var inst = NewInstance();
        var fx = inst.CreateEffect(new ImpulseProvider(HostFormat));
        var buf = new float[512];
        var presets = new[] { "cathedral", "cave", "hall", "room", "small-room" };

        Exception? captured = null;
        using var stop = new ManualResetEventSlim(false);

        var pusher = new Thread(() =>
        {
            var rng = new Random(1234);
            try
            {
                while (!stop.IsSet)
                {
                    var p = presets[rng.Next(presets.Length)];
                    var w = rng.NextSingle();
                    inst.DeserializeConfig($$"""{"Preset":"{{p}}","WetDry":{{w}}}""");
                }
            }
            catch (Exception ex) { captured = ex; }
        });
        pusher.Start();

        for (int i = 0; i < 5000; i++)
            fx.Read(buf, 0, buf.Length);

        stop.Set();
        pusher.Join();

        captured.Should().BeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static ISamplerInstance NewWetInstance(string preset)
    {
        var inst = NewInstance();
        inst.DeserializeConfig($$"""{"Preset":"{{preset}}","WetDry":1.0}""");
        return inst;
    }

    private static float[] ReadAll(ISampleProvider fx, int frames)
    {
        var buf = new float[frames * 2];
        int total = 0;
        while (total < buf.Length)
        {
            int n = fx.Read(buf, total, buf.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return buf;
    }

    /// <summary>A single full-scale stereo impulse on frame 0, silence after.</summary>
    private sealed class ImpulseProvider(WaveFormat fmt) : ISampleProvider
    {
        private long _n;
        public WaveFormat WaveFormat => fmt;
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[offset + i] = _n < fmt.Channels ? 1f : 0f;
                _n++;
            }
            return count;
        }
    }
}
