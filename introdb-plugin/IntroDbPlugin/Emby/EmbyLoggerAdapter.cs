// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

#if EMBY
using System;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace IntroDbPlugin.Emby;

/// <summary>
/// Adapter to use Emby's ILogManager with Microsoft.Extensions.Logging.ILogger interface.
/// </summary>
/// <typeparam name="T">The type for the logger category.</typeparam>
public class EmbyLoggerAdapter<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly MediaBrowser.Model.Logging.ILogger _embyLogger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbyLoggerAdapter{T}"/> class.
    /// </summary>
    /// <param name="logManager">Emby log manager.</param>
    public EmbyLoggerAdapter(ILogManager logManager)
    {
        if (logManager == null)
        {
            throw new ArgumentNullException(nameof(logManager));
        }

        _embyLogger = logManager.GetLogger(typeof(T).Name);
    }

    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

    /// <inheritdoc />
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception exception,
        Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);

        switch (logLevel)
        {
            case Microsoft.Extensions.Logging.LogLevel.Trace:
            case Microsoft.Extensions.Logging.LogLevel.Debug:
                _embyLogger.Debug(message);
                break;
            case Microsoft.Extensions.Logging.LogLevel.Information:
                _embyLogger.Info(message);
                break;
            case Microsoft.Extensions.Logging.LogLevel.Warning:
                _embyLogger.Warn(message);
                break;
            case Microsoft.Extensions.Logging.LogLevel.Error:
            case Microsoft.Extensions.Logging.LogLevel.Critical:
                if (exception != null)
                {
                    _embyLogger.ErrorException(message, exception);
                }
                else
                {
                    _embyLogger.Error(message);
                }

                break;
            default:
                _embyLogger.Info(message);
                break;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new NullScope();

        public void Dispose()
        {
        }
    }
}
#endif
