using System;
using Daml.VisualStudio.Core.Logging;

namespace Daml.VisualStudio.LanguageServer.Messages;

/// <summary>Logs virtual resource notifications; nothing displays them yet.</summary>
public sealed class LoggingVirtualResourceSink : IVirtualResourceSink
{
    private readonly IDamlLogger _logger;

    /// <summary>Creates the sink.</summary>
    public LoggingVirtualResourceSink(IDamlLogger logger) => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public void Changed(VirtualResourceChangedParams change)
    {
        if (change is not null && _logger.IsEnabled(DamlLogLevel.Debug))
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Debug, $"{change.Uri} changed ({change.Contents.Length} characters).");
        }
    }

    /// <inheritdoc />
    public void Noted(VirtualResourceNoteParams note)
    {
        if (note is not null)
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Information, $"{note.Uri}: {note.Note}");
        }
    }

    /// <inheritdoc />
    public void Progressed(VirtualResourceProgressParams progress)
    {
        if (progress is not null && _logger.IsEnabled(DamlLogLevel.Debug))
        {
            _logger.Log(DamlLogCategory.LanguageServer, DamlLogLevel.Debug, $"{progress.Uri} still running after {progress.MillisecondsPassed} ms.");
        }
    }
}
