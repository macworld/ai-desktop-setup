using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;
namespace AiDesktopSetup.Core.Workflow;

/// <summary>The target and fresh authorization currently presented by the desktop window.</summary>
public sealed class SetupInteractionState
{
    public SetupPreview? Preview { get; set; }
    public AuthenticatedSetup? Setup { get; set; }
    public ResumeId? Active { get; set; }
    public string? Help { get; set; }
    public bool CanLaunch { get; set; }
    public bool CanAuthenticate => Preview != null;
    public bool CanInstall => Setup != null;
    public void CodeChanged()
    {
        Preview = null; Setup = null; Active = null; Help = null; CanLaunch = false;
    }
    public ResumeId? RecoveryForRefresh(IReadOnlyList<ResumeId> available, bool selectFirst = false)
    {
        if (Active.HasValue && available.Contains(Active.Value)) return Active;
        return selectFirst && !CanLaunch && Preview == null && available.Count != 0 ? available[0] : (ResumeId?)null;
    }
    public ResumeRecord? LoadRecovery(ResumeId id, Func<ResumeId, ResumeRecord?> load)
    {
        // Retarget before accessing storage; an unavailable B must never leave A authorized.
        SelectRecovery(id);
        try
        {
            var record = load(id);
            if (record == null) CodeChanged();
            return record;
        }
        catch { CodeChanged(); throw; }
    }
    public bool SelectRecovery(ResumeId id)
    {
        if (Active == id) return false;
        CodeChanged(); Active = id;
        return true;
    }
    public void Complete(SetupOutcome outcome)
    {
        if (outcome.ReceiptState == ReceiptState.Acknowledged) { Active = null; Setup = null; Preview = null; Help = null; }
        CanLaunch = outcome.CanLaunch;
    }
}
