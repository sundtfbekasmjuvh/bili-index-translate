using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Session;
using BiliIndexTranslate.Core.Translation;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class ReadingSessionTests
{
    private static SelectionSnapshot Snap(string text, long hwnd = 100, int pid = 7)
    {
        var r = PxRect.FromSize(100, 100, 200, 20);
        return new SelectionSnapshot(text, new AnchorRect(r, r, 1, 20), hwnd, pid, "app", EditableKind.NotEditable, SelectionOrigin.UiAutomation, 5);
    }

    private static TranslationOutcome.Success Ok(string t) => new(t, RequestRoute.Direct, false, 100);

    private static T Single<T>(IReadOnlyList<SessionEffect> effects) where T : SessionEffect
        => Assert.Single(effects.OfType<T>());

    private static long StartTimer(ReadingSession s, TriggerKind kind = TriggerKind.MouseUp)
        => Single<SessionEffect.StartStabilizationTimer>(s.Handle(new SessionEvent.TriggerReceived(kind))).TimerId;

    private static long Probe(ReadingSession s, long timerId)
        => Single<SessionEffect.RequestProbe>(s.Handle(new SessionEvent.StabilizationElapsed(timerId))).ProbeId;

    /// <summary>Drives trigger → timer → probe → Selected and returns the translation request id.</summary>
    private static (long RequestId, long SelectionId, IReadOnlyList<SessionEffect> Effects) SelectText(ReadingSession s, string text, long hwnd = 100)
    {
        var timer = StartTimer(s);
        var probe = Probe(s, timer);
        var fx = s.Handle(new SessionEvent.ProbeCompleted(probe, new ProbeOutcome.Selected(Snap(text, hwnd))));
        var start = Single<SessionEffect.StartTranslation>(fx);
        return (start.RequestId, start.SelectionId, fx);
    }

    [Fact]
    public void Scenario1_SelectionProducesLoadingThenTranslation()
    {
        var s = new ReadingSession();
        var fx = s.Handle(new SessionEvent.TriggerReceived(TriggerKind.MouseUp));
        var timer = Single<SessionEffect.StartStabilizationTimer>(fx);
        Assert.Equal(250, timer.DelayMs);
        Assert.Equal(SessionState.SelectionPending, s.State);

        var probeId = Probe(s, timer.TimerId);
        var (req, selId, selected) = SelectText(s, "hello world");
        _ = probeId;
        Assert.Contains(selected, e => e is SessionEffect.BeginTracking);
        Assert.Contains(selected, e => e is SessionEffect.ShowLoading);
        Assert.Equal(SessionState.Loading, s.State);

        var done = s.Handle(new SessionEvent.TranslationCompleted(req, Ok("你好，世界")));
        var show = Single<SessionEffect.ShowTranslation>(done);
        Assert.Equal(selId, show.SelectionId);
        Assert.Equal("你好，世界", show.Result.Translation);
        Assert.Equal(SessionState.ShowingTranslation, s.State);
    }

    [Fact]
    public void DelaysDependOnTrigger()
    {
        var s = new ReadingSession(new SessionOptions(BaseDelayMs: 250));
        Assert.Equal(280, Single<SessionEffect.StartStabilizationTimer>(s.Handle(new SessionEvent.TriggerReceived(TriggerKind.KeyUp))).DelayMs);
        Assert.Equal(320, Single<SessionEffect.StartStabilizationTimer>(s.Handle(new SessionEvent.TriggerReceived(TriggerKind.UiaEvent))).DelayMs);
        var hot = s.Handle(new SessionEvent.TriggerReceived(TriggerKind.Hotkey));
        Assert.Equal(ProbeMode.Explicit, Single<SessionEffect.RequestProbe>(hot).Mode);
        Assert.Contains(hot, e => e is SessionEffect.CancelStabilizationTimer);
    }

    [Fact]
    public void RepeatedTriggers_CoalesceIntoOneTimer()
    {
        var s = new ReadingSession();
        var t1 = StartTimer(s);
        var fx = s.Handle(new SessionEvent.TriggerReceived(TriggerKind.MouseUp));
        Assert.Equal(t1, Single<SessionEffect.CancelStabilizationTimer>(fx).TimerId);
        var t2 = Single<SessionEffect.StartStabilizationTimer>(fx).TimerId;

        Assert.Empty(s.Handle(new SessionEvent.StabilizationElapsed(t1))); // late timer: ignored
        Assert.Single(s.Handle(new SessionEvent.StabilizationElapsed(t2)).OfType<SessionEffect.RequestProbe>());
    }

    [Fact]
    public void Scenario2_ClearingSelection_HidesPopup()
    {
        var s = new ReadingSession();
        var (req, _, _) = SelectText(s, "hello");
        s.Handle(new SessionEvent.TranslationCompleted(req, Ok("你好")));

        var timer = StartTimer(s);
        var probe = Probe(s, timer);
        var fx = s.Handle(new SessionEvent.ProbeCompleted(probe, new ProbeOutcome.NoSelection()));
        Assert.Equal(DismissReason.SelectionCleared, Single<SessionEffect.HidePopup>(fx).Reason);
        Assert.Contains(fx, e => e is SessionEffect.EndTracking);
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public void Scenario3_SelectB_WhileALoading_CancelsA_AndIgnoresLateA()
    {
        var s = new ReadingSession();
        var (reqA, _, _) = SelectText(s, "alpha");
        var (reqB, selB, fxB) = SelectText(s, "beta");

        Assert.Equal(reqA, Single<SessionEffect.CancelTranslation>(fxB).RequestId);
        Assert.NotEqual(reqA, reqB);

        var late = s.Handle(new SessionEvent.TranslationCompleted(reqA, Ok("A")));
        Assert.DoesNotContain(late, e => e is SessionEffect.ShowTranslation);
        Assert.Equal(SessionState.Loading, s.State);

        var show = Single<SessionEffect.ShowTranslation>(s.Handle(new SessionEvent.TranslationCompleted(reqB, Ok("B"))));
        Assert.Equal(selB, show.SelectionId);
        Assert.Equal("B", show.Result.Translation);
    }

    [Fact]
    public void Scenario4_DismissWhilePending_NeverReopensOnLateResult()
    {
        var s = new ReadingSession();
        var (req, _, _) = SelectText(s, "pending");
        var fx = s.Handle(new SessionEvent.EscapePressed());
        Assert.Equal(req, Single<SessionEffect.CancelTranslation>(fx).RequestId);
        Assert.Equal(DismissReason.Escape, Single<SessionEffect.HidePopup>(fx).Reason);
        Assert.Equal(SessionState.Dismissed, s.State);

        var late = s.Handle(new SessionEvent.TranslationCompleted(req, Ok("late")));
        Assert.DoesNotContain(late, e => e is SessionEffect.ShowTranslation);
        Assert.Equal(SessionState.Dismissed, s.State);
    }

    [Fact]
    public void Scenario5_SameTextAfterDismissal_StaysQuiet_UntilSelectionChanges()
    {
        var s = new ReadingSession();
        var (req, _, _) = SelectText(s, "same");
        s.Handle(new SessionEvent.TranslationCompleted(req, Ok("同")));
        s.Handle(new SessionEvent.OutsideClick());
        Assert.Equal(SessionState.Dismissed, s.State);

        // Same selection re-probed (e.g. Alt+Tab back): no popup.
        var timer = StartTimer(s);
        var probe = Probe(s, timer);
        var fx = s.Handle(new SessionEvent.ProbeCompleted(probe, new ProbeOutcome.Selected(Snap("same"))));
        Assert.DoesNotContain(fx, e => e is SessionEffect.ShowLoading);
        Assert.Equal(SessionState.Dismissed, s.State);

        // Explicit hotkey overrides the suppression.
        var hot = s.Handle(new SessionEvent.TriggerReceived(TriggerKind.Hotkey));
        var hotProbe = Single<SessionEffect.RequestProbe>(hot).ProbeId;
        var hotFx = s.Handle(new SessionEvent.ProbeCompleted(hotProbe, new ProbeOutcome.Selected(Snap("same"))));
        Assert.Contains(hotFx, e => e is SessionEffect.ShowLoading);
        s.Handle(new SessionEvent.EscapePressed());

        // A different selection reopens normally.
        var (_, _, fx2) = SelectText(s, "different");
        Assert.Contains(fx2, e => e is SessionEffect.ShowLoading);
        Assert.Equal(SessionState.Loading, s.State);
    }

    [Fact]
    public void Suppression_ClearsWhenSelectionIsCleared()
    {
        var s = new ReadingSession();
        var (req, _, _) = SelectText(s, "same");
        s.Handle(new SessionEvent.TranslationCompleted(req, Ok("同")));
        s.Handle(new SessionEvent.OutsideClick());

        var t = StartTimer(s);
        var p = Probe(s, t);
        s.Handle(new SessionEvent.ProbeCompleted(p, new ProbeOutcome.NoSelection()));
        Assert.Equal(SessionState.Idle, s.State);

        var (_, _, fx) = SelectText(s, "same");
        Assert.Contains(fx, e => e is SessionEffect.ShowLoading);
    }

    [Fact]
    public void Scenario6_SameSelectionWhileVisible_KeepsPopup_UpdatesAnchor()
    {
        var s = new ReadingSession();
        var (req, selId, _) = SelectText(s, "keep");
        s.Handle(new SessionEvent.TranslationCompleted(req, Ok("保持")));

        var t = StartTimer(s);
        var p = Probe(s, t);
        var fx = s.Handle(new SessionEvent.ProbeCompleted(p, new ProbeOutcome.Selected(Snap("keep"))));
        Assert.DoesNotContain(fx, e => e is SessionEffect.HidePopup);
        Assert.DoesNotContain(fx, e => e is SessionEffect.StartTranslation);
        Assert.Equal(selId, Single<SessionEffect.UpdateAnchor>(fx).SelectionId);
        Assert.Equal(SessionState.ShowingTranslation, s.State);
    }

    [Fact]
    public void ForegroundChange_And_SourceLoss_Dismiss()
    {
        var s = new ReadingSession();
        var (req, _, _) = SelectText(s, "fg");
        s.Handle(new SessionEvent.TranslationCompleted(req, Ok("前")));

        Assert.Empty(s.Handle(new SessionEvent.ForegroundChanged(true)).OfType<SessionEffect.HidePopup>());
        var fx = s.Handle(new SessionEvent.ForegroundChanged(false));
        Assert.Equal(DismissReason.ForegroundChanged, Single<SessionEffect.HidePopup>(fx).Reason);
        Assert.Equal(SessionState.Dismissed, s.State);

        var (req2, _, _) = SelectText(s, "lost");
        s.Handle(new SessionEvent.TranslationCompleted(req2, Ok("丢")));
        var fx2 = s.Handle(new SessionEvent.SourceWindowLost());
        Assert.Equal(DismissReason.SourceWindowLost, Single<SessionEffect.HidePopup>(fx2).Reason);
    }

    [Fact]
    public void GeometryGrace_HidesOnlyAfterTimeout_AndIsCancelledByRects()
    {
        var s = new ReadingSession(new SessionOptions(GeometryGraceMs: 400));
        var (req, selId, _) = SelectText(s, "scroll");
        s.Handle(new SessionEvent.TranslationCompleted(req, Ok("滚")));

        var fx = s.Handle(new SessionEvent.GeometryRefreshed(selId, new GeometryUpdate.NoVisibleRects()));
        Assert.Equal(400, Single<SessionEffect.StartGeometryGrace>(fx).DelayMs);
        Assert.Empty(s.Handle(new SessionEvent.GeometryRefreshed(selId, new GeometryUpdate.NoVisibleRects()))); // no duplicate grace

        var r = PxRect.FromSize(0, 0, 10, 10);
        var back = s.Handle(new SessionEvent.GeometryRefreshed(selId, new GeometryUpdate.Rects(new AnchorRect(r, r, 1, 10))));
        Assert.Contains(back, e => e is SessionEffect.CancelGeometryGrace);
        Assert.Contains(back, e => e is SessionEffect.UpdateAnchor);

        Assert.Empty(s.Handle(new SessionEvent.GeometryGraceElapsed(selId))); // grace was cancelled
        Assert.Equal(SessionState.ShowingTranslation, s.State);

        s.Handle(new SessionEvent.GeometryRefreshed(selId, new GeometryUpdate.Unavailable("gone")));
        var hide = s.Handle(new SessionEvent.GeometryGraceElapsed(selId));
        Assert.Equal(DismissReason.GeometryUnavailable, Single<SessionEffect.HidePopup>(hide).Reason);
        Assert.Equal(SessionState.Dismissed, s.State);
    }

    [Fact]
    public void GeometryUpdates_ForOldSelection_AreIgnored()
    {
        var s = new ReadingSession();
        var (req, selId, _) = SelectText(s, "old");
        s.Handle(new SessionEvent.TranslationCompleted(req, Ok("旧")));
        Assert.Empty(s.Handle(new SessionEvent.GeometryRefreshed(selId + 99, new GeometryUpdate.Cleared())));
        Assert.Equal(SessionState.ShowingTranslation, s.State);
    }

    [Fact]
    public void GeometryCleared_Hides_Idle()
    {
        var s = new ReadingSession();
        var (req, selId, _) = SelectText(s, "x");
        s.Handle(new SessionEvent.TranslationCompleted(req, Ok("y")));
        var fx = s.Handle(new SessionEvent.GeometryRefreshed(selId, new GeometryUpdate.Cleared()));
        Assert.Contains(fx, e => e is SessionEffect.HidePopup { Reason: DismissReason.SelectionCleared });
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public void Scenario15_Error_ThenRetry()
    {
        var s = new ReadingSession();
        var (req, _, _) = SelectText(s, "err");
        var err = new TranslationError(TranslationErrorKind.Http, "Request rejected by the service (HTTP 412).", RequestRoute.Direct, 412);
        var fx = s.Handle(new SessionEvent.TranslationCompleted(req, new TranslationOutcome.Failure(err)));
        Assert.Equal(412, Single<SessionEffect.ShowError>(fx).Error.StatusCode);
        Assert.Equal(SessionState.ShowingError, s.State);

        var retry = s.Handle(new SessionEvent.UserRetry());
        var start = Single<SessionEffect.StartTranslation>(retry);
        Assert.NotEqual(req, start.RequestId);
        Assert.Equal(SessionState.Loading, s.State);
        s.Handle(new SessionEvent.TranslationCompleted(start.RequestId, Ok("好")));
        Assert.Equal(SessionState.ShowingTranslation, s.State);
    }

    [Fact]
    public void CancelledFailure_DoesNotShowError()
    {
        var s = new ReadingSession();
        var (req, _, _) = SelectText(s, "c");
        var fx = s.Handle(new SessionEvent.TranslationCompleted(req, new TranslationOutcome.Failure(new TranslationError(TranslationErrorKind.Cancelled, "Cancelled.", RequestRoute.Direct))));
        Assert.DoesNotContain(fx, e => e is SessionEffect.ShowError);
    }

    [Fact]
    public void StaleProbe_IsIgnored()
    {
        var s = new ReadingSession();
        var t1 = StartTimer(s);
        var p1 = Probe(s, t1);
        var t2 = StartTimer(s);
        var p2 = Probe(s, t2);
        var stale = s.Handle(new SessionEvent.ProbeCompleted(p1, new ProbeOutcome.Selected(Snap("stale"))));
        Assert.DoesNotContain(stale, e => e is SessionEffect.ShowLoading);
        var fresh = s.Handle(new SessionEvent.ProbeCompleted(p2, new ProbeOutcome.Selected(Snap("fresh"))));
        Assert.Contains(fresh, e => e is SessionEffect.ShowLoading);
    }

    [Fact]
    public void AutoTranslateOff_IgnoresAutomaticTriggers_ButNotHotkey()
    {
        var s = new ReadingSession(new SessionOptions(AutoTranslate: false));
        Assert.Empty(s.Handle(new SessionEvent.TriggerReceived(TriggerKind.MouseUp)));
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Single(s.Handle(new SessionEvent.TriggerReceived(TriggerKind.Hotkey)).OfType<SessionEffect.RequestProbe>());
    }

    [Fact]
    public void Pin_DetachesPopup_AndSuppressesSameSelection()
    {
        var s = new ReadingSession();
        var (req, selId, _) = SelectText(s, "pin");
        Assert.Empty(s.Handle(new SessionEvent.UserPinned())); // not allowed while loading
        s.Handle(new SessionEvent.TranslationCompleted(req, Ok("钉")));
        var fx = s.Handle(new SessionEvent.UserPinned());
        Assert.Equal(selId, Single<SessionEffect.DetachPopup>(fx).SelectionId);
        Assert.Contains(fx, e => e is SessionEffect.EndTracking);
        Assert.DoesNotContain(fx, e => e is SessionEffect.HidePopup);
        Assert.Equal(SessionState.Dismissed, s.State);
    }

    [Fact]
    public void Shutdown_CancelsEverything_AndIgnoresLaterEvents()
    {
        var s = new ReadingSession();
        var (req, _, _) = SelectText(s, "bye");
        var fx = s.Handle(new SessionEvent.Shutdown());
        Assert.Equal(req, Single<SessionEffect.CancelTranslation>(fx).RequestId);
        Assert.Equal(DismissReason.Shutdown, Single<SessionEffect.HidePopup>(fx).Reason);
        Assert.Empty(s.Handle(new SessionEvent.TriggerReceived(TriggerKind.MouseUp)));
        Assert.Empty(s.Handle(new SessionEvent.TranslationCompleted(req, Ok("x"))));
    }

    [Fact]
    public void SettingsChanged_HidesWithoutSuppression()
    {
        var s = new ReadingSession();
        var (req, _, _) = SelectText(s, "cfg");
        s.Handle(new SessionEvent.TranslationCompleted(req, Ok("配")));
        var fx = s.Handle(new SessionEvent.SettingsChanged());
        Assert.Equal(DismissReason.SettingsChanged, Single<SessionEffect.HidePopup>(fx).Reason);
        Assert.Equal(SessionState.Idle, s.State);
        var (_, _, again) = SelectText(s, "cfg");
        Assert.Contains(again, e => e is SessionEffect.ShowLoading);
    }

    [Fact]
    public void SkippedWhileVisible_Hides_UnsupportedWhileVisible_Keeps()
    {
        var s = new ReadingSession();
        var (req, _, _) = SelectText(s, "v");
        s.Handle(new SessionEvent.TranslationCompleted(req, Ok("可")));

        var t = StartTimer(s);
        var p = Probe(s, t);
        Assert.DoesNotContain(s.Handle(new SessionEvent.ProbeCompleted(p, new ProbeOutcome.Unsupported("button"))), e => e is SessionEffect.HidePopup);
        Assert.Equal(SessionState.ShowingTranslation, s.State);

        t = StartTimer(s);
        p = Probe(s, t);
        Assert.Contains(s.Handle(new SessionEvent.ProbeCompleted(p, new ProbeOutcome.Skipped("too long"))), e => e is SessionEffect.HidePopup);
        Assert.Equal(SessionState.Idle, s.State);
    }
}
