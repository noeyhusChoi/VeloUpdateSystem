using System;
using System.Collections.Generic;

namespace Watchdog;

public sealed class RestartLimiter
{
    private readonly Queue<DateTimeOffset> _restartTimes = new();

    public bool TryRegisterRestart(DateTimeOffset now, TimeSpan window, int maxRestarts)
    {
        while (_restartTimes.Count > 0 && now - _restartTimes.Peek() > window)
        {
            _restartTimes.Dequeue();
        }

        if (_restartTimes.Count >= maxRestarts)
        {
            return false;
        }

        _restartTimes.Enqueue(now);
        return true;
    }
}
