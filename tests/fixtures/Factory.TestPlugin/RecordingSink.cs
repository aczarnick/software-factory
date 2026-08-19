using Factory.Core;

namespace Factory.TestPlugin;

/// <summary>Takes its options through the provider reference and records each event where the
/// host can see it, so a test can prove a call through the contract reached plugin code.</summary>
[FactoryProvider("recording", Contract = FactoryVersion.ContractVersion)]
public sealed class RecordingSink(ProviderRef reference) : IRunHistorySink
{
    private readonly string _path = reference.Options["path"];

    public void Emit(FactoryEvent evt) => File.AppendAllText(_path, $"{evt.GetType().Name}{Environment.NewLine}");
    public void Flush() { }
}
