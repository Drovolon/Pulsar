using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

namespace Pulsar.Tests.Fakes;

/// <summary>
/// IFramework whose "framework thread" is whatever thread calls it: callbacks run
/// inline so tests observe their effects synchronously.
/// </summary>
public sealed class FakeFramework : IFramework
{
    public event IFramework.OnUpdateDelegate? Update
    {
        add { }
        remove { }
    }

    public DateTime LastUpdate => DateTime.Now;
    public DateTime LastUpdateUTC => DateTime.UtcNow;
    public TimeSpan UpdateDelta => TimeSpan.Zero;
    public bool IsInFrameworkUpdateThread => true;
    public bool IsFrameworkUnloading => false;

    public TaskFactory GetTaskFactory() => Task.Factory;
    public Task DelayTicks(long numTicks, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task Run(Action action, CancellationToken cancellationToken = default) => RunOnFrameworkThread(action);
    public Task<T> Run<T>(Func<T> func, CancellationToken cancellationToken = default) => RunOnFrameworkThread(func);
    public Task Run(Func<Task> func, CancellationToken cancellationToken = default) => RunOnFrameworkThread(func);
    public Task<T> Run<T>(Func<Task<T>> func, CancellationToken cancellationToken = default) => RunOnFrameworkThread(func);

    public Task<T> RunOnFrameworkThread<T>(Func<T> func)
    {
        try
        {
            return Task.FromResult(func());
        }
        catch (Exception e)
        {
            return Task.FromException<T>(e);
        }
    }

    public Task RunOnFrameworkThread(Action action)
    {
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception e)
        {
            return Task.FromException(e);
        }
    }

    public Task<T> RunOnFrameworkThread<T>(Func<Task<T>> func) => func();
    public Task RunOnFrameworkThread(Func<Task> func) => func();

    public Task<T> RunOnTick<T>(
        Func<T> func, TimeSpan delay = default, int delayTicks = default, CancellationToken cancellationToken = default) =>
        RunOnFrameworkThread(func);

    public Task RunOnTick(
        Action action, TimeSpan delay = default, int delayTicks = default, CancellationToken cancellationToken = default) =>
        RunOnFrameworkThread(action);

    public Task<T> RunOnTick<T>(
        Func<Task<T>> func, TimeSpan delay = default, int delayTicks = default,
        CancellationToken cancellationToken = default) =>
        func();

    public Task RunOnTick(
        Func<Task> func, TimeSpan delay = default, int delayTicks = default,
        CancellationToken cancellationToken = default) =>
        func();

    public IDebouncer CreateDebouncer(TimeSpan delay, Action action) => throw new NotSupportedException("not used in tests");
}

/// <summary>Records the text of everything printed to chat.</summary>
public sealed class RecordingChatGui : IChatGui
{
    private readonly List<string> messages = [];
    private readonly List<SeString> richMessages = [];

    public string[] Messages
    {
        get
        {
            lock (messages)
            {
                return [.. messages];
            }
        }
    }

    public SeString[] RichMessages
    {
        get
        {
            lock (messages)
            {
                return [.. richMessages];
            }
        }
    }

    public void Clear()
    {
        lock (messages)
        {
            messages.Clear();
            richMessages.Clear();
        }
    }

    private void Record(string text)
    {
        lock (messages)
        {
            messages.Add(text);
        }
    }

    private void Record(SeString message)
    {
        lock (messages)
        {
            messages.Add(message.TextValue);
            richMessages.Add(message);
        }
    }

    public void Print(XivChatEntry chat) => Record(chat.Message);
    public void Print(string message, string? messageTag = null, ushort? tagColor = null) => Record(message);
    public void Print(SeString message, string? messageTag = null, ushort? tagColor = null) => Record(message);
    public void PrintError(string message, string? messageTag = null, ushort? tagColor = null) => Record(message);
    public void PrintError(SeString message, string? messageTag = null, ushort? tagColor = null) => Record(message);

    public void Print(ReadOnlySpan<byte> message, string? messageTag = null, ushort? tagColor = null) =>
        Record(System.Text.Encoding.UTF8.GetString(message));

    public void PrintError(ReadOnlySpan<byte> message, string? messageTag = null, ushort? tagColor = null) =>
        Record(System.Text.Encoding.UTF8.GetString(message));

    public event IChatGui.OnHandleableChatMessageDelegate? ChatMessage
    {
        add { }
        remove { }
    }

    public event IChatGui.OnHandleableChatMessageDelegate? CheckMessageHandled
    {
        add { }
        remove { }
    }

    public event IChatGui.OnChatMessageDelegate? ChatMessageHandled
    {
        add { }
        remove { }
    }

    public event IChatGui.OnChatMessageDelegate? ChatMessageUnhandled
    {
        add { }
        remove { }
    }

    public event IChatGui.OnLogMessageDelegate? LogMessage
    {
        add { }
        remove { }
    }

    public uint LastLinkedItemId => 0;
    public byte LastLinkedItemFlags => 0;

    public IReadOnlyDictionary<(string PluginName, uint CommandId), Action<uint, SeString>> RegisteredLinkHandlers { get; } =
        new Dictionary<(string, uint), Action<uint, SeString>>();

    public DalamudLinkPayload AddChatLinkHandler(uint commandId, Action<uint, SeString> commandAction) =>
        throw new NotSupportedException("not used in tests");

    public void RemoveChatLinkHandler(uint commandId) { }
    public void RemoveChatLinkHandler() { }
}
