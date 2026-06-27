using System;

namespace Terryball;

/// <summary>
/// Procedural impact-sound synthesiser for the pins — pure DSP, no engine dependency. Given a
/// <see cref="Recipe"/> (a small set of damped resonant modes) it renders a wooden "clack" into a
/// 16-bit PCM buffer. This is deliberately the ONLY place the actual sound is generated.
///
/// Reuse boundary: <see cref="Render"/> is a pure function — caller supplies the destination span,
/// it fills it and returns the sample count, allocating nothing. The live path
/// (<see cref="PinImpactAudio"/> in Live mode) calls it once per impact into a shared scratch buffer.
/// If we later switch to option #2 (bake a fixed bank at load and just play it back), the exact same
/// call renders each variation once into a stored buffer at startup — no synthesis code changes, only
/// who holds the buffers. See <see cref="PinImpactAudio.Mode"/>.
///
/// A struck maple pin is, acoustically, a few heavily-damped resonant modes excited by a short
/// contact transient. We model that directly: a brief filtered-noise burst (the strike) drives a
/// bank of two-pole resonators (the modes). Exciting the resonances with broadband noise — rather
/// than ringing clean seeded sinusoids — is what gives the woody/struck character instead of a
/// metallic bell tone. The inner loop is a few multiply-adds per mode (no per-sample
/// <c>sin</c>/<c>exp</c>), so a full tail generates in well under a millisecond.
/// </summary>
public static class PinSynth
{
	public const int SampleRate = 44100;

	/// <summary>A resonant mode: a decaying sinusoid. Frequencies are intentionally inharmonic
	/// (not integer ratios) — that is what reads as "wood" rather than "bell".</summary>
	public readonly record struct Mode( float Freq, float Gain, float Decay );

	/// <summary>One material's voice: its modes plus a short filtered-noise contact transient.</summary>
	public readonly struct Recipe
	{
		public readonly Mode[] Modes;
		public readonly float NoiseSeconds;   // length of the contact "click"
		public readonly float NoiseGain;
		public readonly float NoiseCutoff01;  // one-pole LP coefficient (0..1), lower = duller

		public Recipe( Mode[] modes, float noiseSeconds, float noiseGain, float noiseCutoff01 )
		{
			Modes = modes;
			NoiseSeconds = noiseSeconds;
			NoiseGain = noiseGain;
			NoiseCutoff01 = noiseCutoff01;
		}

		/// <summary>Longest mode decay — used to size how much tail to render.</summary>
		public float MaxDecay
		{
			get
			{
				float m = 0f;
				foreach ( var mode in Modes ) if ( mode.Decay > m ) m = mode.Decay;
				return m;
			}
		}
	}

	// Maple-on-maple: a hard, woody "clack". Decays are short and strongly frequency-dependent —
	// real wood damps its high partials almost instantly, so the top modes are barely more than the
	// contact "tick" while a low body resonance gives the brief knock. The modes are excited by the
	// noise burst (see Render), which is what keeps it woody instead of bell-like.
	public static readonly Recipe PinOnPin = new(
		new[]
		{
			// Energy sits in the MIDS (the wood "crack"), not a single dominant low resonance — a
			// lone low mode rings like a hollow jug. Decays are short so nothing reads as a pitch.
			new Mode( 540f,  0.45f, 0.020f ),
			new Mode( 980f,  0.70f, 0.018f ),
			new Mode( 1650f, 1.00f, 0.013f ), // dominant crack
			new Mode( 2700f, 0.75f, 0.009f ),
			new Mode( 4300f, 0.45f, 0.005f ),
			new Mode( 6400f, 0.22f, 0.003f ), // bright snap of the contact
		},
		noiseSeconds: 0.006f, noiseGain: 0.9f, noiseCutoff01: 0.85f );

	// Pin against the lane bed / deck / gutter: a duller, shorter thud — lower body, faster decay,
	// darker strike noise.
	public static readonly Recipe PinOnStructure = new(
		new[]
		{
			new Mode( 150f, 1.00f, 0.045f ),
			new Mode( 300f, 0.60f, 0.028f ),
			new Mode( 620f, 0.30f, 0.014f ),
		},
		noiseSeconds: 0.014f, noiseGain: 1.0f, noiseCutoff01: 0.22f );

