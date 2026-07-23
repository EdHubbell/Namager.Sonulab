using System.Collections.Generic;
using Namager.App.Services;

/// <summary>Records every call so VM tests can assert what was reported to the status channel.</summary>
public sealed class FakeStatusService : IStatusService
{
    public List<string> Begun { get; } = new();
    public List<string> Succeeded { get; } = new();
    public List<string> Failed { get; } = new();
    public List<string> IdleSummaries { get; } = new();

    public IOperationScope BeginOperation(string message, bool determinate = false)
    { Begun.Add(message); return new Scope(); }
    public void Success(string message) => Succeeded.Add(message);
    public void Failure(string message) => Failed.Add(message);
    public void SetIdleSummary(string summary) => IdleSummaries.Add(summary);
    public void Dismiss() { }

    private sealed class Scope : IOperationScope
    { public void Report(double progress) { } public void Report(string message) { } public void Dispose() { } }
}
