namespace FlowOrchestrator.Core.Execution;

public interface IStepInstance<TInput> : IExecutionContext
{
    DateTimeOffset ScheduledTime { get; set; }
    string Type { get; set; }
    string Key { get; }
    TInput Inputs { get; set; }
    int Index { get; set; }
    bool ScopeMoveNext { get; set; }
}

public interface IStepInstance : IStepInstance<IDictionary<string, object?>> { }
