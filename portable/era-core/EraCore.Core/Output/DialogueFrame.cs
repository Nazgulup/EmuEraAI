using System.Collections.Generic;
namespace MinorShift.Emuera.Output;

/// <summary>Read-only strings captured from one original dialogue execution. No engine objects.</summary>
public sealed record DialogueFrame(string EventId, string Character, string User, string Facts, string Original, string Hook)
{
    /// <summary>Presentation-only positions in the current display snapshot.</summary>
    public int[] LineIndices { get; init; } = [];
    /// <summary>Optional insertion before a real menu row when no original dialogue exists.</summary>
    public int? InsertBeforeLine { get; init; }
    public Dictionary<string,string> Addresses { get; init; } = [];
    public string[] StoryContext { get; init; } = [];
    public long StoryTurn { get; init; }
}

/// <summary>Capture diagnostics only; no script contents, variables or engine references.</summary>
public sealed record DialogueCaptureInfo(string Reason, int PrintedLines, int OutsideScope, int InvalidTarget, int ButtonLines, int ObserverErrors, string ErrorType);
