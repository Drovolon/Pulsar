using Xunit;

namespace Pulsar.Tests;

// ChatNotifier writes to one process-global fake, just as the plugin writes to one Dalamud chat.
// Keep suites which assert on that sink from racing each other.
[CollectionDefinition(Name)]
public sealed class ChatNotificationCollection
{
    public const string Name = "Chat notifications";
}
