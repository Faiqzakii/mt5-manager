using Mt5Manager.Application.Abstractions;
using Mt5Manager.Application.Services;
using Mt5Manager.Application.Telegram;
using Mt5Manager.Domain.Models;
using Xunit;

namespace Mt5Manager.Application.Tests;

public sealed class TerminalUpdateServiceTests
{
    static TerminalRegistration Terminal(string name, bool verified = true) => new(Guid.NewGuid(), name, "terminal64.exe", "data", ".", ["/portable"], DiscoverySource.Manual, verified);
    [Fact]
    public async Task OrdersTargetsWithoutOverlapAndPreservesArguments()
    {
        var a = Terminal("a"); var b = Terminal("b"); var p = new Process(); var d = new Delay();
        var result = await new TerminalUpdateService(p, new Evidence(), new Audit(), d).UpdateAsync(new([a,b,a]));
        Assert.Equal(["stop:a", "start:a", "stop:b", "start:b"], p.Calls);
        Assert.Equal(2, result.Count); Assert.All(result, r => Assert.True(r.Success));
        Assert.Equal([90d,30d,90d], d.Seconds);
        Assert.All(p.Starts, t => Assert.Equal("/portable", Assert.Single(t.Arguments)));
    }
    [Fact]
    public async Task SkipsStoppedAndUnverifiedAndNeverStartsOnTimeout()
    {
        var p = new Process { Outcome = StopOutcome.TimedOut }; var stopped = Terminal("stopped"); p.States[stopped.Id] = TerminalState.Stopped;
        var result = await new TerminalUpdateService(p,new Evidence(),new Audit(),new Delay()).UpdateAsync(new([Terminal("unverified",false),stopped,Terminal("timeout")]));
        Assert.True(result[0].Skipped); Assert.True(result[1].Skipped); Assert.False(result[2].Success); Assert.Empty(p.Starts);
    }
    [Fact]
    public async Task MissingEvidenceRemainsUnknownWithoutRepeats()
    {
        var p=new Process();var result=await new TerminalUpdateService(p,new Evidence(),new Audit(),new Delay()).UpdateAsync(new([Terminal("a")]));
        Assert.Contains("Unknown",result[0].Message);Assert.Single(p.Starts);Assert.False(result[0].After!.JournalAvailable);
    }
    [Theory]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public async Task ActivityOrVersionChangeRepeatsButNeverBeyondThree(bool activity,bool change)
    {
        var p=new Process();var result=await new TerminalUpdateService(p,new Evidence {Activity=activity,Change=change},new Audit(),new Delay()).UpdateAsync(new([Terminal("a")]));
        Assert.Equal(3,p.Starts.Count);Assert.Equal(3,result[0].Passes);Assert.Contains("Maximum passes",result[0].Message);
    }
    [Fact]
    public async Task CancellationAfterStopRecoversBeforeAbandoningTargets()
    {
        using var cts=new CancellationTokenSource();var p=new Process { OnStop=()=>cts.Cancel() };
        var result=await new TerminalUpdateService(p,new Evidence(),new Audit(),new Delay()).UpdateAsync(new([Terminal("a"),Terminal("b")]),cancellationToken:cts.Token);
        Assert.Equal(["stop:a","start:a"],p.Calls);Assert.False(Assert.Single(result).Success);
    }
    sealed class Process : ITerminalProcessController
    {
        public readonly Dictionary<Guid,TerminalState> States=[];public readonly List<string> Calls=[];public readonly List<TerminalRegistration> Starts=[];
        public StopOutcome Outcome=StopOutcome.ExitedGracefully;public Action? OnStop;
        public Task<TerminalRuntimeState> GetStateAsync(TerminalRegistration t,CancellationToken c)=>Task.FromResult(new TerminalRuntimeState(States.GetValueOrDefault(t.Id,TerminalState.Running),1,null));
        public Task<int> StartAsync(TerminalRegistration t,CancellationToken c){Assert.False(c.IsCancellationRequested);Calls.Add("start:"+t.DisplayName);Starts.Add(t);States[t.Id]=TerminalState.Running;return Task.FromResult(1);}
        public Task<StopResult> StopAsync(TerminalRegistration t,TimeSpan timeout,bool force,CancellationToken c){Assert.False(force);Assert.Equal(TimeSpan.FromSeconds(90),timeout);Calls.Add("stop:"+t.DisplayName);if(Outcome==StopOutcome.ExitedGracefully)States[t.Id]=TerminalState.Stopped;OnStop?.Invoke();return Task.FromResult(new StopResult(Outcome,null));}
    }
    sealed class Evidence : ITerminalUpdateEvidenceReader
    {
        int reads;public bool Activity;public bool Change;
        public Task<UpdateEvidence> ReadAsync(TerminalRegistration t,DateTimeOffset since,CancellationToken c=default)=>Task.FromResult(new UpdateEvidence(Change?(++reads).ToString():null,Activity,Activity,DateTimeOffset.UtcNow));
    }
    sealed class Delay : IDelay {public readonly List<double> Seconds=[];public Task DelayAsync(TimeSpan delay,CancellationToken c){c.ThrowIfCancellationRequested();Seconds.Add(delay.TotalSeconds);return Task.CompletedTask;}}
    sealed class Audit : IAuditLogger {public Task AppendAsync(AuditRecord r,CancellationToken c=default)=>Task.CompletedTask;}
}
