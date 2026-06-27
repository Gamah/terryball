using System;

namespace Terryball;

/// <summary>
/// A small mono Freeverb-style reverb — parallel damped comb filters into a couple of allpass
/// diffusers. We run each procedural clack through it so the dry impact gains an alley room tail:
/// that "resonance in the scene" is what makes an impulsive synth sound real rather than a bare,
/// hollow knock.
///
/// Why bake it here instead of leaning on s&box's reverb: the engine's reverb is raytraced off
/// scene geometry, so an open-topped lane reads as outdoors and stays dry. Baking a fixed room into
/// the sound is scene-independent and fully tunable. Like <see cref="PinSynth"/> it's pure DSP —
/// preallocated delay lines, no per-call allocation — so the live path resets it per hit and the
/// prebaked path runs it once.
///
/// The delay lengths are the classic Freeverb tunings (in samples at 44.1 kHz, which is our rate).
/// </summary>
public sealed class PinReverb
{
	public struct Settings
	{
		public float RoomSize;   // 0..~0.97 — comb feedback, i.e. tail length
		public float Damping;    // 0..1 — high-frequency absorption in the tail
		public float Wet;        // 0..1 — reverb level mixed under the dry signal
		public float TailSeconds;// how long to let the tail ring out past the dry input
	}

	private static readonly int[] CombLengths = { 1116, 1188, 1277, 1356 };
	private static readonly int[] AllpassLengths = { 556, 441 };

	private readonly float[][] _comb;
	private readonly int[] _combIdx;
	private readonly float[] _combStore; // one-pole damping state per comb
	private readonly float[][] _allpass;
	private readonly int[] _allpassIdx;

	public PinReverb()
	{
		_comb = new float[CombLengths.Length][];
		_combIdx = new int[CombLengths.Length];
		_combStore = new float[CombLengths.Length];
		for ( int i = 0; i < CombLengths.Length; i++ ) _comb[i] = new float[CombLengths[i]];

		_allpass = new float[AllpassLengths.Length][];
		_allpassIdx = new int[AllpassLengths.Length];
		for ( int i = 0; i < AllpassLengths.Length; i++ ) _allpass[i] = new float[AllpassLengths[i]];
	}

	private void Reset()
	{
		for ( int i = 0; i < _comb.Length; i++ ) { Array.Clear( _comb[i] ); _combIdx[i] = 0; _combStore[i] = 0f; }
		for ( int i = 0; i < _allpass.Length; i++ ) { Array.Clear( _allpass[i] ); _allpassIdx[i] = 0; }
	}

	/// <summary>
	/// Read a dry 16-bit clack and write the wet (dry + room tail) into <paramref name="dest"/>.
	/// Returns the wet length (dry length plus the ring-out tail), clamped to dest. Resets state, so
	/// successive hits don't bleed into each other.
	/// </summary>
	public int Process( ReadOnlySpan<short> dry, int dryLen, in Settings s, Span<short> dest )
	{
		Reset();

		float feedback = Math.Clamp( s.RoomSize, 0f, 0.97f );
		float damp = Math.Clamp( s.Damping, 0f, 1f );
		float wet = Math.Max( 0f, s.Wet );
		const float inputGain = 0.030f; // tames the summed comb gain so the tail can't blow up

		int tail = (int)(Math.Max( 0f, s.TailSeconds ) * PinSynth.SampleRate);
		int count = Math.Min( dest.Length, dryLen + tail );

		for ( int n = 0; n < count; n++ )
		{
			float x = n < dryLen ? dry[n] / 32768f : 0f;
			float input = x * inputGain;

			// parallel damped combs
			float combSum = 0f;
			for ( int i = 0; i < _comb.Length; i++ )
			{
				var buf = _comb[i];
				int idx = _combIdx[i];
				float outv = buf[idx];
				_combStore[i] = outv * (1f - damp) + _combStore[i] * damp; // lowpass in the feedback
				buf[idx] = input + _combStore[i] * feedback;
				_combIdx[i] = idx + 1 >= buf.Length ? 0 : idx + 1;
				combSum += outv;
			}

			// series allpass diffusion
			float y = combSum;
			for ( int i = 0; i < _allpass.Length; i++ )
			{
				var buf = _allpass[i];
				int idx = _allpassIdx[i];
				float bufout = buf[idx];
				float outv = -y + bufout;
				buf[idx] = y + bufout * 0.5f;
				_allpassIdx[i] = idx + 1 >= buf.Length ? 0 : idx + 1;
				y = outv;
			}

			float mixed = x + y * wet;
			int v = (int)(mixed * 32000f);
			dest[n] = (short)Math.Clamp( v, short.MinValue, short.MaxValue );
		}

		return count;
	}
}
