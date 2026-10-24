namespace PcDiag.Core.Overlay;

/// <summary>A particle drawn with a character: where (from its feet, in pixels), how big, its color (ARGB) and opacity, and its turn (radians).</summary>
public readonly record struct CharacterParticle(float X, float Y, float R, uint Color, float Alpha, float Angle);

/// <summary>
/// Particles drawn in code with a character (as the editor's characters/particles.ts, the same numbers): confetti
/// bursting from its head on a happy moment (relief, a game started, the user back) and dust puffing behind its feet
/// while it runs; and its breathing (a slow squash and stretch while it stands, sits or sleeps). Each is a function of
/// time alone, so the recorder and the editor draw the same.
/// </summary>
public static class CharacterParticles
{
    /// <summary>How long a confetti burst lasts, in seconds.</summary>
    public const double ConfettiSeconds = 1.6;

    /// <summary>How often a puff of dust leaves its feet while it runs, in seconds.</summary>
    public const double DustEvery = 0.17;

    /// <summary>How long a puff of dust lasts, in seconds.</summary>
    public const double DustSeconds = 0.5;

    private static readonly uint[] Confetti = [0xFFF43F5E, 0xFFF59E0B, 0xFF10B981, 0xFF3B82F6, 0xFFA855F7, 0xFFFDE047];

    /// <summary>A number from 0 to 1 for a particle and a draw (the same in the editor).</summary>
    public static double Random(int particle, int draw)
    {
        var x = Math.Sin((particle * 12.9898) + (draw * 78.233)) * 43758.5453;
        return x - Math.Floor(x);
    }

    /// <summary>A confetti burst <paramref name="age"/> seconds in, from the head of a character of <paramref name="size"/> pixels.</summary>
    public static IReadOnlyList<CharacterParticle> ConfettiAt(double age, float size)
    {
        if (age < 0 || age >= ConfettiSeconds)
        {
            return [];
        }

        var pieces = new List<CharacterParticle>(24);
        var fade = (float)(1 - Math.Pow(age / ConfettiSeconds, 3));
        for (var k = 0; k < 24; k++)
        {
            var angle = (-Math.PI / 2) + ((Random(k, 1) - 0.5) * Math.PI * 0.9);
            var speed = size * (1.2 + (Random(k, 2) * 1.4));
            var gravity = size * 2.2;
            var x = Math.Cos(angle) * speed * age;
            var y = (-size * 0.9) + (Math.Sin(angle) * speed * age) + (0.5 * gravity * age * age);
            var spin = age * (4 + (Random(k, 3) * 8)) * (Random(k, 4) > 0.5 ? 1 : -1);
            pieces.Add(new CharacterParticle((float)x, (float)y, size * 0.035f, Confetti[k % Confetti.Length], fade, (float)spin));
        }

        return pieces;
    }

    /// <summary>The puffs of dust behind the feet of a character running (facing <paramref name="facing"/>) at <paramref name="now"/> seconds.</summary>
    public static IReadOnlyList<CharacterParticle> DustAt(double now, int facing, float size)
    {
        var puffs = new List<CharacterParticle>(3);
        var last = Math.Floor(now / DustEvery) * DustEvery;
        for (var j = 0; j < 3; j++)
        {
            var age = now - (last - (j * DustEvery));
            if (age < 0 || age >= DustSeconds)
            {
                continue;
            }

            puffs.Add(new CharacterParticle(
                (float)(-facing * ((size * 0.12) + (age * size * 0.5))),
                (float)(-age * size * 0.15),
                (float)(size * (0.04 + (age * 0.12))),
                0xFFC8BEAA,
                (float)(0.45 * (1 - (age / DustSeconds))),
                0));
        }

        return puffs;
    }

    /// <summary>How tall it is drawn against its size at a moment: breathing (1.8 % up and down every 2.6 s) while it stands, sits or sleeps.</summary>
    public static double BreathAt(string action, double now) =>
        action is "idle" or "sit" or "sleep" ? 1 + (0.018 * Math.Sin(2 * Math.PI * now / 2.6)) : 1;
}
