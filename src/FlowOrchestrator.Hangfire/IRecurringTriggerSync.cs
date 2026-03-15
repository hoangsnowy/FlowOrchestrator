namespace FlowOrchestrator.Hangfire;

public interface IRecurringTriggerSync
{
    void SyncTriggers(Guid flowId, bool isEnabled);
}
