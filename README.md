# gmsb-sampler-reverb

Reverb FX plugin for
[Game Master Sound Board](https://github.com/DevinSanders/game-master-soundboard).
Schroeder reverb (4 comb + 2 allpass per channel, Freeverb tunings) with
five presets — Cathedral, Cave, Hall, Room, Small Room — and a single
wet/dry knob. Attach per-shortcut, per-preset, per-playlist, or to the
master bus.

## Install

**Paid plugin.** The source is open here for reference, but the pre-built
binary is distributed pay-what-you-want on itch.io:

**→ https://dsand64.itch.io/gmsb-sampler-reverb**

Download the `.zip` from that page and drop it onto **Settings → Plugin
Manager** in Game Master Sound Board. Restart when prompted, then enable it under **Settings → Plugins**.

## Manifest

| Field     | Value                       |
|-----------|-----------------------------|
| publisher | `github.DevinSanders`       |
| id        | `sampler.reverb`            |
| entryDll  | `ReverbPlugin.dll`          |

## Bypass behavior

This plugin holds DSP state (comb + allpass delay lines). The host's BypassableSamplerInstance wrapper toggles bypass by flipping a flag — it does NOT rebuild the chain. While bypassed the wet path isn't clocked, so on un-bypass you'll briefly hear stale material from the moment bypass engaged. For an interactive soundboard with on-demand FX this is fine; for a long-tail stateful effect (large hall reverb, slow chorus) the artifact is more noticeable. Workaround: leave bypass off and use the wet/dry knob (or the plugin's own dry/wet mix where exposed) to mute the effect contribution.

## License

Released under the [MIT License](LICENSE).

Third-party components used by this plugin:

- Schroeder/Freeverb reverb (Jezar at Dreampoint, public domain — original tunings adapted to per-source sample rate).