	/// <summary>
	/// Render one impact into <paramref name="dest"/> and return the number of samples written
	/// (mono, 16-bit). Allocates nothing.
	/// </summary>
	/// <param name="recipe">Which material voice to use.</param>
	/// <param name="amplitude">Overall level, 0..1, from the impact strength.</param>
	/// <param name="pitch">Frequency/decay scaler — harder/lighter hits and per-hit variation. 1 = nominal.</param>
	/// <param name="seed">Deterministic per-hit jitter (so no two clacks are identical).</param>
	public static int Render( in Recipe recipe, float amplitude, float pitch, int seed, Span<short> dest )
	{
		amplitude = Math.Clamp( amplitude, 0f, 1f );
		if ( pitch < 0.25f ) pitch = 0.25f;

		// Harder hits read brighter and a touch shorter; jitter pitch slightly per hit.
		var rng = new Rng( (uint)seed | 1u );
		float freqJitter = 1f + (rng.Float() - 0.5f) * 0.06f;   // +/-3%
		float decayJitter = 1f + (rng.Float() - 0.5f) * 0.30f;  // +/-15%

		// How long to bother rendering: ~6 time-constants of the longest mode is fully inaudible.
		float tail = recipe.MaxDecay * decayJitter / pitch;
		int count = Math.Min( dest.Length, (int)(tail * 6f * SampleRate) + 1 );
		if ( count <= 0 ) return 0;

		var modes = recipe.Modes;
		int m = modes.Length;
		// Each mode is a two-pole resonator EXCITED BY the contact noise, not a free-ringing sine.
		// Driving the resonances with a broadband strike is what gives the woody/struck character —
		// pure seeded sinusoids are exactly what sounds metallic. State: y1/y2 = last two outputs.
		Span<float> y1 = stackalloc float[m];
		Span<float> y2 = stackalloc float[m];
		Span<float> a1 = stackalloc float[m];   // 2 r cos(w0)
		Span<float> a2 = stackalloc float[m];   // r^2
		Span<float> gain = stackalloc float[m]; // mode level, pre-scaled to normalise the resonator

		for ( int k = 0; k < m; k++ )
		{
			float f = modes[k].Freq * pitch * freqJitter;
			double w = 2.0 * Math.PI * f / SampleRate;
			// pole radius from the target decay time-constant: r = e^{-1/(tau*sr)}
			float tau = modes[k].Decay * decayJitter / pitch;
			float r = (float)Math.Exp( -1.0 / Math.Max( 1e-4, tau * SampleRate ) );
			a1[k] = (float)(2.0 * r * Math.Cos( w ));
			a2[k] = r * r;
			// (1 - r) keeps each resonator's response level roughly equal regardless of decay length
			gain[k] = modes[k].Gain * (1f - r);
			y1[k] = 0f; y2[k] = 0f;
		}

		int noiseSamples = Math.Max( 1, (int)(recipe.NoiseSeconds * SampleRate) );
		float noiseDecay = (float)Math.Exp( -5.0 / noiseSamples ); // sharp strike that fades fast
		float noiseEnv = recipe.NoiseGain;
		float lp = 0f;

		// Normalise so the summed modes sit at a sane level; the soft-clip below catches peaks.
		float norm = 0f;
		for ( int k = 0; k < m; k++ ) norm += modes[k].Gain;
		norm = norm > 0f ? 1f / norm : 1f;

		float dcPrev = 0f, dcOut = 0f;

		for ( int n = 0; n < count; n++ )
		{
			// Excitation: a hard click at t0 plus a few ms of lowpassed noise — the contact transient
			// that strikes every resonance at once.
			float exc = 0f;
			if ( n < noiseSamples )
			{
				float white = rng.Float() * 2f - 1f;
				if ( n == 0 ) white += 1f; // initial impact spike
				lp += recipe.NoiseCutoff01 * (white - lp);
				exc = lp * noiseEnv;
				noiseEnv *= noiseDecay;
			}

			float s = 0f;
			for ( int k = 0; k < m; k++ )
			{
				// two-pole resonator: y = exc + a1*y1 - a2*y2
				float y = exc + a1[k] * y1[k] - a2[k] * y2[k];
				y2[k] = y1[k]; y1[k] = y;
				s += y * gain[k];
			}
			s *= norm;

			// block the resonators' DC leakage so the body modes read as a knock, not a thump-with-bias
			dcOut = s - dcPrev + 0.995f * dcOut;
			dcPrev = s;

			// gentle soft clip then to 16-bit
			float outv = (float)Math.Tanh( dcOut ) * amplitude;
			int v = (int)(outv * 32000f);
			dest[n] = (short)Math.Clamp( v, short.MinValue, short.MaxValue );
		}

		return count;
	}

	// Tiny deterministic xorshift — no allocation, repeatable per seed.
	private struct Rng
	{
		private uint _s;
		public Rng( uint seed ) { _s = seed == 0 ? 0x9E3779B9u : seed; }
		public float Float()
		{
			_s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
			return (_s & 0xFFFFFF) / (float)0x1000000;
		}
	}
}
