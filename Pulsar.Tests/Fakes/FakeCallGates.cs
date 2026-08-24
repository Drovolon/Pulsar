using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using Pulsar.Api;
using Pulsar.Ipc;

namespace Pulsar.Tests.Fakes;

// Minimal ICallGateProvider fakes: capture what production registers, record what it
// sends (Sent, one object?[] per SendMessage), and let tests drive the registered
// handlers exactly like a peer plugin invoking the gate would.

public sealed class FakeCallGate<TRet> : ICallGateProvider<TRet>
{
    public Action? Action { get; private set; }
    public Func<TRet>? Func { get; private set; }
    public List<object?[]> Sent { get; } = [];
    public Action<object?[]>? OnSent { get; set; }

    public int SubscriptionCount => 0;
    public void RegisterAction(Action action) => Action = action;
    public void RegisterFunc(Func<TRet> func) => Func = func;
    public void UnregisterAction() => Action = null;
    public void UnregisterFunc() => Func = null;
    public IpcContext? GetContext() => null;

    public void SendMessage()
    {
        object?[] args = [];
        Sent.Add(args);
        OnSent?.Invoke(args);
    }
}

public sealed class FakeCallGate<T1, TRet> : ICallGateProvider<T1, TRet>
{
    public Action<T1>? Action { get; private set; }
    public Func<T1, TRet>? Func { get; private set; }
    public List<object?[]> Sent { get; } = [];
    public Action<object?[]>? OnSent { get; set; }

    public int SubscriptionCount => 0;
    public void RegisterAction(Action<T1> action) => Action = action;
    public void RegisterFunc(Func<T1, TRet> func) => Func = func;
    public void UnregisterAction() => Action = null;
    public void UnregisterFunc() => Func = null;
    public IpcContext? GetContext() => null;

    public void SendMessage(T1 a1)
    {
        object?[] args = [a1];
        Sent.Add(args);
        OnSent?.Invoke(args);
    }
}

public sealed class FakeCallGate<T1, T2, T3, T4, TRet> : ICallGateProvider<T1, T2, T3, T4, TRet>
{
    public Action<T1, T2, T3, T4>? Action { get; private set; }
    public Func<T1, T2, T3, T4, TRet>? Func { get; private set; }
    public List<object?[]> Sent { get; } = [];
    public Action<object?[]>? OnSent { get; set; }

    public int SubscriptionCount => 0;
    public void RegisterAction(Action<T1, T2, T3, T4> action) => Action = action;
    public void RegisterFunc(Func<T1, T2, T3, T4, TRet> func) => Func = func;
    public void UnregisterAction() => Action = null;
    public void UnregisterFunc() => Func = null;
    public IpcContext? GetContext() => null;

    public void SendMessage(T1 a1, T2 a2, T3 a3, T4 a4)
    {
        object?[] args = [a1, a2, a3, a4];
        Sent.Add(args);
        OnSent?.Invoke(args);
    }
}

/// <summary>The full gate set IpcProvider needs, pre-bundled. Internal because it
/// exposes the internal IpcProvider.Gates type (public would be CS0053).</summary>
internal sealed class FakeIpcGates
{
    public FakeCallGate<object?> Ready { get; } = new();
    public FakeCallGate<object?> Disposing { get; } = new();
    public FakeCallGate<PulsarPlayerData?, object?> PlayerDataChanged { get; } = new();
    public FakeCallGate<bool> IsEnabled { get; } = new();
    public FakeCallGate<PulsarApiVersion> ApiVersion { get; } = new();
    public FakeCallGate<PulsarPlayerData?> GetPlayerData { get; } = new();
    public FakeCallGate<ulong, string, string?, string, object?> SetPlayerData { get; } = new();
    public FakeCallGate<ulong, object?> ClearPlayerData { get; } = new();

    public IpcProvider.Gates Gates =>
        new(Ready, Disposing, PlayerDataChanged, IsEnabled, ApiVersion, GetPlayerData, SetPlayerData, ClearPlayerData);
}
