using System;
using Godot;

internal sealed class GardenAudio : IDisposable
{
    private const int SampleRate = 24000;
    private const int Chunk = 256;
    private static readonly float[] Frequencies = [73.42f, 110, 164.81f, 246.94f, 369.99f];
    private static readonly int[] Notes = [0, 2, 5, 7, 9, 12, 14, 17, 19];
    private readonly record struct Voice(float Frequency, float Glide, float Duration, float Volume, float Attack,
        float Pan, double Onset, bool Triangle);
    private readonly AudioStreamPlayer _player;
    private readonly AudioStreamGeneratorPlayback _stream;
    private readonly Random _random = new(20261110);
    private readonly Voice[] _voices = new Voice[16];
    private readonly Vector2[] _samples = new Vector2[Chunk];
    private readonly Vector2[] _echo = new Vector2[8192];
    private int _voiceIndex, _echoIndex;
    private double _time;
    private double _bubbleAt = 1.5, _chimeAt = 5, _whaleAt = 12;
    private Vector2 _brown, _low, _high;
    private float _volume, _rush, _wash;

    public bool Muted { get; set; }

    public GardenAudio(Node owner)
    {
        using var generator = new AudioStreamGenerator { MixRate = SampleRate, BufferLength = 0.15f };
        _player = new AudioStreamPlayer { Stream = generator };
        owner.AddChild(_player);
        _player.Play();
        _stream = (AudioStreamGeneratorPlayback)_player.GetStreamPlayback();
    }

    public void Dispose()
    {
        _player.Stop();
        _stream.Dispose();
    }

    public void Fill(float speed, float depth)
    {
        int available = _stream.GetFramesAvailable();
        float surface = Mathf.Clamp(1 + depth / 30, 0, 1);
        float washTarget = 0.25f + 0.55f * surface * surface;
        float rushTarget = 0.5f * Mathf.Pow(Mathf.Min(speed / 26, 1), 1.2f);
        float volumeTarget = Muted ? 0 : 0.78f;
        Schedule();
        while (available >= Chunk)
        {
            for (int i = 0; i < Chunk; i++)
            {
                _time += 1d / SampleRate;
                _volume += (volumeTarget - _volume) * 0.00052f;
                _rush += (rushTarget - _rush) * 0.00014f;
                _wash += (washTarget - _wash) * 0.00008f;
                float rumble = 0.34f + 0.11f * Wave(0.07) + 0.08f * Wave(0.113);
                float wash = _wash * (1 + 0.3f * Wave(0.09) + 0.25f * Wave(0.151));
                _brown = (_brown + new Vector2(Range(-1, 1), Range(-1, 1)) * 0.02f) / 1.02f;
                _low += (_brown - _low) * 0.0385f;
                _high += (_brown - _high) * 0.105f;
                Vector2 ambient = 3.5f * (_low * rumble + (_high - _low) * (wash + _rush));
                float drone = 0;
                for (int j = 0; j < Frequencies.Length; j++)
                    drone += (0.022f - j * 0.003f) * (Wave(Frequencies[j]) + Wave(Frequencies[j] + 0.05 + 0.012 * j));
                Vector2 notes = PlayVoices();
                Vector2 delayed = _echo[_echoIndex];
                _echo[_echoIndex] = Vector2.One * (drone * 0.25f) + notes * 0.6f
                    + new Vector2(delayed.Y, delayed.X) * 0.48f;
                _echoIndex = (_echoIndex + 1) % _echo.Length;
                Vector2 sample = (ambient + Vector2.One * drone + notes + delayed * 0.45f) * _volume;
                _samples[i] = sample.Clamp(-Vector2.One, Vector2.One);
            }
            _stream.PushBuffer(_samples);
            available -= Chunk;
        }
    }

    private void Schedule()
    {
        if (_time >= _bubbleAt)
        {
            int count = 1 + (int)(Range(0, 1) * Range(0, 1) * 6);
            double time = _time;
            for (int i = 0; i < count; i++)
            {
                float frequency = Range(380, 1300);
                AddVoice(frequency, frequency * Range(1.4f, 2.2f), 0.05f + 40 / frequency, Range(0.03f, 0.08f), 0.006f, time);
                time += Range(0.07f, 0.2f);
            }
            _bubbleAt = time + Range(0.5f, 5);
        }
        if (_time >= _chimeAt)
        {
            float frequency = 440 * Mathf.Pow(2, Notes[_random.Next(Notes.Length)] / 12f);
            AddVoice(frequency, frequency, 5, Range(0.02f, 0.04f), 0.03f, _time);
            _chimeAt = _time + Range(2.5f, 9);
        }
        if (_time >= _whaleAt)
        {
            double time = _time;
            int count = _random.Next(2, 4);
            for (int i = 0; i < count; i++)
            {
                float frequency = Range(170, 380);
                float duration = Range(1.6f, 3.2f);
                AddVoice(frequency, frequency * Range(0.6f, 1.7f), duration, 0.08f, duration * 0.4f, time, true);
                time += duration * 0.8f;
            }
            _whaleAt = time + Range(20, 50);
        }
    }

    private void AddVoice(float start, float end, float duration, float volume, float attack, double onset,
        bool triangle = false)
    {
        _voices[_voiceIndex] = new(start, Mathf.Log(end / start) / duration, duration, volume, attack,
            Range(-0.7f, 0.7f), onset, triangle);
        _voiceIndex = (_voiceIndex + 1) % _voices.Length;
    }

    private Vector2 PlayVoices()
    {
        Vector2 mix = Vector2.Zero;
        foreach (Voice voice in _voices)
        {
            float age = (float)(_time - voice.Onset);
            if (age < 0 || age >= voice.Duration)
                continue;
            float level = voice.Volume * (age < voice.Attack ? age / voice.Attack :
                Mathf.Exp(-5 * (age - voice.Attack) / (voice.Duration - voice.Attack)));
            float cycles = Mathf.Abs(voice.Glide) < 0.001f ? voice.Frequency * age
                : voice.Frequency * (Mathf.Exp(voice.Glide * age) - 1) / voice.Glide;
            float wave = voice.Triangle ? 1 - 4 * Mathf.Abs((cycles - Mathf.Floor(cycles)) - 0.5f)
                : Mathf.Sin(Mathf.Tau * cycles);
            mix += new Vector2(1 - voice.Pan, 1 + voice.Pan) * (level * wave * 0.7071f);
        }
        return mix;
    }

    private float Range(float min, float max) => Mathf.Lerp(min, max, _random.NextSingle());
    private float Wave(double frequency) => (float)Math.Sin(Math.Tau * frequency * _time);
}
