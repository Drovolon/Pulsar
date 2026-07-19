namespace Pulsar.Tests.Fakes;

internal sealed class FakeGameBgmControl(bool muted = false) : IGameBgmControl
{
    public bool Muted { get; private set; } = muted;
    public bool ReadSucceeds { get; set; } = true;
    public int SetCount { get; private set; }

    public bool TryGetMuted(out bool value)
    {
        value = Muted;
        return ReadSucceeds;
    }

    public void SetMuted(bool value)
    {
        Muted = value;
        SetCount++;
    }

    internal void UserSetsMuted(bool value) => Muted = value;
}
