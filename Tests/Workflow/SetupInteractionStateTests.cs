using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Workflow;
using AiDesktopSetup.Core.Recovery;
using AiDesktopSetup.Tests.Protocol;
namespace AiDesktopSetup.Tests.Workflow;

public sealed class SetupInteractionStateTests
{
    [Fact] public void SelectingAnotherRecoveryRetargetsCancelAndDropsFreshAuthorization()
    {
        var state = Authenticated(); var target = new ResumeId(Guid.NewGuid());
        Assert.True(state.SelectRecovery(target));
        Assert.Equal(target, state.Active);
        Assert.Null(state.Setup); Assert.Null(state.Preview); Assert.Null(state.Help);
        Assert.False(state.CanAuthenticate); Assert.False(state.CanInstall); Assert.False(state.CanLaunch);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void UnavailableRecoverySelectionDropsPriorAuthorizationBeforeLoading(bool throws)
    {
        var state = Authenticated(); var target = new ResumeId(Guid.NewGuid());
        AuthenticatedSetup? observedSetup = null; ResumeId? observedTarget = null;
        ResumeRecord? Load(ResumeId id)
        {
            observedSetup = state.Setup; observedTarget = state.Active;
            if (throws) throw new IOException("Storage unavailable");
            return null;
        }
        if (throws) Assert.Throws<IOException>(() => state.LoadRecovery(target, Load));
        else Assert.Null(state.LoadRecovery(target, Load));
        Assert.Null(observedSetup); Assert.Equal(target, observedTarget);
        Assert.Null(state.Active); Assert.Null(state.Setup); Assert.Null(state.Preview); Assert.Null(state.Help);
        Assert.False(state.CanInstall); Assert.False(state.CanAuthenticate); Assert.False(state.CanLaunch);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void UnavailableSameRecoveryRefreshAlsoDropsAuthorization(bool throws)
    {
        var state = Authenticated(); var id = state.Active!.Value;
        if (throws) Assert.Throws<IOException>(() => state.LoadRecovery(id, _ => throw new IOException("Storage unavailable")));
        else Assert.Null(state.LoadRecovery(id, _ => null));
        Assert.Null(state.Active); Assert.Null(state.Setup); Assert.Null(state.Preview); Assert.Null(state.Help);
        Assert.False(state.CanInstall); Assert.False(state.CanAuthenticate); Assert.False(state.CanLaunch);
    }
    [Fact] public void ValidSameRecoveryLoadPreservesAuthenticatedTarget()
    {
        var state = Authenticated(); var setup = state.Setup; var preview = state.Preview; var id = state.Active!.Value;
        var clock = new FixedClock();
        var record = new ResumeRecord(new ClaimRecord(id, Guid.NewGuid(), new ResumeSecret(new byte[32])), preview!.Code,
            clock.UtcNow, clock.UtcNow.AddHours(2), LocalStage.Created);
        Assert.Same(record, state.LoadRecovery(id, _ => record));
        Assert.Same(setup, state.Setup); Assert.Same(preview, state.Preview);
        Assert.True(state.CanInstall); Assert.True(state.CanAuthenticate); Assert.NotNull(state.Help);
    }
    [Fact] public void RefreshingSameRecoveryPreservesAuthenticatedTarget()
    {
        var state = Authenticated(); var setup = state.Setup; var preview = state.Preview;
        Assert.False(state.SelectRecovery(state.Active!.Value));
        Assert.Same(setup, state.Setup); Assert.Same(preview, state.Preview);
        Assert.True(state.CanAuthenticate); Assert.True(state.CanInstall); Assert.NotNull(state.Help);
    }
    [Fact] public void EditingCodeAfterRecoverySelectionDetachesTheSavedTarget()
    {
        var state = Authenticated(); state.SelectRecovery(new ResumeId(Guid.NewGuid()));
        state.CodeChanged();
        Assert.Null(state.Active); Assert.Null(state.Setup); Assert.Null(state.Preview); Assert.Null(state.Help);
        Assert.False(state.CanInstall); Assert.False(state.CanAuthenticate); Assert.False(state.CanLaunch);
    }
    [Fact] public void CompletionClearsAuthorizationButRetainsLaunchReadiness()
    {
        var state = Authenticated();
        state.CodeChanged(); // PasswordBox.Clear raises this before the outcome is presented.
        state.Complete(new(SetupLocalState.Completed, ReceiptState.Acknowledged, false, false, true));
        Assert.Null(state.Active); Assert.Null(state.Preview); Assert.Null(state.Setup); Assert.Null(state.Help);
        Assert.True(state.CanLaunch); Assert.False(state.CanInstall); Assert.False(state.CanAuthenticate);
    }
    [Fact] public void RefreshAfterCompletionDoesNotSelectAnUnrelatedRecovery()
    {
        var state = Authenticated(); var other = new ResumeId(Guid.NewGuid());
        state.Complete(new(SetupLocalState.Completed, ReceiptState.Acknowledged, false, false, true));
        Assert.Null(state.RecoveryForRefresh(new[] { other }));
        Assert.True(state.CanLaunch);
    }
    [Fact] public void InitialRecoveryRefreshCanSelectTheFirstSavedTarget()
    {
        var state = new SetupInteractionState(); var first = new ResumeId(Guid.NewGuid());
        Assert.Equal(first, state.RecoveryForRefresh(new[] { first }, selectFirst: true));
    }
    [Fact] public void SameTargetRefreshRetainsItsRecoverySelection()
    {
        var state = Authenticated(); var other = new ResumeId(Guid.NewGuid());
        Assert.Equal(state.Active, state.RecoveryForRefresh(new[] { other, state.Active!.Value }));
    }
    [Fact] public void PendingReceiptKeepsItsTargetForRetry()
    {
        var state = Authenticated(); var target = state.Active;
        state.Complete(new(SetupLocalState.Completed, ReceiptState.Pending, false, false, false));
        Assert.Equal(target, state.Active); Assert.False(state.CanLaunch);
    }
    [Fact] public void LocalSetupGuidanceIsPresentedWithoutUnexpectedExceptionDetails()
    {
        const string guidance = "Recovery expired or was ended. Paste a new setup code.";
        Assert.Equal(guidance, SetupFailurePresentation.Message(new SetupException(guidance)));
        Assert.Equal(SetupFailurePresentation.Generic, SetupFailurePresentation.Message(new IOException("synthetic-private-key", new Exception("remote detail"))));
    }
    [Theory]
    [InlineData("")]
    [InlineData("unsafe\u001bmessage")]
    [InlineData("unsafe\rmessage")]
    public void EmptyOrControlCharacterGuidanceUsesGenericMessage(string message)
        => Assert.Equal(SetupFailurePresentation.Generic, SetupFailurePresentation.Message(new SetupException(message)));
    [Fact] public void OversizedGuidanceUsesGenericMessage()
        => Assert.Equal(SetupFailurePresentation.Generic, SetupFailurePresentation.Message(new SetupException(new string('a', 4097))));

    private static SetupInteractionState Authenticated()
    {
        var code = ProtocolFixtures.Code(); var id = new ResumeId(Guid.NewGuid());
        var session = ProtocolFixtures.Validator().Validate(code, ProtocolFixtures.Session());
        return new() { Active = id, Preview = new(code, new Uri(code.SetupBaseUrl).GetLeftPart(UriPartial.Authority), code.ApiBaseUrl, code.ServiceName),
            Setup = new(id, new(session, new ResumeSecret(new byte[32])), session), Help = "https://service.example/help", CanLaunch = true };
    }
}
