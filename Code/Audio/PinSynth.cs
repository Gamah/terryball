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
/// A struck maple pin is, acoustically, a handful of decaying sinusoids (its resonant modes) plus a
/// short contact transient. We synthesise each mode with a recursive complex rotator multiplied by a
/// per-sample exponential decay, so the inner loop is a few multiply-adds per mode — no per-sample
/// <c>sin</c>/<c>exp</c>. That keeps a full ~0.2s tail well under a millisecond to generate.
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

	// Maple-on-maple: the bright, hollow pin-on-pin clack. A low body thunk plus several upper
	// partials that decay fast, so it cracks then rings briefly.
	public static readonly Recipe PinOnPin = new(
		new[]
		{
			new Mode( 420f,  0.50f, 0.120f ),
			new Mode( 1300f, 1.00f, 0.090f ),
			new Mode( 2150f, 0.70f, 0.060f ),
			new Mode( 3400f, 0.45f, 0.040f ),
			new Mode( 5200f, 0.25f, 0.025f ),
		},
		noiseSeconds: 0.006f, noiseGain: 0.6f, noiseCutoff01: 0.55f );

	// Pin against the lane bed / deck / gutter: a duller, shorter thud — lower partials, heavier
	// transient, darker noise.
	public static readonly Recipe PinOnStructure = new(
		new[]
		{
			new Mode( 180f, 1.00f, 0.080f ),
			new Mode( 320f, 0.60f, 0.060f ),
			new Mode( 700f, 0.35f, 0.035f ),
		},
		noiseSeconds: 0.010f, noiseGain: 0.8f, noiseCutoff01: 0.25f );

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
		Span<float> re = stackalloc float[m];
		Span<float> im = stackalloc float[m];
		Span<float> cos = stackalloc float[m];
		Span<float> sin = stackalloc float[m];
		Span<float> dec = stackalloc float[m];
		Span<float> gain = stackalloc float[m];

		for ( int k = 0; k < m; k++ )
		{
			float f = modes[k].Freq * pitch * freqJitter;
			double w = 2.0 * Math.PI * f / SampleRate;
			cos[k] = (float)Math.Cos( w );
			sin[k] = (float)Math.Sin( w );
			// e^{-1/(tau*sr)} : per-sample decay so the rotator's magnitude follows e^{-t/tau}.
			float tau = modes[k].Decay * decayJitter / pitch;
			dec[k] = (float)Math.Exp( -1.0 / Math.Max( 1e-4, tau * SampleRate ) );
			gain[k] = modes[k].Gain;
			re[k] = 1f; // z[0] = 1 -> Im = 0, so each mode's sinusoid starts at the contact instant
			im[k] = 0f;
		}

		int noiseSamples = (int)(recipe.NoiseSeconds * SampleRate);
		float noiseDecay = noiseSamples > 0 ? (float)Math.Exp( -4.0 / noiseSamples ) : 0f;
		float noiseEnv = recipe.NoiseGain;
		float lp = 0f;

		// Normalise so the summed mode gains can't routinely clip; the soft-clip below catches peaks.
		float norm = 0f;
		for ( int k = 0; k < m; k++ ) norm += gain[k];
		norm = norm > 0f ? 1f / norm : 1f;

		for ( int n = 0; n < count; n++ )
		{
			float s = 0f;
			for ( int k = 0; k < m; k++ )
			{
				// advance the rotator one sample, then shrink by the decay factor
				float nr = re[k] * cos[k] - im[k] * sin[k];
				float ni = re[k] * sin[k] + im[k] * cos[k];
				nr *= dec[k]; ni *= dec[k];
				re[k] = nr; im[k] = ni;
				s += im[k] * gain[k];
			}
			s *= norm;

			if ( n < noiseSamples )
			{
				float white = rng.Float() * 2f - 1f;
				lp += recipe.NoiseCutoff01 * (white - lp); // one-pole lowpass for woodiness
				s += lp * noiseEnv;
				noiseEnv *= noiseDecay;
			}

			// soft clip then to 16-bit
			s = (float)Math.Tanh( s * 1.2f ) * amplitude;
			int v = (int)(s * 32000f);
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
