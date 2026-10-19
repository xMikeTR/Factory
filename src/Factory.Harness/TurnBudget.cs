namespace Factory.Harness;

/// The tool calls one acting turn may make. One instance per turn. Exempt tools (check_quality)
/// are never counted, so the budget does not discourage the feedback the agent should seek.
/// At the cap the turn ends: the harness stops asking the model and verify runs on the worktree.
public sealed class TurnBudget(int maxToolCalls = 15, int warnAt = 12, IReadOnlySet<string>? exempt = null, string finishTool = "declare_done")
{
    /// A planner reads more than an implementer writes, so it gets twice the calls; the warning names the tool that ends its turn.
    public static TurnBudget ForPlanning(string submitTool) => new(30, 24, finishTool: submitTool);

    private readonly object _gate = new();
    private int _calls;

    public int Calls { get { lock (_gate) return _calls; } }
    public bool Exhausted { get; private set; }

    /// Counts a call about to run. False when the budget is already spent: the call must not run.
    public bool TryAdmit(string tool)
    {
        if (exempt?.Contains(tool) == true) return true;
        lock (_gate)
        {
            if (Exhausted) return false;
            _calls++;
            return true;
        }
    }

    /// After a call ran: a note for the model when the warning point is reached, and the turn
    /// is over once the cap is. Null for exempt tools and for every other call.
    public string? Completed(string tool)
    {
        if (exempt?.Contains(tool) == true) return null;
        lock (_gate)
        {
            if (_calls >= maxToolCalls) Exhausted = true;
            return _calls == warnAt
                ? $"\n\n[{maxToolCalls - _calls} calls left in this turn: write what you have and call {finishTool}]"
                : null;
        }
    }
}
