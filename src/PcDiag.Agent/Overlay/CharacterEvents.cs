using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// What happened that the characters react to once (<see cref="CharacterEvent"/>): an incident saved, recording turned
/// on or off, a game come to the front. The overlay service says it; every character's mind reads the latest (any thread).
/// </summary>
internal static class CharacterEvents
{
    private static long _count;
    private static volatile CharacterEvent? _latest;

    /// <summary>The latest event (null: none yet).</summary>
    public static CharacterEvent? Latest => _latest;

    /// <summary>Says that something happened.</summary>
    public static void Raise(string kind, string? text = null) => _latest = new CharacterEvent(Interlocked.Increment(ref _count), kind, text);
}
